'use strict';
// Signed mailbox for website updates (site/docs/SITE-UPDATES.md, site/docs/PUBLISH-TO-R2.md).
//
// The owner's PC signs a small JSON description of a deploy package and either uploads the zip itself (schema 1, PUT)
// or, since Cloudflare Free refuses request bodies over 100 MB, stages the zip in the R2 bucket and sends only the
// signed description with a time-limited download URL (schema 2, POST): this service then downloads the zip and checks
// it against the signed size and SHA-256 exactly like an upload. This module runs on its OWN loopback listener (default
// 127.0.0.1:3011, long request timeout), proxied by Caddy at https://<domain>/api/site-update only. It never installs
// anything: it stores the zip in <dataDir>\site-update\inbox\ and the elevated Update-Site.ps1 (scheduled task or
// desktop shortcut) re-verifies and deploys it.
//
// Every route lives on the ONE path Caddy proxies (/api/site-update); the method and the query tell them apart:
//
//   PUT  /api/site-update            schema 1: headers X-LotR-Update-Meta (base64url of the exact UTF-8 meta JSON) and
//                                    X-LotR-Update-Signature (ECDSA P-256 / SHA-256, IEEE-P1363, base64url), body = zip
//                                    -> 201 { id } | 401 bad signature | 400 | 404 disabled | 409 duplicate/busy | 413 | 429 | 507
//   POST /api/site-update            schema 2: the same two headers, EMPTY body; the meta carries "url" (https, a host of
//                                    the allow list: the account's R2 S3 endpoint by default). The service downloads it.
//                                    -> 201 { id } stored | 202 { id, state: "downloading" } still running after
//                                    envelopeWaitSec | 502 download_failed | the same 4xx as PUT
//   GET  /api/site-update?id=<id>    delivery state of a POST: { id, state: downloading|stored|installed|failed|unknown }
//   GET  /api/site-update?file=<rel> one file of <root>\files\releases (pending.json, channels.json, <id>/manifest.json,
//                                    manifest.json.sig, security.json, <id>/objects/<ab>/<name>) for the team PC that
//                                    pushes a release to R2. Signed read token in the same two headers (meta kind
//                                    "lotrmp-release-read", valid readMaxAgeHours). Players never come here.
//   GET  /status                     loopback only, never through the proxy -> { enabled, fingerprint, pending, busy }
//
// PUT: the signature is checked on the headers BEFORE a single body byte is read (Expect: 100-continue is honoured), so
// an unsigned upload never touches the disk. POST: nothing is downloaded before the signature, the meta, the URL host,
// the size, the free disk and the duplicate checks pass. Disabled (404) while the public key file is missing.
//
// CLI (defence in depth, used by Update-Site.ps1):
//   node src/site-update.js verify --zip <file.zip> --meta <inbox entry .json> --pub <public key .pem>   exit 0 | 1
//   node src/site-update.js fingerprint --pub <public key .pem>
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { Readable, Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const { clientIp, isLoopback, isProxied, sendJson, HttpError } = require('./http-util');
const { RateLimiter } = require('./support');
const { silentLogger } = require('./log');

const KIND = 'lotrmp-site-update';
const READ_KIND = 'lotrmp-release-read';
const SCHEMA = 1; // direct upload (PUT)
const SCHEMA_URL = 2; // signed envelope with a download URL (POST)
const META_HEADER = 'x-lotr-update-meta';
const SIG_HEADER = 'x-lotr-update-signature';
const ROUTE = '/api/site-update';
const ID_PATTERN = /^\d{8}-\d{6}-[0-9a-f]{8}$/;
const SHA_PATTERN = /^[0-9a-f]{64}$/;
const B64URL = /^[A-Za-z0-9_-]+$/;
// The account's R2 S3 endpoint (<32 hex account id>.r2.cloudflarestorage.com): where Team-Publish stages a package and
// presigns its URL. Other hosts only through siteUpdate.downloadHosts.
const R2_HOST = /^[0-9a-f]{32}\.r2\.cloudflarestorage\.com$/;
// Files of files\releases a signed read may fetch. Nothing else is reachable, whatever the token says.
const RELEASE_FILE = /^(?:pending\.json|channels\.json|(\d{8}-\d{6}-[0-9a-f]{8})\/(?:manifest\.json|manifest\.json\.sig|security\.json|objects\/([0-9a-f]{2})\/([0-9a-f]{64})\.(?:zst|raw)))$/;
const utf8 = new TextDecoder('utf-8', { fatal: true });

// Defaults of the settings added with the R2 delivery (config.js predates them; a config file may override each).
const SITE_UPDATE_DEFAULTS = Object.freeze({
    // Extra download hosts for schema 2 (exact names, https only). The R2 S3 endpoint is always allowed.
    downloadHosts: [],
    // Tests only: http://127.0.0.1|localhost|[::1] URLs are accepted. Never on the server.
    allowLoopbackHttpDownload: false,
    // How long a POST waits for its download before answering 202 (Cloudflare cuts a proxied request at 100 s).
    envelopeWaitSec: 60,
    // A signed read token is valid this long after its createdAt.
    readMaxAgeHours: 12,
    // Refused read attempts (bad or missing token) one address may make per hour.
    readFailuresPerIpPerHour: 30,
});

// ---------------------------------------------------------------------------------------------------------------
// Crypto helpers (shared with the CLI; site/scripts/site-update.mjs and r2-sync.mjs implement the signing side)
// ---------------------------------------------------------------------------------------------------------------
function b64urlDecode(text) {
    const s = String(text || '').trim();
    if (!s || !B64URL.test(s)) return null;
    return Buffer.from(s, 'base64url');
}

// Public key from a PEM file: { key } | { missing: true } | { error }. Only ECDSA P-256 keys are accepted.
function loadPublicKey(file) {
    let pem;
    try {
        pem = fs.readFileSync(file, 'utf8');
    } catch (error) {
        if (error.code === 'ENOENT') return { missing: true };
        return { error: `${file}: ${error.code || error.message}` };
    }
    try {
        const key = crypto.createPublicKey(pem);
        if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
            return { error: `${file}: not an ECDSA P-256 public key` };
        }
        return { key };
    } catch (error) {
        return { error: `${file}: ${error.message}` };
    }
}

// SHA-256 of the SubjectPublicKeyInfo DER, hex in groups of 4 (compare it with what init-key printed on the PC).
function fingerprint(key) {
    const der = key.export({ type: 'spki', format: 'der' });
    return crypto.createHash('sha256').update(der).digest('hex').match(/.{4}/g).join(' ');
}

function verifySignature(metaBytes, signatureB64url, key) {
    const sig = b64urlDecode(signatureB64url);
    if (!sig || sig.length !== 64) return false;
    try {
        return crypto.verify('sha256', metaBytes, { key, dsaEncoding: 'ieee-p1363' }, sig);
    } catch {
        return false;
    }
}

// Structure of a schema 2 download URL. The host allow list is checked separately (checkDownloadUrl).
function parseDownloadUrl(text) {
    if (typeof text !== 'string' || text.length < 10 || text.length > 4096 || /[\s\u0000-\u001f]/.test(text)) return null;
    let u;
    try { u = new URL(text); } catch { return null; }
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return null;
    if (u.username || u.password || u.hash) return null;
    return u;
}

// Validates the parsed meta. Returns a list of problems (empty = fine). Time checks are separate (checkFreshness).
function validateMeta(meta) {
    const p = [];
    if (!meta || typeof meta !== 'object' || Array.isArray(meta)) return ['meta must be a JSON object'];
    if (meta.kind !== KIND) p.push(`kind must be "${KIND}"`);
    if (meta.schema !== SCHEMA && meta.schema !== SCHEMA_URL) p.push(`schema must be ${SCHEMA} or ${SCHEMA_URL}`);
    if (typeof meta.name !== 'string' || !/^[\w.() -]{1,120}\.zip$/i.test(meta.name)) p.push('name: a .zip file name (letters, digits, . _ - space, at most 120)');
    if (typeof meta.version !== 'string' || meta.version.length < 1 || meta.version.length > 120 || /[\u0000-\u001f]/.test(meta.version)) p.push('version: 1-120 printable characters');
    if (typeof meta.sha256 !== 'string' || !SHA_PATTERN.test(meta.sha256)) p.push('sha256: 64 lowercase hex characters');
    if (!Number.isSafeInteger(meta.size) || meta.size < 1) p.push('size: a positive integer');
    if (typeof meta.createdAt !== 'string' || !Number.isFinite(Date.parse(meta.createdAt))) p.push('createdAt: an ISO date');
    if (meta.notes !== undefined && meta.notes !== null && (typeof meta.notes !== 'string' || meta.notes.length > 1000)) p.push('notes: text, at most 1000 characters');
    if (meta.schema === SCHEMA_URL) {
        if (!parseDownloadUrl(meta.url)) p.push('url: an http(s) URL without credentials, at most 4096 characters');
    } else if (meta.url !== undefined) {
        p.push(`url: only in schema ${SCHEMA_URL}`);
    }
    return p;
}

// Replay protection: a signed meta is valid for maxAgeDays after createdAt, and not more than maxFutureMinutes ahead.
function checkFreshness(meta, nowMs, { maxAgeDays = 7, maxFutureMinutes = 60 } = {}) {
    const t = Date.parse(meta.createdAt);
    if (t < nowMs - maxAgeDays * 86400000) return `createdAt ${meta.createdAt} is older than ${maxAgeDays} days: sign the package again`;
    if (t > nowMs + maxFutureMinutes * 60000) return `createdAt ${meta.createdAt} is in the future: check the clock of the PC that signed it`;
    return null;
}

// Where a schema 2 package may be downloaded from. Returns null when allowed, else the reason.
function checkDownloadUrl(urlText, { downloadHosts = [], allowLoopbackHttpDownload = false } = {}) {
    const u = parseDownloadUrl(urlText);
    if (!u) return 'not a valid URL';
    const host = u.hostname.toLowerCase();
    const loopback = host === '127.0.0.1' || host === 'localhost' || host === '[::1]';
    if (allowLoopbackHttpDownload && loopback) return null;
    if (u.protocol !== 'https:') return 'https only';
    if (u.port && u.port !== '443') return 'default https port only';
    if (R2_HOST.test(host)) return null;
    if (downloadHosts.map((h) => String(h).toLowerCase()).includes(host)) return null;
    return `host ${host} is not allowed (R2 S3 endpoint or siteUpdate.downloadHosts)`;
}

// Streams a file through SHA-256. Resolves { sha256, size }.
function hashFile(file) {
    return new Promise((resolve, reject) => {
        const h = crypto.createHash('sha256');
        let size = 0;
        fs.createReadStream(file)
            .on('data', (c) => { h.update(c); size += c.length; })
            .on('error', reject)
            .on('end', () => resolve({ sha256: h.digest('hex'), size }));
    });
}

// Full check of a stored update: signature over metaRaw, meta fields, size and SHA-256 of the zip.
// `envelope` is an inbox entry ({ metaRaw, signature, ... }). Never throws: { ok, problems, meta, fingerprint }.
// Schema 1 (uploaded) and schema 2 (downloaded) entries are checked the same way: the URL only said where to fetch.
async function verifyEnvelope({ zip, envelope, publicKeyFile }) {
    const problems = [];
    const k = loadPublicKey(publicKeyFile);
    if (!k.key) return { ok: false, problems: [k.missing ? `public key not found: ${publicKeyFile}` : k.error] };
    const fp = fingerprint(k.key);
    if (!envelope || typeof envelope.metaRaw !== 'string' || typeof envelope.signature !== 'string') {
        return { ok: false, fingerprint: fp, problems: ['the entry has no metaRaw/signature'] };
    }
    const metaBytes = Buffer.from(envelope.metaRaw, 'utf8');
    if (!verifySignature(metaBytes, envelope.signature, k.key)) problems.push('signature does not match the public key');
    let meta = null;
    try { meta = JSON.parse(envelope.metaRaw); } catch { problems.push('metaRaw is not JSON'); }
    if (meta) problems.push(...validateMeta(meta));
    if (meta && problems.length === 0) {
        let actual;
        try {
            actual = await hashFile(zip);
        } catch (error) {
            return { ok: false, fingerprint: fp, meta, problems: [`cannot read ${zip}: ${error.code || error.message}`] };
        }
        if (actual.size !== meta.size) problems.push(`size ${actual.size} differs from the signed ${meta.size}`);
        if (actual.sha256 !== meta.sha256) problems.push(`sha256 ${actual.sha256} differs from the signed ${meta.sha256}`);
    }
    return { ok: problems.length === 0, problems, meta, fingerprint: fp };
}

function stampOf(ms) {
    return new Date(ms).toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
}

function writeJsonAtomic(file, value) {
    const tmp = `${file}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, `${JSON.stringify(value, null, 2)}\n`);
    fs.renameSync(tmp, file);
}

function readJsonSafe(file) {
    try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, '')); } catch { return null; }
}

// Only the scheme, host and path of a download URL go to the log: the query of a presigned URL is a bearer credential.
function redactUrl(text) {
    try { const u = new URL(text); return `${u.protocol}//${u.host}${u.pathname}`; } catch { return '(unparsable)'; }
}

// ---------------------------------------------------------------------------------------------------------------
// Receiver
// ---------------------------------------------------------------------------------------------------------------
// Resolves true when a request with no declared length ends without a single byte (within 10 s).
// The first byte settles it: the rest is never read, the socket is not kept for a stray body.
function bodyIsEmpty(req, timeoutMs = 10_000) {
    return new Promise((resolve) => {
        let settled = false;
        const finish = (empty) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            req.off('data', onData);
            req.off('end', onEnd);
            req.off('error', onError);
            if (!empty) req.pause();
            resolve(empty);
        };
        const onData = (chunk) => { if (chunk.length > 0) finish(false); };
        const onEnd = () => finish(true);
        const onError = () => finish(false);
        const timer = setTimeout(() => finish(false), timeoutMs);
        timer.unref?.();
        req.on('data', onData);
        req.on('end', onEnd);
        req.on('error', onError);
    });
}

function createSiteUpdate(config, { logger = silentLogger, now = () => Date.now(), fetchImpl = (...a) => fetch(...a) } = {}) {
    const su = { ...SITE_UPDATE_DEFAULTS, ...config.siteUpdate };
    const log = logger;
    const inbox = path.join(su.dir, 'inbox');
    const installed = path.join(su.dir, 'installed');
    const releasesDir = config.queue?.releasesDir ?? path.join(config.root, 'files', 'releases');
    const limiter = new RateLimiter({ limit: su.perIpPerHour, windowMs: 3600000, now });
    const readFailures = new RateLimiter({ limit: su.readFailuresPerIpPerHour, windowMs: 3600000, now });
    let busy = null;
    // Downloads started by POST, by id (the last 20): what GET ?id= reports while and after they run.
    const jobs = new Map();

    // SHA-256 of every update already received or installed (inbox entries, installed entries, installed history).
    function knownHashes() {
        const out = new Map();
        for (const dir of [inbox, installed]) {
            let names = [];
            try { names = fs.readdirSync(dir); } catch { continue; }
            for (const n of names) {
                if (!n.endsWith('.json') || !ID_PATTERN.test(n.slice(0, -5))) continue;
                const e = readJsonSafe(path.join(dir, n));
                const sha = e?.meta?.sha256;
                if (typeof sha === 'string') out.set(sha, dir === inbox ? 'pending' : 'installed');
            }
        }
        try {
            for (const line of fs.readFileSync(path.join(installed, 'history.jsonl'), 'utf8').split('\n')) {
                if (!line.trim()) continue;
                try { const h = JSON.parse(line); if (h.sha256) out.set(h.sha256, 'installed'); } catch { /* torn line */ }
            }
        } catch { /* no history yet */ }
        return out;
    }

    function pendingIds() {
        let names = [];
        try { names = fs.readdirSync(inbox); } catch { return []; }
        // Oldest first: by file time (ids also start with the reception time, they break ties).
        const mtime = (n) => { try { return fs.statSync(path.join(inbox, n)).mtimeMs; } catch { return 0; } };
        return names.filter((n) => n.endsWith('.zip') && ID_PATTERN.test(n.slice(0, -4)))
            .map((n) => ({ id: n.slice(0, -4), t: mtime(n) }))
            .sort((x, y) => x.t - y.t || (x.id < y.id ? -1 : 1))
            .map((x) => x.id);
    }

    // Keeps the newest keepPending updates in the inbox.
    function prunePending() {
        const ids = pendingIds();
        for (const id of ids.slice(0, Math.max(0, ids.length - su.keepPending))) {
            for (const ext of ['.zip', '.json', '.result.json']) {
                try { fs.unlinkSync(path.join(inbox, id + ext)); } catch { /* already gone */ }
            }
            log.info('siteupdate.pruned', { id });
        }
    }

    function cleanParts() {
        let names = [];
        try { names = fs.readdirSync(inbox); } catch { return; }
        for (const n of names) {
            if (n.endsWith('.part')) { try { fs.unlinkSync(path.join(inbox, n)); } catch { /* in use */ } }
        }
    }

    function freeBytes(dir) {
        try {
            const s = fs.statfsSync(dir);
            return Number(s.bavail) * Number(s.bsize);
        } catch {
            return Infinity; // unknown: do not block
        }
    }

    // 1-3 of every signed request: key present, rate limit (when given), signature over the exact meta bytes, JSON.
    // Returns { key, metaRaw, meta, sig }. Nothing in the meta is believed before the signature matched.
    function checkSigned(req, ip, rateLimiter) {
        const k = loadPublicKey(su.publicKeyFile);
        if (k.missing) throw new HttpError(404, 'not_found');
        if (!k.key) {
            log.error('siteupdate.key.unreadable', { error: k.error });
            throw new HttpError(503, 'key_unreadable', 'the server cannot read its public key');
        }
        if (rateLimiter) {
            const wait = rateLimiter.retryAfter(ip);
            if (wait > 0) {
                log.warn('siteupdate.rate_limited', { ip });
                throw new HttpError(429, 'rate_limited', 'too many requests from this address', { retryAfter: wait });
            }
            rateLimiter.record(ip);
        }
        const metaBytes = b64urlDecode(req.headers[META_HEADER]);
        const sig = req.headers[SIG_HEADER];
        if (!metaBytes || metaBytes.length > 8192 || typeof sig !== 'string') {
            throw new HttpError(401, 'unsigned', `headers ${META_HEADER} and ${SIG_HEADER} are required`);
        }
        if (!verifySignature(metaBytes, sig, k.key)) {
            throw new HttpError(401, 'bad_signature', 'the signature does not match the server key');
        }
        let metaRaw;
        let meta;
        try {
            metaRaw = utf8.decode(metaBytes);
            meta = JSON.parse(metaRaw);
        } catch {
            throw new HttpError(400, 'bad_meta', 'meta is not UTF-8 JSON');
        }
        return { key: k.key, metaRaw, meta, sig: sig.trim() };
    }

    // 4. Checks shared by PUT and POST once the signature matched: meta, schema, replay, size, duplicate, busy, disk.
    function admit(signedReq, ip, schema) {
        const { meta } = signedReq;
        const problems = validateMeta(meta);
        if (problems.length) throw new HttpError(400, 'bad_meta', problems.join('; '));
        if (meta.schema !== schema) {
            throw new HttpError(400, 'bad_meta', schema === SCHEMA ? `PUT takes schema ${SCHEMA} (the zip in the body); a schema ${SCHEMA_URL} envelope goes with POST` : `POST takes schema ${SCHEMA_URL} (a signed envelope with "url")`);
        }
        const stale = checkFreshness(meta, now(), su);
        if (stale) {
            log.warn('siteupdate.rejected', { ip, reason: 'stale', createdAt: meta.createdAt });
            throw new HttpError(400, 'stale', stale);
        }
        if (meta.size > su.maxBytes) throw new HttpError(413, 'too_large', `the package is larger than ${su.maxBytes} bytes`);
        fs.mkdirSync(inbox, { recursive: true });
        const known = knownHashes().get(meta.sha256);
        if (known) {
            log.info('siteupdate.duplicate', { ip, sha256: meta.sha256.slice(0, 12), state: known });
            throw new HttpError(409, 'duplicate', `this package was already received (${known})`);
        }
        if (busy) throw new HttpError(409, 'busy', 'another upload is in progress', { since: new Date(busy.at).toISOString() });
        const free = freeBytes(inbox);
        if (free < meta.size + su.minFreeBytes) {
            log.error('siteupdate.disk_full', { free, size: meta.size });
            throw new HttpError(507, 'insufficient_storage', 'not enough free disk space on the server');
        }
    }

    // 5. A body (upload or download) to <id>.part, counted and hashed on the way, renamed to <id>.zip only when it
    // matches the signed size and SHA-256, then the inbox entry. Throws HttpError; never leaves a .part behind.
    async function store(source, { id, signedReq, ip, delivery, onAbort }) {
        const { meta, metaRaw, sig, key } = signedReq;
        const part = path.join(inbox, `${id}.part`);
        const started = Date.now();
        const hash = crypto.createHash('sha256');
        let count = 0;
        const meter = new Transform({
            transform(chunk, _enc, done) {
                count += chunk.length;
                if (count > meta.size) return done(new HttpError(400, 'too_long', 'body longer than the signed size'));
                hash.update(chunk);
                return done(null, chunk);
            },
        });
        try {
            await pipeline(source, meter, fs.createWriteStream(part, { flags: 'wx' }));
        } catch (error) {
            try { fs.unlinkSync(part); } catch { /* not created */ }
            if (error instanceof HttpError) throw error;
            log.warn('siteupdate.aborted', { id, ip, bytes: count, error: error.code || error.message });
            throw onAbort(error);
        }
        const sha = hash.digest('hex');
        if (count !== meta.size || sha !== meta.sha256) {
            try { fs.unlinkSync(part); } catch { /* ignore */ }
            log.warn('siteupdate.rejected', { id, ip, reason: 'sha_mismatch', bytes: count });
            throw new HttpError(400, 'sha_mismatch', count !== meta.size ? `received ${count} bytes, signed size ${meta.size}` : 'the body does not match the signed sha256');
        }
        fs.renameSync(part, path.join(inbox, `${id}.zip`));
        writeJsonAtomic(path.join(inbox, `${id}.json`), {
            id, meta, metaRaw, signature: sig, receivedAt: new Date(now()).toISOString(), ip, fingerprint: fingerprint(key), delivery,
        });
        prunePending();
        const sec = Math.max(0.001, (Date.now() - started) / 1000);
        log.info('siteupdate.stored', { id, ip, name: meta.name, version: meta.version, size: meta.size, sha256: meta.sha256.slice(0, 12), delivery: delivery.method, seconds: Math.round(sec), mbps: Math.round((meta.size / 1048576 / sec) * 10) / 10 });
    }

    // PUT: the zip in the body (schema 1).
    async function receive(req, res, ip, expectContinue) {
        let signedReq;
        try {
            signedReq = checkSigned(req, ip, limiter);
        } catch (error) {
            if (error instanceof HttpError && error.status === 401) log.warn('siteupdate.rejected', { ip, reason: error.code });
            throw error;
        }
        admit(signedReq, ip, SCHEMA);
        const { meta } = signedReq;
        const declared = req.headers['content-length'];
        if (declared !== undefined && Number(declared) !== meta.size) throw new HttpError(400, 'length_mismatch', `Content-Length ${declared} differs from the signed size ${meta.size}`);

        const id = `${stampOf(now())}-${meta.sha256.slice(0, 8)}`;
        busy = { id, at: now(), ip };
        try {
            log.info('siteupdate.receiving', { id, ip, name: meta.name, size: meta.size });
            if (expectContinue) res.writeContinue();
            await store(req, { id, signedReq, ip, delivery: { method: 'upload' }, onAbort: () => new HttpError(400, 'aborted', 'upload interrupted') });
            return sendJson(res, 201, { id, name: meta.name, version: meta.version, size: meta.size, sha256: meta.sha256 });
        } finally {
            busy = null;
        }
    }

    function rememberJob(job) {
        jobs.set(job.id, job);
        while (jobs.size > 20) jobs.delete(jobs.keys().next().value);
    }

    // The download of a schema 2 envelope. Resolves when stored, rejects with an HttpError.
    async function download(signedReq, { id, ip }) {
        const { meta } = signedReq;
        const host = new URL(meta.url).host;
        const deadline = AbortSignal.timeout(su.requestTimeoutSec * 1000);
        let r;
        try {
            r = await fetchImpl(meta.url, { redirect: 'error', signal: deadline, headers: { 'user-agent': 'lotr-services/site-update', accept: 'application/zip, application/octet-stream' } });
        } catch (error) {
            log.warn('siteupdate.download.failed', { id, host, error: error.cause?.code || error.name || error.message });
            throw new HttpError(502, 'download_failed', `could not download the package from ${host}: ${error.cause?.code || error.name || error.message}`);
        }
        if (r.status !== 200 || !r.body) {
            try { await r.body?.cancel(); } catch { /* ignore */ }
            log.warn('siteupdate.download.failed', { id, host, status: r.status });
            throw new HttpError(502, 'download_failed', `${host} answered HTTP ${r.status} (expired or deleted URL?)`);
        }
        const declared = r.headers.get('content-length');
        if (declared !== null && Number(declared) !== meta.size) {
            try { await r.body.cancel(); } catch { /* ignore */ }
            throw new HttpError(400, 'length_mismatch', `${host} announces ${declared} bytes, signed size ${meta.size}`);
        }
        log.info('siteupdate.downloading', { id, ip, name: meta.name, size: meta.size, from: redactUrl(meta.url) });
        await store(Readable.fromWeb(r.body), {
            id, signedReq, ip, delivery: { method: 'download', host },
            onAbort: (error) => new HttpError(502, 'download_failed', `download from ${host} interrupted: ${error.code || error.name || error.message}`),
        });
    }

    // POST: a signed envelope (schema 2), the service downloads the zip itself.
    async function receiveEnvelope(req, res, ip) {
        let signedReq;
        try {
            signedReq = checkSigned(req, ip, limiter);
        } catch (error) {
            if (error instanceof HttpError && error.status === 401) log.warn('siteupdate.rejected', { ip, reason: error.code });
            throw error;
        }
        const declared = req.headers['content-length'];
        if (declared !== undefined && Number(declared) !== 0) {
            throw new HttpError(400, 'body_not_empty', 'a signed envelope (POST) has no body; the package goes with PUT or through its URL');
        }
        // A proxy chain (Cloudflare edge -> cloudflared -> Caddy) may turn an empty POST into a
        // chunked one with zero bytes: accept that, refuse as soon as one byte of body arrives.
        if (req.headers['transfer-encoding'] && !(await bodyIsEmpty(req))) {
            throw new HttpError(400, 'body_not_empty', 'a signed envelope (POST) has no body; the package goes with PUT or through its URL');
        }
        admit(signedReq, ip, SCHEMA_URL);
        const { meta } = signedReq;
        const refused = checkDownloadUrl(meta.url, su);
        if (refused) {
            log.warn('siteupdate.rejected', { ip, reason: 'download_host', detail: refused });
            throw new HttpError(400, 'bad_url', `url: ${refused}`);
        }
        const id = `${stampOf(now())}-${meta.sha256.slice(0, 8)}`;
        busy = { id, at: now(), ip };
        const job = { id, state: 'downloading', error: null, message: null, startedAt: now(), name: meta.name, version: meta.version, size: meta.size };
        rememberJob(job);
        job.done = download(signedReq, { id, ip }).then(
            () => { job.state = 'stored'; },
            (error) => {
                job.state = 'failed';
                job.error = error instanceof HttpError ? error.code : 'internal';
                job.message = error instanceof HttpError ? error.message : 'internal error';
                job.status = error instanceof HttpError ? error.status : 500;
                if (!(error instanceof HttpError)) log.error('siteupdate.download.crashed', { id, error: error.stack || error.message });
            },
        ).finally(() => { if (busy?.id === id) busy = null; });

        let timer;
        const waited = await Promise.race([
            job.done.then(() => true),
            new Promise((resolve) => { timer = setTimeout(() => resolve(false), su.envelopeWaitSec * 1000); timer.unref?.(); }),
        ]);
        clearTimeout(timer);
        const body = { id, name: meta.name, version: meta.version, size: meta.size, sha256: meta.sha256 };
        if (!waited) return sendJson(res, 202, { ...body, state: 'downloading' });
        if (job.state === 'stored') return sendJson(res, 201, { ...body, state: 'stored' });
        throw new HttpError(job.status ?? 502, job.error ?? 'download_failed', job.message ?? 'download failed', { id });
    }

    // GET ?id=: state of a delivery (never secret: the id only says when a package with that hash prefix arrived).
    function deliveryState(res, id) {
        if (!ID_PATTERN.test(id)) throw new HttpError(400, 'bad_id', 'id like 20261018-153000-ab12cd34');
        const job = jobs.get(id);
        if (job && job.state !== 'stored') return sendJson(res, 200, { id, state: job.state, error: job.error, message: job.message });
        if (fs.existsSync(path.join(inbox, `${id}.zip`))) return sendJson(res, 200, { id, state: 'stored' });
        if (fs.existsSync(path.join(installed, `${id}.json`))) return sendJson(res, 200, { id, state: 'installed' });
        if (job) return sendJson(res, 200, { id, state: 'stored' }); // pruned from the inbox since
        return sendJson(res, 200, { id, state: 'unknown' });
    }

    // GET ?file=: one file of files\releases for the team PC (signed read token, site update key).
    async function readReleaseFile(req, res, ip, rel) {
        const wait = readFailures.retryAfter(ip);
        if (wait > 0) throw new HttpError(429, 'rate_limited', 'too many refused reads from this address', { retryAfter: wait });
        let signedReq;
        try {
            signedReq = checkSigned(req, ip, null);
            const { meta } = signedReq;
            if (!meta || meta.kind !== READ_KIND || meta.schema !== 1 || typeof meta.createdAt !== 'string' || !Number.isFinite(Date.parse(meta.createdAt))) {
                throw new HttpError(400, 'bad_meta', `a read token is { kind: "${READ_KIND}", schema: 1, createdAt }`);
            }
            const stale = checkFreshness(meta, now(), { maxAgeDays: su.readMaxAgeHours / 24, maxFutureMinutes: su.maxFutureMinutes });
            if (stale) throw new HttpError(401, 'stale', stale);
        } catch (error) {
            if (error instanceof HttpError && error.status !== 404 && error.status !== 503) {
                readFailures.record(ip);
                log.warn('siteupdate.read.rejected', { ip, reason: error.code });
            }
            throw error;
        }
        const m = RELEASE_FILE.exec(rel);
        if (!m || (m[2] && !m[3].startsWith(m[2]))) throw new HttpError(400, 'bad_file', 'not a release file');
        const file = path.join(releasesDir, ...rel.split('/'));
        let st;
        try { st = fs.statSync(file); } catch { st = null; }
        if (!st?.isFile()) throw new HttpError(404, 'no_such_file', `${rel} is not on the server`);
        if (!m[2]) log.info('siteupdate.read', { ip, file: rel, size: st.size });
        const type = rel.endsWith('.json') ? 'application/json; charset=utf-8' : 'application/octet-stream';
        res.writeHead(200, { 'Content-Type': type, 'Content-Length': st.size, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
        await pipeline(fs.createReadStream(file), res);
        return undefined;
    }

    async function handle(req, res, { expectContinue = false } = {}) {
        const url = new URL(req.url, 'http://lotr-services.local');
        const ip = clientIp(req, config.trustForwardedFor);
        try {
            if (req.method === 'GET' && url.pathname === '/status') {
                if (!isLoopback(req.socket.remoteAddress) || isProxied(req)) throw new HttpError(404, 'not_found');
                const k = loadPublicKey(su.publicKeyFile);
                return sendJson(res, 200, {
                    enabled: Boolean(k.key), fingerprint: k.key ? fingerprint(k.key) : null, keyError: k.error || null,
                    pending: pendingIds(), busy: busy ? { id: busy.id, since: new Date(busy.at).toISOString() } : null,
                });
            }
            if (url.pathname === ROUTE && req.method === 'PUT') return await receive(req, res, ip, expectContinue);
            if (url.pathname === ROUTE && req.method === 'POST') return await receiveEnvelope(req, res, ip);
            if (url.pathname === ROUTE && req.method === 'GET') {
                if (!loadPublicKey(su.publicKeyFile).key) throw new HttpError(404, 'not_found');
                if (url.searchParams.has('id')) return deliveryState(res, url.searchParams.get('id'));
                if (url.searchParams.has('file')) return await readReleaseFile(req, res, ip, url.searchParams.get('file'));
                throw new HttpError(400, 'bad_request', 'GET takes ?id=<update id> or ?file=<release file>');
            }
            if (url.pathname === ROUTE) throw new HttpError(405, 'method_not_allowed', 'PUT, POST or GET');
            throw new HttpError(404, 'not_found');
        } catch (error) {
            // Nothing more is read from this request: tell the client and close the connection after the answer.
            const headers = { Connection: 'close' };
            if (error instanceof HttpError) {
                if (error.extra?.retryAfter) headers['Retry-After'] = String(error.extra.retryAfter);
                if (!res.headersSent) sendJson(res, error.status, { error: error.code, message: error.message, ...error.extra }, headers);
                return undefined;
            }
            log.error('siteupdate.failed', { error: error.stack || error.message });
            if (!res.headersSent) sendJson(res, 500, { error: 'internal' }, headers);
            else res.destroy();
            return undefined;
        }
    }

    const server = http.createServer((req, res) => {
        handle(req, res).catch(() => { if (!res.headersSent) sendJson(res, 500, { error: 'internal' }); });
    });
    // Expect: 100-continue: the headers are judged first, the client sends the body only after "100 Continue".
    server.on('checkContinue', (req, res) => {
        handle(req, res, { expectContinue: true }).catch(() => { if (!res.headersSent) sendJson(res, 500, { error: 'internal' }); });
    });
    server.requestTimeout = su.requestTimeoutSec * 1000;
    server.headersTimeout = 15000;
    server.keepAliveTimeout = 5000;

    return {
        server,
        handle,
        status: () => ({ busy, pending: pendingIds() }),
        // Tests: the download of a POST, by id (resolves when it ended, whatever the result).
        job: (id) => jobs.get(id) ?? null,
        start() {
            cleanParts();
            return new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(su.port, config.host, () => {
                    server.removeListener('error', reject);
                    const addr = server.address();
                    const k = loadPublicKey(su.publicKeyFile);
                    log.info('siteupdate.started', { host: addr.address, port: addr.port, enabled: Boolean(k.key), fingerprint: k.key ? fingerprint(k.key) : null, dir: su.dir, releasesDir });
                    resolve(addr);
                });
            });
        },
        async stop() {
            await new Promise((resolve) => {
                if (!server.listening) { resolve(); return; }
                server.close(() => resolve());
                server.closeAllConnections?.();
            });
            // A download still running ends on its own deadline; tests wait for theirs explicitly.
        },
    };
}

// ---------------------------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------------------------
function argValue(argv, name) {
    const i = argv.indexOf(name);
    return i >= 0 && argv[i + 1] ? argv[i + 1] : null;
}

async function cli(argv) {
    const cmd = argv[0];
    if (cmd === 'fingerprint') {
        const pub = argValue(argv, '--pub');
        const k = pub ? loadPublicKey(pub) : { error: '--pub <file> is required' };
        if (!k.key) { process.stderr.write(`${k.missing ? `not found: ${pub}` : k.error}\n`); return 1; }
        process.stdout.write(`${fingerprint(k.key)}\n`);
        return 0;
    }
    if (cmd === 'verify') {
        const zip = argValue(argv, '--zip');
        const metaFile = argValue(argv, '--meta');
        const pub = argValue(argv, '--pub');
        if (!zip || !metaFile || !pub) { process.stderr.write('usage: verify --zip <file.zip> --meta <entry.json> --pub <key.pem>\n'); return 1; }
        const envelope = readJsonSafe(metaFile);
        const r = await verifyEnvelope({ zip, envelope, publicKeyFile: pub });
        const delivery = envelope?.delivery?.method === 'download' ? `download from ${envelope.delivery.host}` : 'upload';
        process.stdout.write(`${JSON.stringify({ ok: r.ok, problems: r.problems, fingerprint: r.fingerprint || null, name: r.meta?.name ?? null, version: r.meta?.version ?? null, sha256: r.meta?.sha256 ?? null, size: r.meta?.size ?? null, createdAt: r.meta?.createdAt ?? null, schema: r.meta?.schema ?? null, delivery })}\n`);
        return r.ok ? 0 : 1;
    }
    process.stderr.write('usage: node src/site-update.js verify --zip <file.zip> --meta <entry.json> --pub <key.pem>\n       node src/site-update.js fingerprint --pub <key.pem>\n');
    return 1;
}

if (require.main === module) {
    cli(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => {
        process.stderr.write(`${error.stack || error.message}\n`);
        process.exitCode = 1;
    });
}

module.exports = {
    createSiteUpdate, verifyEnvelope, verifySignature, validateMeta, checkFreshness, checkDownloadUrl, loadPublicKey, fingerprint, hashFile, cli,
    KIND, READ_KIND, SCHEMA, SCHEMA_URL, META_HEADER, SIG_HEADER, ROUTE, ID_PATTERN, SITE_UPDATE_DEFAULTS,
};
