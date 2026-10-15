'use strict';
// lotr-services: the HTTP routes (CONTRACTS section 4). No framework: node:http and a switch on the path.
//
// Public (reached through Caddy):
//   POST /api/queue/slot        { kind, client }       -> 200 { token, expiresAt } | 429 { position, retryAfter }
//   POST /api/queue/heartbeat   { token }              -> 200 { expiresAt } | 410 { error: "slot_expired" }
//   POST /api/queue/release     { token }              -> 200 { released }
//   POST /api/support           { installId, appVersion, log } -> 200 { code, expiresAt }
// Loopback only (never routed by Caddy):
//   GET  /healthz                                      -> 200 { ok, ... }
//   GET  /internal/queue/check?slot=<token>            -> forward_auth for core objects (advisory by default)
//   GET  /internal/support/<code>   header X-LotR-Secret -> 200 record, then the code is burnt
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const { DownloadQueue } = require('./queue');
const { SupportStore, RateLimiter, maskUserPaths, summariseLog, validateSubmission, normaliseCode } = require('./support');
const { clientIp, isLoopback, isProxied, sendJson, readJson, HttpError, safeEqual } = require('./http-util');
const { silentLogger } = require('./log');

const VERSION = require('../package.json').version;
const SECRET_HEADER = 'x-lotr-secret';

// Reads the shared secret of the internal support route; creates the file with a random value when missing.
function loadSupportSecret(config, env = process.env) {
    if (env.LOTR_SUPPORT_SECRET) return { secret: env.LOTR_SUPPORT_SECRET.trim(), source: 'env' };
    const file = config.support.secretFile;
    if (fs.existsSync(file)) {
        const secret = fs.readFileSync(file, 'utf8').trim();
        if (secret.length < 16) throw new Error(`${file}: secret shorter than 16 characters`);
        return { secret, source: file, created: false };
    }
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const secret = crypto.randomBytes(32).toString('base64url');
    fs.writeFileSync(file, `${secret}\n`, { flag: 'wx' });
    return { secret, source: file, created: true };
}

function writeJsonAtomic(file, value) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = `${file}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(value));
    fs.renameSync(tmp, file);
}

function createServices(config, { now = () => Date.now(), logger = silentLogger, secret = null, env = process.env } = {}) {
    const log = logger;
    const startedAt = now();
    const supportSecret = secret ?? loadSupportSecret(config, env).secret;

    // --- queue + crash-safe state -------------------------------------------------------------------------
    let persistTimer = null;
    let dirty = false;
    const persistNow = () => {
        if (persistTimer) { clearTimeout(persistTimer); persistTimer = null; }
        if (!dirty) return;
        dirty = false;
        try {
            writeJsonAtomic(config.queue.stateFile, queue.toJSON());
        } catch (error) {
            log.error('queue.persist.failed', { error: error.message });
        }
    };
    const queue = new DownloadQueue(config.queue, {
        now,
        logger: log,
        onChange: () => {
            dirty = true;
            if (!persistTimer) {
                persistTimer = setTimeout(persistNow, config.queue.persistDebounceMs);
                persistTimer.unref();
            }
        },
    });
    try {
        if (fs.existsSync(config.queue.stateFile)) {
            const restored = queue.load(JSON.parse(fs.readFileSync(config.queue.stateFile, 'utf8')));
            log.info('queue.state.restored', { entries: restored, stats: queue.stats() });
        }
    } catch (error) {
        // A corrupt state file must not keep the service down: start empty, keep the bad file for inspection.
        log.error('queue.state.unreadable', { error: error.message });
        try { fs.renameSync(config.queue.stateFile, `${config.queue.stateFile}.bad-${now()}`); } catch { /* ignore */ }
    }

    // --- support ------------------------------------------------------------------------------------------
    const store = new SupportStore(config.support, { now, logger: log });
    const byIp = new RateLimiter({ limit: config.support.perIpPerHour, windowMs: 3600000, now });
    const byInstall = new RateLimiter({ limit: config.support.perInstallPerHour, windowMs: 3600000, now });
    const advisory = { checks: 0, missing: 0, invalid: 0 };

    // Object names ("<sha256>.raw") of the files of role "core" modules, read from every published manifest under
    // files\releases (CONTRACTS sections 1-2). Chunked files (>= 16 MiB) have no "object": their chunk index
    // ("chunkIndex", a small .zst) is read too, and its chunks are core objects of a core module. The chunk index itself
    // never needs a slot: the updater reads it while PLANNING, before it asks for one. Refreshed by the periodic sweep,
    // and at most every 5 s when an unknown object is asked for (a release published since). Unknown objects count as
    // core: the check fails closed in "enforce" mode only for objects it cannot place.
    const chunkIndexCache = new Map();
    const readChunkIndex = (releaseDir, name) => {
        if (chunkIndexCache.has(name)) return chunkIndexCache.get(name);
        let rows = null;
        try {
            let bytes = fs.readFileSync(path.join(releaseDir, 'objects', name.slice(0, 2), name));
            if (name.endsWith('.zst')) {
                if (typeof zlib.zstdDecompressSync !== 'function') throw new Error('this Node has no zstd (needs Node >= 22.15)');
                bytes = zlib.zstdDecompressSync(bytes);
            }
            const index = JSON.parse(bytes.toString('utf8'));
            rows = (index.chunks || []).map((c) => `${c[0]}.${c[3]}`);
        } catch (error) {
            log.warn('queue.chunkindex.unreadable', { object: name, error: error.message });
            return []; // not cached: retried at the next refresh
        }
        chunkIndexCache.set(name, rows);
        return rows;
    };
    const coreObjects = {
        names: new Set(),
        loadedAt: 0,
        has(name) { return this.names.has(name) || !this.known.has(name); },
        isKnown(name) { return this.known.has(name); },
        known: new Set(),
        refresh() {
            const dir = config.queue.releasesDir;
            const names = new Set();
            const known = new Set();
            try {
                for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
                    if (!entry.isDirectory()) continue;
                    const file = path.join(dir, entry.name, 'manifest.json');
                    if (!fs.existsSync(file)) continue;
                    try {
                        const manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
                        for (const m of manifest.modules || []) {
                            const core = m.role === 'core';
                            for (const f of m.files || []) {
                                if (f.object) {
                                    known.add(f.object);
                                    if (core) names.add(f.object);
                                }
                                if (f.chunked && f.chunkIndex) {
                                    known.add(f.chunkIndex);
                                    for (const chunk of readChunkIndex(path.join(dir, entry.name), f.chunkIndex)) {
                                        known.add(chunk);
                                        if (core) names.add(chunk);
                                    }
                                }
                            }
                        }
                    } catch (error) {
                        log.warn('queue.manifest.unreadable', { file, error: error.message });
                    }
                }
            } catch { /* no releases yet */ }
            this.names = names;
            this.known = known;
            this.loadedAt = now();
        },
    };
    coreObjects.refresh();

    async function handle(req, res) {
        const url = new URL(req.url, 'http://lotr-services.local');
        const route = `${req.method} ${url.pathname}`;
        const ip = clientIp(req, config.trustForwardedFor);
        try {
            if (route === 'GET /healthz') {
                if (!isLoopback(req.socket.remoteAddress) || isProxied(req)) throw new HttpError(404, 'not_found');
                return sendJson(res, 200, {
                    ok: true, version: VERSION, uptimeSec: Math.round((now() - startedAt) / 1000),
                    queue: { ...queue.stats(), enforcement: config.queue.enforcement, forwardAuth: advisory },
                    support: { stored: store.count() },
                });
            }

            if (route === 'POST /api/queue/slot') {
                const body = await readJson(req, 4096);
                const kind = body.kind ?? 'core';
                if (!queue.state(kind)) throw new HttpError(400, 'bad_kind', `kind must be one of: ${config.queue.kinds.join(', ')}`);
                if (!DownloadQueue.validClient(body.client)) throw new HttpError(400, 'bad_client', 'client: 8-128 characters, letters, digits, - and _');
                const r = queue.requestSlot({ kind, client: body.client, ip });
                if (r.granted) return sendJson(res, 200, { token: r.token, expiresAt: new Date(r.expiresAt).toISOString(), heartbeatSec: Math.floor(config.queue.slotTtlSec / 3) });
                const out = { position: r.position, retryAfter: r.retryAfter };
                if (r.reason) out.error = r.reason;
                return sendJson(res, 429, out, { 'Retry-After': String(r.retryAfter) });
            }

            if (route === 'POST /api/queue/heartbeat') {
                // Heartbeat keeps a slot alive (CONTRACTS section 8, 15/10; TTL slotTtlSec, the grant says heartbeatSec).
                const body = await readJson(req, 4096);
                const expiresAt = queue.heartbeat(body.token);
                if (!expiresAt) return sendJson(res, 410, { error: 'slot_expired' });
                return sendJson(res, 200, { expiresAt: new Date(expiresAt).toISOString() });
            }

            if (route === 'POST /api/queue/release') {
                const body = await readJson(req, 4096);
                return sendJson(res, 200, { released: queue.release(body.token) });
            }

            if (route === 'GET /internal/queue/check') {
                if (!isLoopback(req.socket.remoteAddress)) throw new HttpError(404, 'not_found');
                // forward_auth passes the original URI in X-Forwarded-Uri; ?slot= on the check URL wins.
                let token = url.searchParams.get('slot');
                let original = null;
                if (req.headers['x-forwarded-uri']) {
                    try { original = new URL(String(req.headers['x-forwarded-uri']), 'http://x'); } catch { original = null; }
                }
                if (!token && original) token = original.searchParams.get('slot');
                // Only objects of Core modules need a slot; other objects (event module, admin client) pass.
                // Anything that is not a release object (manifest, channels.json, the updater) never needs a slot.
                const object = original ? path.posix.basename(original.pathname) : null;
                if (object && !coreObjects.isKnown(object) && now() - coreObjects.loadedAt > 5000) coreObjects.refresh();
                if (original && (!original.pathname.includes('/objects/') || !coreObjects.has(object))) {
                    res.writeHead(200, { 'Content-Length': 0, 'Cache-Control': 'no-store' });
                    return res.end();
                }
                advisory.checks++;
                const ok = Boolean(token) && queue.check(token);
                if (!ok) {
                    if (token) advisory.invalid++; else advisory.missing++;
                    if (config.queue.enforcement === 'enforce') {
                        log.info('queue.check.refused', { ip, token: token ? token.slice(0, 8) : null });
                        return sendJson(res, 403, { error: token ? 'slot_invalid' : 'slot_required' });
                    }
                }
                res.writeHead(200, { 'Content-Length': 0, 'Cache-Control': 'no-store' });
                return res.end();
            }

            if (route === 'POST /api/support') {
                const body = await readJson(req, config.support.maxBodyBytes);
                const problems = validateSubmission(body);
                if (problems.length) throw new HttpError(400, 'invalid_fields', problems.join('; '));
                const wait = Math.max(byIp.retryAfter(`ip:${ip}`), byInstall.retryAfter(`id:${body.installId}`));
                if (wait > 0) {
                    log.info('support.rate_limited', { ip, install: body.installId.slice(0, 8) });
                    return sendJson(res, 429, { error: 'rate_limited', retryAfter: wait }, { 'Retry-After': String(wait) });
                }
                const masked = maskUserPaths(body.log);
                const summary = summariseLog(masked.text, body.appVersion);
                const record = store.save({ installId: body.installId, appVersion: body.appVersion, log: masked.text, maskedCount: masked.count, summary });
                byIp.record(`ip:${ip}`);
                byInstall.record(`id:${body.installId}`);
                log.info('support.stored', { code: record.code, ip, install: body.installId.slice(0, 8), bytes: Buffer.byteLength(masked.text), maskedServerSide: masked.count, errors: summary.errors });
                return sendJson(res, 200, { code: record.code, expiresAt: record.expiresAt });
            }

            if (req.method === 'GET' && url.pathname.startsWith('/internal/support/')) {
                // Loopback only, never through the proxy, and the shared secret: the Discord bot on this machine.
                if (!isLoopback(req.socket.remoteAddress) || isProxied(req)) throw new HttpError(404, 'not_found');
                const given = req.headers[SECRET_HEADER];
                if (!supportSecret || typeof given !== 'string' || !safeEqual(given, supportSecret)) {
                    log.warn('support.fetch.unauthorised', { ip });
                    throw new HttpError(401, 'unauthorised', 'missing or wrong X-LotR-Secret');
                }
                const code = normaliseCode(decodeURIComponent(url.pathname.slice('/internal/support/'.length)));
                if (!code) throw new HttpError(400, 'bad_code', 'code looks like LOTR-7F3K');
                const taken = store.take(code);
                if (taken.status === 'unknown') throw new HttpError(404, 'unknown_code', 'no log with this code');
                if (taken.status === 'expired') throw new HttpError(410, 'expired', 'this code has expired');
                if (taken.status === 'used') throw new HttpError(410, 'already_used', 'this code was already used');
                if (taken.status === 'busy') throw new HttpError(409, 'busy', 'this code is being fetched');
                const r = taken.record;
                let burnt = false;
                res.on('finish', () => {
                    burnt = true;
                    store.burn(code, r.expiresAt);
                    log.info('support.fetched', { code, ip });
                });
                res.on('close', () => { if (!burnt) store.unreserve(code); });
                return sendJson(res, 200, {
                    code: r.code, createdAt: r.createdAt, expiresAt: r.expiresAt, installId: r.installId,
                    appVersion: r.appVersion, summary: r.summary, maskedServerSide: r.maskedServerSide, log: r.log,
                });
            }

            throw new HttpError(404, 'not_found');
        } catch (error) {
            if (error instanceof HttpError) {
                if (!res.headersSent) sendJson(res, error.status, { error: error.code, message: error.message, ...error.extra });
                return undefined;
            }
            log.error('request.failed', { route, error: error.stack || error.message });
            if (!res.headersSent) sendJson(res, 500, { error: 'internal' });
            return undefined;
        }
    }

    function sweep() {
        queue.sweep();
        if (now() - coreObjects.loadedAt > 60000) coreObjects.refresh();
        store.purge();
        byIp.prune();
        byInstall.prune();
    }

    return { handle, queue, store, sweep, persistNow, advisory, coreObjects };
}

// Creates the HTTP server and the periodic jobs. start() resolves with the bound address.
function createApp(config, deps = {}) {
    const services = createServices(config, deps);
    const log = deps.logger || silentLogger;
    const server = http.createServer((req, res) => {
        services.handle(req, res).catch(() => { if (!res.headersSent) sendJson(res, 500, { error: 'internal' }); });
    });
    server.requestTimeout = 60000;
    server.headersTimeout = 15000;
    server.keepAliveTimeout = 5000;
    let timer = null;
    return {
        ...services,
        server,
        start() {
            return new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(config.port, config.host, () => {
                    services.sweep();
                    timer = setInterval(services.sweep, Math.min(config.support.purgeIntervalSec, 30) * 1000);
                    timer.unref();
                    const addr = server.address();
                    log.info('service.started', { host: addr.address, port: addr.port, version: VERSION, enforcement: config.queue.enforcement, maxConcurrent: config.queue.maxConcurrent });
                    resolve(addr);
                });
            });
        },
        stop() {
            if (timer) clearInterval(timer);
            services.persistNow();
            return new Promise((resolve) => {
                server.close(() => resolve());
                server.closeAllConnections?.();
            });
        },
    };
}

module.exports = { createApp, createServices, loadSupportSecret, VERSION, SECRET_HEADER };
