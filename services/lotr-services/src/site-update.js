'use strict';
// Signed mailbox for website updates (site/docs/SITE-UPDATES.md).
//
// The owner's PC signs a small JSON description of a deploy package (site/scripts/site-update.mjs send) and uploads
// the zip with an HTTP PUT. This module receives it on its OWN loopback listener (default 127.0.0.1:3011, long
// request timeout), proxied by Caddy at https://<domain>/api/site-update only. It never installs anything: it stores
// the zip in <dataDir>\site-update\inbox\ and the elevated Update-Site.ps1 (scheduled task or desktop shortcut)
// re-verifies and deploys it.
//
//   PUT /api/site-update   headers X-LotR-Update-Meta (base64url of the exact UTF-8 meta JSON) and
//                          X-LotR-Update-Signature (ECDSA P-256 / SHA-256, IEEE-P1363, base64url), body = the zip
//                          -> 201 { id } | 401 bad signature | 400 | 404 disabled | 409 duplicate/busy | 413 | 429 | 507
//   GET  /status           loopback only, never through the proxy -> { enabled, fingerprint, pending, busy }
//
// The signature is checked on the headers BEFORE a single body byte is read (Expect: 100-continue is honoured), so an
// unsigned upload never touches the disk. Disabled (404) while the public key file is missing.
//
// CLI (defence in depth, used by Update-Site.ps1):
//   node src/site-update.js verify --zip <file.zip> --meta <inbox entry .json> --pub <public key .pem>   exit 0 | 1
//   node src/site-update.js fingerprint --pub <public key .pem>
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const { clientIp, isLoopback, isProxied, sendJson, HttpError } = require('./http-util');
const { RateLimiter } = require('./support');
const { silentLogger } = require('./log');

const KIND = 'lotrmp-site-update';
const SCHEMA = 1;
const META_HEADER = 'x-lotr-update-meta';
const SIG_HEADER = 'x-lotr-update-signature';
const ROUTE = '/api/site-update';
const ID_PATTERN = /^\d{8}-\d{6}-[0-9a-f]{8}$/;
const SHA_PATTERN = /^[0-9a-f]{64}$/;
const B64URL = /^[A-Za-z0-9_-]+$/;
const utf8 = new TextDecoder('utf-8', { fatal: true });

// ---------------------------------------------------------------------------------------------------------------
// Crypto helpers (shared with the CLI; site/scripts/site-update.mjs implements the signing side)
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

// Validates the parsed meta. Returns a list of problems (empty = fine). Time checks are separate (checkFreshness).
function validateMeta(meta) {
    const p = [];
    if (!meta || typeof meta !== 'object' || Array.isArray(meta)) return ['meta must be a JSON object'];
    if (meta.kind !== KIND) p.push(`kind must be "${KIND}"`);
    if (meta.schema !== SCHEMA) p.push(`schema must be ${SCHEMA}`);
    if (typeof meta.name !== 'string' || !/^[\w.() -]{1,120}\.zip$/i.test(meta.name)) p.push('name: a .zip file name (letters, digits, . _ - space, at most 120)');
    if (typeof meta.version !== 'string' || meta.version.length < 1 || meta.version.length > 120 || /[\u0000-\u001f]/.test(meta.version)) p.push('version: 1-120 printable characters');
    if (typeof meta.sha256 !== 'string' || !SHA_PATTERN.test(meta.sha256)) p.push('sha256: 64 lowercase hex characters');
    if (!Number.isSafeInteger(meta.size) || meta.size < 1) p.push('size: a positive integer');
    if (typeof meta.createdAt !== 'string' || !Number.isFinite(Date.parse(meta.createdAt))) p.push('createdAt: an ISO date');
    if (meta.notes !== undefined && meta.notes !== null && (typeof meta.notes !== 'string' || meta.notes.length > 1000)) p.push('notes: text, at most 1000 characters');
    return p;
}

// Replay protection: a signed meta is valid for maxAgeDays after createdAt, and not more than maxFutureMinutes ahead.
function checkFreshness(meta, nowMs, { maxAgeDays = 7, maxFutureMinutes = 60 } = {}) {
    const t = Date.parse(meta.createdAt);
    if (t < nowMs - maxAgeDays * 86400000) return `createdAt ${meta.createdAt} is older than ${maxAgeDays} days: sign the package again`;
    if (t > nowMs + maxFutureMinutes * 60000) return `createdAt ${meta.createdAt} is in the future: check the clock of the PC that signed it`;
    return null;
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
    try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); } catch { return null; }
}

// ---------------------------------------------------------------------------------------------------------------
// Receiver
// ---------------------------------------------------------------------------------------------------------------
function createSiteUpdate(config, { logger = silentLogger, now = () => Date.now() } = {}) {
    const su = config.siteUpdate;
    const log = logger;
    const inbox = path.join(su.dir, 'inbox');
    const installed = path.join(su.dir, 'installed');
    const limiter = new RateLimiter({ limit: su.perIpPerHour, windowMs: 3600000, now });
    let busy = null;

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

    async function receive(req, res, ip, expectContinue) {
        // 1. Enabled only with a public key.
        const k = loadPublicKey(su.publicKeyFile);
        if (k.missing) throw new HttpError(404, 'not_found');
        if (!k.key) {
            log.error('siteupdate.key.unreadable', { error: k.error });
            throw new HttpError(503, 'key_unreadable', 'the server cannot read its public key');
        }
        // 2. Rate limit (every attempt counts, signed or not).
        const wait = limiter.retryAfter(ip);
        if (wait > 0) {
            log.warn('siteupdate.rate_limited', { ip });
            throw new HttpError(429, 'rate_limited', 'too many uploads from this address', { retryAfter: wait });
        }
        limiter.record(ip);
        // 3. Signature over the exact meta bytes, before anything else is believed.
        const metaBytes = b64urlDecode(req.headers[META_HEADER]);
        const sig = req.headers[SIG_HEADER];
        if (!metaBytes || metaBytes.length > 8192 || typeof sig !== 'string') {
            log.warn('siteupdate.rejected', { ip, reason: 'unsigned' });
            throw new HttpError(401, 'unsigned', `headers ${META_HEADER} and ${SIG_HEADER} are required`);
        }
        if (!verifySignature(metaBytes, sig, k.key)) {
            log.warn('siteupdate.rejected', { ip, reason: 'bad_signature' });
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
        const problems = validateMeta(meta);
        if (problems.length) throw new HttpError(400, 'bad_meta', problems.join('; '));
        // 4. Replay, size, duplicate, busy, disk.
        const stale = checkFreshness(meta, now(), su);
        if (stale) {
            log.warn('siteupdate.rejected', { ip, reason: 'stale', createdAt: meta.createdAt });
            throw new HttpError(400, 'stale', stale);
        }
        if (meta.size > su.maxBytes) throw new HttpError(413, 'too_large', `the package is larger than ${su.maxBytes} bytes`);
        const declared = req.headers['content-length'];
        if (declared !== undefined && Number(declared) !== meta.size) throw new HttpError(400, 'length_mismatch', `Content-Length ${declared} differs from the signed size ${meta.size}`);
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

        // 5. Body: streamed to <id>.part, hashed and counted on the way.
        const id = `${stampOf(now())}-${meta.sha256.slice(0, 8)}`;
        const part = path.join(inbox, `${id}.part`);
        busy = { id, at: now(), ip };
        const started = Date.now();
        try {
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
            log.info('siteupdate.receiving', { id, ip, name: meta.name, size: meta.size });
            if (expectContinue) res.writeContinue();
            try {
                await pipeline(req, meter, fs.createWriteStream(part, { flags: 'wx' }));
            } catch (error) {
                try { fs.unlinkSync(part); } catch { /* not created */ }
                if (error instanceof HttpError) throw error;
                log.warn('siteupdate.aborted', { id, ip, bytes: count, error: error.code || error.message });
                throw new HttpError(400, 'aborted', 'upload interrupted');
            }
            const sha = hash.digest('hex');
            if (count !== meta.size || sha !== meta.sha256) {
                try { fs.unlinkSync(part); } catch { /* ignore */ }
                log.warn('siteupdate.rejected', { id, ip, reason: 'sha_mismatch', bytes: count });
                throw new HttpError(400, 'sha_mismatch', count !== meta.size ? `received ${count} bytes, signed size ${meta.size}` : 'the body does not match the signed sha256');
            }
            fs.renameSync(part, path.join(inbox, `${id}.zip`));
            writeJsonAtomic(path.join(inbox, `${id}.json`), {
                id, meta, metaRaw, signature: sig.trim(), receivedAt: new Date(now()).toISOString(), ip, fingerprint: fingerprint(k.key),
            });
            prunePending();
            const sec = Math.max(0.001, (Date.now() - started) / 1000);
            log.info('siteupdate.stored', { id, ip, name: meta.name, version: meta.version, size: meta.size, sha256: meta.sha256.slice(0, 12), seconds: Math.round(sec), mbps: Math.round((meta.size / 1048576 / sec) * 10) / 10 });
            return sendJson(res, 201, { id, name: meta.name, version: meta.version, size: meta.size, sha256: meta.sha256 });
        } finally {
            busy = null;
        }
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
            if (url.pathname === ROUTE) throw new HttpError(405, 'method_not_allowed', 'PUT only');
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
        start() {
            cleanParts();
            return new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(su.port, config.host, () => {
                    server.removeListener('error', reject);
                    const addr = server.address();
                    const k = loadPublicKey(su.publicKeyFile);
                    log.info('siteupdate.started', { host: addr.address, port: addr.port, enabled: Boolean(k.key), fingerprint: k.key ? fingerprint(k.key) : null, dir: su.dir });
                    resolve(addr);
                });
            });
        },
        stop() {
            return new Promise((resolve) => {
                if (!server.listening) { resolve(); return; }
                server.close(() => resolve());
                server.closeAllConnections?.();
            });
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
        process.stdout.write(`${JSON.stringify({ ok: r.ok, problems: r.problems, fingerprint: r.fingerprint || null, name: r.meta?.name ?? null, version: r.meta?.version ?? null, sha256: r.meta?.sha256 ?? null, size: r.meta?.size ?? null, createdAt: r.meta?.createdAt ?? null })}\n`);
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
    createSiteUpdate, verifyEnvelope, verifySignature, validateMeta, checkFreshness, loadPublicKey, fingerprint, hashFile, cli,
    KIND, SCHEMA, META_HEADER, SIG_HEADER, ROUTE, ID_PATTERN,
};
