'use strict';
// End-to-end: the real HTTP server on a random loopback port, a temporary data folder and a fake clock.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { loadConfig } = require('../src/config');
const { createApp } = require('../src/app');

const SECRET = 'test-secret-0123456789abcdef';
const INSTALL = '0f8fad5b-d9cb-469f-a165-70867728950e';

async function startApp(overrides = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-services-'));
    const clock = { t: 1_800_000_000_000 };
    const config = loadConfig({ argv: [], env: {}, overrides: { root, port: 0, ...overrides } });
    const app = createApp(config, { now: () => clock.t, secret: SECRET });
    const addr = await app.start();
    const base = `http://127.0.0.1:${addr.port}`;
    const call = async (method, p, { body, headers = {}, raw } = {}) => {
        const init = { method, headers: { ...headers } };
        if (raw !== undefined) init.body = raw;
        else if (body !== undefined) { init.body = JSON.stringify(body); init.headers['content-type'] ??= 'application/json'; }
        const res = await fetch(base + p, init);
        const text = await res.text();
        let json = null;
        try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
        return { status: res.status, json, headers: res.headers };
    };
    return { app, config, clock, call, root, stop: () => app.stop() };
}

const submission = (extra = {}) => ({ installId: INSTALL, appVersion: '0.3.1', log: 'App version: 0.3.1\nStore: Steam\nErrors: 1\nC:\\Users\\jean\\AppData\\x\n[ERROR] boom', ...extra });
const fromIp = (ip) => ({ 'x-forwarded-for': ip });

test('queue over HTTP: grant, 429 with position and Retry-After, heartbeat, release', async (t) => {
    const s = await startApp({ queue: { maxConcurrent: 1 } });
    t.after(s.stop);
    const a = await s.call('POST', '/api/queue/slot', { body: { kind: 'core', client: 'install-aaaa' } });
    assert.equal(a.status, 200);
    assert.match(a.json.token, /^q_/);
    assert.ok(Date.parse(a.json.expiresAt) > s.clock.t);
    const b = await s.call('POST', '/api/queue/slot', { body: { kind: 'core', client: 'install-bbbb' } });
    assert.equal(b.status, 429);
    assert.equal(b.json.position, 1);
    assert.equal(b.headers.get('retry-after'), String(b.json.retryAfter));
    assert.equal((await s.call('POST', '/api/queue/heartbeat', { body: { token: a.json.token } })).status, 200);
    assert.equal((await s.call('POST', '/api/queue/heartbeat', { body: { token: 'q_bogus' } })).status, 410);
    assert.deepEqual((await s.call('POST', '/api/queue/release', { body: { token: a.json.token } })).json, { released: true });
    assert.equal((await s.call('POST', '/api/queue/slot', { body: { kind: 'core', client: 'install-bbbb' } })).status, 200);
    assert.equal((await s.call('POST', '/api/queue/slot', { body: { kind: 'event', client: 'install-cccc' } })).status, 400);
    assert.equal((await s.call('POST', '/api/queue/slot', { body: { kind: 'core', client: 'x' } })).status, 400);
});

test('queue: slot TTL over HTTP and state survives a restart', async (t) => {
    const s = await startApp({ queue: { maxConcurrent: 1, slotTtlSec: 60, persistDebounceMs: 10 } });
    const a = await s.call('POST', '/api/queue/slot', { body: { client: 'install-aaaa' } });
    await s.stop();
    const config = loadConfig({ argv: [], env: {}, overrides: { root: s.root, port: 0, queue: { maxConcurrent: 1, slotTtlSec: 60 } } });
    const app2 = createApp(config, { now: () => s.clock.t, secret: SECRET });
    const addr = await app2.start();
    t.after(() => app2.stop());
    const base = `http://127.0.0.1:${addr.port}`;
    const check = await fetch(`${base}/internal/queue/check?slot=${a.json.token}`);
    assert.equal(check.status, 200);
    assert.equal(app2.queue.stats().core.active, 1, 'slot restored after restart');
    s.clock.t += 61000;
    app2.queue.sweep();
    assert.equal(app2.queue.stats().core.active, 0, 'slot expired without heartbeat');
});

test('forward_auth check: advisory answers 200 and counts, enforce answers 403', async (t) => {
    const adv = await startApp();
    t.after(adv.stop);
    assert.equal((await adv.call('GET', '/internal/queue/check')).status, 200);
    assert.equal((await adv.call('GET', '/internal/queue/check?slot=q_bad')).status, 200);
    assert.equal(adv.app.advisory.missing, 1);
    assert.equal(adv.app.advisory.invalid, 1);

    const enf = await startApp({ queue: { enforcement: 'enforce' } });
    t.after(enf.stop);
    assert.equal((await enf.call('GET', '/internal/queue/check')).status, 403);
    assert.equal((await enf.call('GET', '/internal/queue/check?slot=q_bad')).status, 403);
    const slot = await enf.call('POST', '/api/queue/slot', { body: { client: 'install-aaaa' } });
    assert.equal((await enf.call('GET', `/internal/queue/check?slot=${slot.json.token}`)).status, 200);
    assert.equal((await enf.call('GET', '/internal/queue/check', { headers: { 'x-forwarded-uri': `/files/releases/x/objects/ab/abc.raw?slot=${slot.json.token}` } })).status, 200, 'token read from X-Forwarded-Uri');
});

test('forward_auth enforce: only objects of core modules need a slot (from the published manifests)', async (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-services-rel-'));
    const rel = path.join(root, 'files', 'releases', '20261015-140000-a1b2c3d4');
    fs.mkdirSync(rel, { recursive: true });
    const core = 'a'.repeat(64) + '.raw';
    const event = 'b'.repeat(64) + '.raw';
    fs.writeFileSync(path.join(rel, 'manifest.json'), JSON.stringify({
        schema: 1,
        modules: [
            { id: 'LotRMPCore', role: 'core', files: [{ path: 'x.dll', object: core }] },
            { id: 'OrderMiddleEarthEvent', role: 'event', files: [{ path: 'y.dll', object: event }] },
        ],
    }));
    const s = await startApp({ root, queue: { enforcement: 'enforce' } });
    t.after(s.stop);
    const uri = (obj, q = '') => ({ headers: { 'x-forwarded-uri': `/files/releases/20261015-140000-a1b2c3d4/objects/${obj.slice(0, 2)}/${obj}${q}` } });
    assert.equal((await s.call('GET', '/internal/queue/check', uri(event))).status, 200, 'event object: no slot needed');
    assert.equal((await s.call('GET', '/internal/queue/check', uri(core))).status, 403, 'core object without slot');
    assert.equal((await s.call('GET', '/internal/queue/check', uri('c'.repeat(64) + '.raw'))).status, 403, 'unknown object fails closed');
    const slot = await s.call('POST', '/api/queue/slot', { body: { client: 'install-aaaa' } });
    assert.equal((await s.call('GET', '/internal/queue/check', uri(core, `?slot=${slot.json.token}`))).status, 200);
});

test('forward_auth enforce: chunked files (index free, chunks core) and releases published after start', async (t) => {
    const zlib = require('node:zlib');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-services-chunks-'));
    const releases = path.join(root, 'files', 'releases');
    fs.mkdirSync(releases, { recursive: true });
    const s = await startApp({ root, queue: { enforcement: 'enforce' } });
    t.after(s.stop);
    // Published AFTER the service started (the periodic sweep has not run yet).
    const id = '20261015-150000-c0ffee00';
    const rel = path.join(releases, id);
    const put = (name, bytes) => { fs.mkdirSync(path.join(rel, 'objects', name.slice(0, 2)), { recursive: true }); fs.writeFileSync(path.join(rel, 'objects', name.slice(0, 2), name), bytes); };
    const coreChunk = 'd'.repeat(64);
    const eventChunk = 'e'.repeat(64);
    const coreIndex = '1'.repeat(64) + '.zst';
    const eventIndex = '2'.repeat(64) + '.zst';
    const index = (chunk) => zlib.zstdCompressSync(Buffer.from(JSON.stringify({ schema: 1, chunker: 'fastcdc2020', sha256: 'f'.repeat(64), size: 10, chunks: [[chunk, 10, 10, 'raw']] })));
    put(coreIndex, index(coreChunk));
    put(eventIndex, index(eventChunk));
    fs.writeFileSync(path.join(rel, 'manifest.json'), JSON.stringify({
        schema: 1,
        modules: [
            { id: 'LotRMPCore', role: 'core', files: [{ path: 'big.tpac', chunked: true, chunkIndex: coreIndex }] },
            { id: 'OrderMiddleEarthEvent', role: 'event', files: [{ path: 'big.wav', chunked: true, chunkIndex: eventIndex }] },
        ],
    }));
    s.clock.t += 10_000; // past the 5 s refresh guard
    const uri = (obj, q = '') => ({ headers: { 'x-forwarded-uri': `/files/releases/${id}/objects/${obj.slice(0, 2)}/${obj}${q}` } });
    assert.equal((await s.call('GET', '/internal/queue/check', uri(coreIndex))).status, 200, 'a chunk index is read while planning: no slot');
    assert.equal((await s.call('GET', '/internal/queue/check', uri(eventChunk + '.raw'))).status, 200, 'chunk of an event file: no slot');
    assert.equal((await s.call('GET', '/internal/queue/check', uri(coreChunk + '.raw'))).status, 403, 'chunk of a core file needs a slot');
    const slot = await s.call('POST', '/api/queue/slot', { body: { client: 'install-chunks' } });
    assert.equal((await s.call('GET', '/internal/queue/check', uri(coreChunk + '.raw', `?slot=${slot.json.token}`))).status, 200);
});

test('support: stores, masks again server-side, returns a LOTR code with 72 h expiry', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    const r = await s.call('POST', '/api/support', { body: submission() });
    assert.equal(r.status, 200);
    assert.match(r.json.code, /^LOTR-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}$/);
    assert.equal(Date.parse(r.json.expiresAt) - s.clock.t, 72 * 3600 * 1000);
    const stored = JSON.parse(fs.readFileSync(path.join(s.config.support.dir, `${r.json.code}.json`), 'utf8'));
    assert.ok(stored.log.includes('C:\\Users\\<user>\\AppData'));
    assert.ok(!stored.log.includes('jean'));
    assert.equal(stored.summary.store, 'Steam');
    assert.equal(stored.summary.errors, 1);
});

test('support: size limit, content type, binary and field validation', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    const big = await s.call('POST', '/api/support', { body: submission({ log: 'x'.repeat(2 * 1024 * 1024) }) });
    assert.equal(big.status, 413);
    const justUnder = await s.call('POST', '/api/support', { body: submission({ log: 'y'.repeat(2 * 1024 * 1024 - 200) }) });
    assert.equal(justUnder.status, 200, 'a body just under 2 MB is accepted');
    assert.equal((await s.call('POST', '/api/support', { raw: 'log=x', headers: { 'content-type': 'text/plain' } })).status, 415);
    assert.equal((await s.call('POST', '/api/support', { raw: Buffer.from([0x7b, 0xff, 0xfe, 0x7d]), headers: { 'content-type': 'application/json' } })).json.error, 'not_utf8');
    assert.equal((await s.call('POST', '/api/support', { raw: '{nope', headers: { 'content-type': 'application/json' } })).json.error, 'bad_json');
    const bin = await s.call('POST', '/api/support', { body: submission({ log: 'MZ\u0000\u0000\u0003' }) });
    assert.equal(bin.status, 400);
    assert.match(bin.json.message, /binary/);
    assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'bad id' }) })).status, 400);
});

test('support: 3 per hour per IP (X-Forwarded-For from loopback) and per installId', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    for (let i = 0; i < 3; i++) {
        const r = await s.call('POST', '/api/support', { body: submission({ installId: `install-ip-${i}` }), headers: fromIp('203.0.113.7') });
        assert.equal(r.status, 200, `submission ${i}`);
    }
    const fourth = await s.call('POST', '/api/support', { body: submission({ installId: 'install-ip-9' }), headers: fromIp('203.0.113.7') });
    assert.equal(fourth.status, 429);
    assert.ok(fourth.json.retryAfter > 0);
    assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'install-ip-9' }), headers: fromIp('203.0.113.8') })).status, 200, 'another IP is fine');
    // Right-most entry is the one Caddy saw: a spoofed left part does not dodge the limit.
    assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'install-ip-10' }), headers: fromIp('1.2.3.4, 203.0.113.7') })).status, 429);

    for (let i = 0; i < 3; i++) {
        assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'install-same' }), headers: fromIp(`198.51.100.${i}`) })).status, 200);
    }
    assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'install-same' }), headers: fromIp('198.51.100.99') })).status, 429, 'per installId');
    s.clock.t += 3600 * 1000 + 1000;
    assert.equal((await s.call('POST', '/api/support', { body: submission({ installId: 'install-same' }), headers: fromIp('198.51.100.99') })).status, 200, 'window passed');
});

test('internal support route: secret, loopback-only, not through the proxy, burn after fetch', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    const { json: { code } } = await s.call('POST', '/api/support', { body: submission() });
    assert.equal((await s.call('GET', `/internal/support/${code}`)).status, 401, 'no secret');
    assert.equal((await s.call('GET', `/internal/support/${code}`, { headers: { 'x-lotr-secret': 'wrong' } })).status, 401, 'wrong secret');
    assert.equal((await s.call('GET', `/internal/support/${code}`, { headers: { 'x-lotr-secret': SECRET, ...fromIp('203.0.113.7') } })).status, 404, 'proxied request refused');
    assert.equal((await s.call('GET', '/internal/support/../../x', { headers: { 'x-lotr-secret': SECRET } })).status, 404);
    assert.equal((await s.call('GET', '/internal/support/LOTR-0000', { headers: { 'x-lotr-secret': SECRET } })).status, 400, 'impossible code');
    assert.equal((await s.call('GET', '/internal/support/LOTR-ZZZZ', { headers: { 'x-lotr-secret': SECRET } })).status, 404);
    const got = await s.call('GET', `/internal/support/${code.toLowerCase()}`, { headers: { 'x-lotr-secret': SECRET } });
    assert.equal(got.status, 200);
    assert.equal(got.json.code, code);
    assert.equal(got.json.appVersion, '0.3.1');
    assert.equal(got.json.summary.store, 'Steam');
    assert.ok(got.json.log.includes('<user>'));
    await new Promise((r) => setTimeout(r, 20));
    const again = await s.call('GET', `/internal/support/${code}`, { headers: { 'x-lotr-secret': SECRET } });
    assert.equal(again.status, 410);
    assert.equal(again.json.error, 'already_used');
});

test('internal support route: expired code', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    const { json: { code } } = await s.call('POST', '/api/support', { body: submission() });
    s.clock.t += 72 * 3600 * 1000 + 1;
    const r = await s.call('GET', `/internal/support/${code}`, { headers: { 'x-lotr-secret': SECRET } });
    assert.equal(r.status, 410);
    assert.equal(r.json.error, 'expired');
});

test('health endpoint and unknown routes', async (t) => {
    const s = await startApp();
    t.after(s.stop);
    const h = await s.call('GET', '/healthz');
    assert.equal(h.status, 200);
    assert.equal(h.json.ok, true);
    assert.equal(h.json.queue.core.max, 40, 'default max concurrent');
    assert.equal(h.json.queue.enforcement, 'advisory');
    assert.equal((await s.call('GET', '/healthz', { headers: fromIp('203.0.113.7') })).status, 404, 'not exposed through the proxy');
    assert.equal((await s.call('GET', '/nope')).status, 404);
    assert.equal((await s.call('GET', '/api/support')).status, 404);
});

test('config refuses a non-loopback host and bad values', () => {
    assert.throws(() => loadConfig({ argv: [], env: {}, overrides: { host: '0.0.0.0' } }), /not loopback/);
    assert.throws(() => loadConfig({ argv: [], env: {}, overrides: { queue: { enforcement: 'maybe' } } }), /enforcement/);
    assert.throws(() => loadConfig({ argv: [], env: {}, overrides: { support: { codeLength: 3 } } }), /codeLength/);
});

test('secret file is created on first start when missing', () => {
    const { loadSupportSecret } = require('../src/app');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-secret-'));
    const config = loadConfig({ argv: [], env: {}, overrides: { root } });
    const first = loadSupportSecret(config, {});
    assert.equal(first.created, true);
    assert.ok(first.secret.length >= 32);
    const second = loadSupportSecret(config, {});
    assert.equal(second.secret, first.secret);
    assert.equal(loadSupportSecret(config, { LOTR_SUPPORT_SECRET: 'from-env-0123456789' }).secret, 'from-env-0123456789');
});
