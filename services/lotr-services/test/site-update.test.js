'use strict';
// Signed mailbox for website updates: the real listener on a random loopback port, a temporary data folder, a fixed
// clock, and test keys generated here (never the real signing key).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { loadConfig } = require('../src/config');
const { createSiteUpdate, verifyEnvelope, ID_PATTERN } = require('../src/site-update');

const NOW = Date.parse('2026-10-18T12:00:00Z');
const CLI = path.join(__dirname, '..', 'src', 'site-update.js');

function keyPair() {
    return crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
}

function makeMeta(body, extra = {}) {
    return {
        kind: 'lotrmp-site-update', schema: 1, name: 'LotRMPEvent-site-2026-10-18.zip', version: 'LotRMPEvent-site-2026-10-18',
        sha256: crypto.createHash('sha256').update(body).digest('hex'), size: body.length,
        createdAt: new Date(NOW - 60000).toISOString(), notes: 'test', ...extra,
    };
}

function signed(meta, privateKey) {
    const raw = JSON.stringify(meta);
    const sig = crypto.sign('sha256', Buffer.from(raw, 'utf8'), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64url');
    return { raw, sig, headers: { 'x-lotr-update-meta': Buffer.from(raw, 'utf8').toString('base64url'), 'x-lotr-update-signature': sig } };
}

async function startReceiver({ withKey = true, siteUpdate = {} } = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-siteupdate-'));
    const keys = keyPair();
    const config = loadConfig({ argv: [], env: {}, overrides: { root, port: 0, siteUpdate: { port: 0, ...siteUpdate } } });
    if (withKey) {
        fs.mkdirSync(config.siteUpdate.dir, { recursive: true });
        fs.writeFileSync(config.siteUpdate.publicKeyFile, keys.publicKey.export({ type: 'spki', format: 'pem' }));
    }
    const su = createSiteUpdate(config, { now: () => NOW });
    const addr = await su.start();
    const inbox = path.join(config.siteUpdate.dir, 'inbox');
    const inboxFiles = () => { try { return fs.readdirSync(inbox).sort(); } catch { return []; } };
    return { su, config, keys, port: addr.port, root, inbox, inboxFiles, stop: () => su.stop() };
}

// PUT with node:http. `body` is written in one go unless `hold` is set (then the caller ends the request).
function put(port, { headers = {}, body = Buffer.alloc(0), expect = false, hold = false, contentLength } = {}) {
    return new Promise((resolve, reject) => {
        const h = { ...headers, 'content-type': 'application/zip', 'content-length': String(contentLength ?? body.length) };
        if (expect) h.expect = '100-continue';
        const req = http.request({ host: '127.0.0.1', port, method: 'PUT', path: '/api/site-update', headers: h });
        let sentBody = false;
        const sendBody = () => {
            if (sentBody) return;
            sentBody = true;
            if (hold) { req.write(body.subarray(0, Math.floor(body.length / 2))); return; }
            req.end(body);
        };
        req.on('continue', sendBody);
        req.on('response', (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = JSON.parse(text); } catch { /* not JSON */ }
                resolve({ status: res.statusCode, json, sentBody });
            });
        });
        req.on('error', (error) => { if (error.code === 'ECONNRESET' || error.code === 'EPIPE') resolve({ status: 0, error: error.code, sentBody }); else reject(error); });
        if (!expect) sendBody();
        if (hold) put.lastReq = req;
    });
}

const payload = (n = 300_000) => crypto.randomBytes(n);

test('good upload: 201, zip + entry in the inbox, envelope verifies, verify CLI exits 0', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const meta = makeMeta(body);
    const { headers } = signed(meta, s.keys.privateKey);
    const r = await put(s.port, { headers, body, expect: true });
    assert.equal(r.status, 201, JSON.stringify(r.json));
    assert.match(r.json.id, ID_PATTERN);
    assert.deepEqual(s.inboxFiles(), [`${r.json.id}.json`, `${r.json.id}.zip`]);
    const zip = path.join(s.inbox, `${r.json.id}.zip`);
    const entryFile = path.join(s.inbox, `${r.json.id}.json`);
    assert.ok(fs.readFileSync(zip).equals(body));
    const entry = JSON.parse(fs.readFileSync(entryFile, 'utf8'));
    assert.equal(entry.meta.sha256, meta.sha256);
    assert.equal(entry.ip, '127.0.0.1');
    const v = await verifyEnvelope({ zip, envelope: entry, publicKeyFile: s.config.siteUpdate.publicKeyFile });
    assert.equal(v.ok, true, v.problems.join('; '));

    const cli = spawnSync(process.execPath, [CLI, 'verify', '--zip', zip, '--meta', entryFile, '--pub', s.config.siteUpdate.publicKeyFile], { encoding: 'utf8' });
    assert.equal(cli.status, 0, cli.stdout + cli.stderr);
    assert.equal(JSON.parse(cli.stdout).ok, true);
    // Without Expect: 100-continue too (Caddy may not forward it): the same package is now a duplicate.
    const again = await put(s.port, { headers, body });
    assert.equal(again.status, 409);
    assert.equal(again.json.error, 'duplicate');
});

test('bad signature or no signature: 401, the body is never sent (100-continue) nor stored', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const other = keyPair();
    const { headers } = signed(makeMeta(body), other.privateKey);
    const r = await put(s.port, { headers, body, expect: true });
    assert.equal(r.status, 401);
    assert.equal(r.json.error, 'bad_signature');
    assert.equal(r.sentBody, false, 'the client never got 100 Continue');
    const unsigned = await put(s.port, { body, expect: true });
    assert.equal(unsigned.status, 401);
    assert.equal(unsigned.json.error, 'unsigned');
    // Meta changed after signing (version swapped): the signature no longer matches.
    const good = signed(makeMeta(body), s.keys.privateKey);
    const forged = { ...good.headers, 'x-lotr-update-meta': Buffer.from(good.raw.replace('LotRMPEvent-site-2026-10-18"', 'LotRMPEvent-site-2099-01-01"'), 'utf8').toString('base64url') };
    assert.equal((await put(s.port, { headers: forged, body, expect: true })).status, 401);
    assert.deepEqual(s.inboxFiles(), []);
});

test('tampered body: 400 sha_mismatch and nothing kept', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const { headers } = signed(makeMeta(body), s.keys.privateKey);
    const tampered = Buffer.from(body);
    tampered[1000] ^= 0xff;
    const r = await put(s.port, { headers, body: tampered, expect: true });
    assert.equal(r.status, 400);
    assert.equal(r.json.error, 'sha_mismatch');
    assert.deepEqual(s.inboxFiles(), []);
    // Content-Length different from the signed size: refused before the body.
    const short = await put(s.port, { headers, body: body.subarray(10), expect: true });
    assert.equal(short.status, 400);
    assert.equal(short.json.error, 'length_mismatch');
    assert.deepEqual(s.inboxFiles(), []);
});

test('oversize: 413 before the body', async (t) => {
    const s = await startReceiver({ siteUpdate: { maxBytes: 100_000 } });
    t.after(s.stop);
    const body = payload(200_000);
    const r = await put(s.port, { headers: signed(makeMeta(body), s.keys.privateKey).headers, body, expect: true });
    assert.equal(r.status, 413);
    assert.equal(r.sentBody, false);
    assert.deepEqual(s.inboxFiles(), []);
});

test('replay protection: createdAt older than 7 days or more than 1 h ahead is refused', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const old = await put(s.port, { headers: signed(makeMeta(body, { createdAt: new Date(NOW - 8 * 86400000).toISOString() }), s.keys.privateKey).headers, body, expect: true });
    assert.equal(old.status, 400);
    assert.equal(old.json.error, 'stale');
    const future = await put(s.port, { headers: signed(makeMeta(body, { createdAt: new Date(NOW + 2 * 3600000).toISOString() }), s.keys.privateKey).headers, body, expect: true });
    assert.equal(future.status, 400);
    assert.equal(future.json.error, 'stale');
    assert.deepEqual(s.inboxFiles(), []);
});

test('duplicate of an installed package (history) and bad meta fields', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const meta = makeMeta(body);
    const installed = path.join(s.config.siteUpdate.dir, 'installed');
    fs.mkdirSync(installed, { recursive: true });
    fs.writeFileSync(path.join(installed, 'history.jsonl'), `${JSON.stringify({ sha256: meta.sha256, id: 'x' })}\n`);
    const r = await put(s.port, { headers: signed(meta, s.keys.privateKey).headers, body, expect: true });
    assert.equal(r.status, 409);
    assert.match(r.json.message, /installed/);
    const bad = await put(s.port, { headers: signed(makeMeta(body, { kind: 'other' }), s.keys.privateKey).headers, body, expect: true });
    assert.equal(bad.status, 400);
    assert.equal(bad.json.error, 'bad_meta');
});

test('busy: a second upload while one is streaming gets 409, the first one completes', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body1 = payload(400_000);
    const body2 = payload(1000);
    const first = put(s.port, { headers: signed(makeMeta(body1), s.keys.privateKey).headers, body: body1, expect: true, hold: true });
    const req1 = await new Promise((resolve) => { const i = setInterval(() => { if (put.lastReq) { clearInterval(i); resolve(put.lastReq); } }, 5); });
    put.lastReq = null;
    for (let i = 0; i < 200 && !s.su.status().busy; i++) await new Promise((r) => setTimeout(r, 10));
    assert.ok(s.su.status().busy, 'first upload is in progress');
    const second = await put(s.port, { headers: signed(makeMeta(body2), s.keys.privateKey).headers, body: body2, expect: true });
    assert.equal(second.status, 409);
    assert.equal(second.json.error, 'busy');
    req1.end(body1.subarray(Math.floor(body1.length / 2)));
    const r1 = await first;
    assert.equal(r1.status, 201, JSON.stringify(r1.json));
    assert.equal(s.su.status().busy, null);
});

test('disabled without a public key: 404', async (t) => {
    const s = await startReceiver({ withKey: false });
    t.after(s.stop);
    const body = payload(1000);
    const r = await put(s.port, { headers: signed(makeMeta(body), s.keys.privateKey).headers, body, expect: true });
    assert.equal(r.status, 404);
    const st = await fetch(`http://127.0.0.1:${s.port}/status`).then((x) => x.json());
    assert.equal(st.enabled, false);
});

test('not enough disk (507), rate limit (429), pending pruned to keepPending, /status refuses proxied calls', async (t) => {
    const disk = await startReceiver({ siteUpdate: { minFreeBytes: 1e18 } });
    t.after(disk.stop);
    const b0 = payload(1000);
    assert.equal((await put(disk.port, { headers: signed(makeMeta(b0), disk.keys.privateKey).headers, body: b0, expect: true })).status, 507);

    const s = await startReceiver({ siteUpdate: { perIpPerHour: 3, keepPending: 2 } });
    t.after(s.stop);
    const ids = [];
    for (let i = 0; i < 3; i++) {
        const b = payload(2000);
        const r = await put(s.port, { headers: signed(makeMeta(b), s.keys.privateKey).headers, body: b, expect: true });
        assert.equal(r.status, 201);
        ids.push(r.json.id);
    }
    const zips = s.inboxFiles().filter((n) => n.endsWith('.zip'));
    assert.equal(zips.length, 2, `kept ${zips.join(', ')}`);
    const b = payload(1000);
    const limited = await put(s.port, { headers: signed(makeMeta(b), s.keys.privateKey).headers, body: b, expect: true });
    assert.equal(limited.status, 429);
    assert.ok(Number(limited.json.retryAfter) > 0);
    const proxied = await fetch(`http://127.0.0.1:${s.port}/status`, { headers: { 'x-forwarded-for': '203.0.113.9' } });
    assert.equal(proxied.status, 404);
    const st = await fetch(`http://127.0.0.1:${s.port}/status`).then((x) => x.json());
    assert.equal(st.enabled, true);
    assert.match(st.fingerprint, /^([0-9a-f]{4} ){15}[0-9a-f]{4}$/);
});

test('verify CLI: tampered zip and wrong key exit 1; fingerprint prints the grouped hash', async (t) => {
    const s = await startReceiver();
    t.after(s.stop);
    const body = payload();
    const r = await put(s.port, { headers: signed(makeMeta(body), s.keys.privateKey).headers, body, expect: true });
    assert.equal(r.status, 201);
    const zip = path.join(s.inbox, `${r.json.id}.zip`);
    const entry = path.join(s.inbox, `${r.json.id}.json`);
    const pub = s.config.siteUpdate.publicKeyFile;
    const copy = path.join(s.root, 'copy.zip');
    const bytes = fs.readFileSync(zip);
    bytes[5] ^= 1;
    fs.writeFileSync(copy, bytes);
    const tampered = spawnSync(process.execPath, [CLI, 'verify', '--zip', copy, '--meta', entry, '--pub', pub], { encoding: 'utf8' });
    assert.equal(tampered.status, 1);
    assert.match(JSON.parse(tampered.stdout).problems.join(' '), /sha256/);
    const otherPub = path.join(s.root, 'other.pub.pem');
    fs.writeFileSync(otherPub, keyPair().publicKey.export({ type: 'spki', format: 'pem' }));
    const wrong = spawnSync(process.execPath, [CLI, 'verify', '--zip', zip, '--meta', entry, '--pub', otherPub], { encoding: 'utf8' });
    assert.equal(wrong.status, 1);
    assert.match(JSON.parse(wrong.stdout).problems.join(' '), /signature/);
    const fp = spawnSync(process.execPath, [CLI, 'fingerprint', '--pub', pub], { encoding: 'utf8' });
    assert.equal(fp.status, 0);
    assert.match(fp.stdout.trim(), /^([0-9a-f]{4} ){15}[0-9a-f]{4}$/);
    const usage = spawnSync(process.execPath, [CLI, 'verify'], { encoding: 'utf8' });
    assert.equal(usage.status, 1);
});

// ---------------------------------------------------------------------------------------------------------------
// Schema 2: signed envelope + download URL (POST), delivery state (GET ?id=), signed release reads (GET ?file=).
// The "R2" here is a local HTTP server; allowLoopbackHttpDownload is a test-only setting.
// ---------------------------------------------------------------------------------------------------------------
const { checkDownloadUrl, validateMeta } = require('../src/site-update');

// A local download server: `routes` maps a path to { body, status, holdMs, headers }.
async function startOrigin(routes) {
    const hits = [];
    const server = http.createServer(async (req, res) => {
        hits.push(req.url);
        const r = routes[new URL(req.url, 'http://x').pathname];
        if (!r) { res.writeHead(404); res.end(); return; }
        res.writeHead(r.status ?? 200, { 'content-type': 'application/zip', 'content-length': String(r.body.length), ...(r.headers ?? {}) });
        if (r.holdMs) {
            res.write(r.body.subarray(0, 1000));
            await new Promise((ok) => setTimeout(ok, r.holdMs));
            res.end(r.body.subarray(1000));
        } else res.end(r.body);
    });
    await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
    return { port: server.address().port, hits, stop: () => new Promise((ok) => { server.closeAllConnections(); server.close(ok); }) };
}

function post(port, { headers = {}, body = null } = {}) {
    return new Promise((resolve, reject) => {
        const h = { ...headers, 'content-length': String(body ? body.length : 0) };
        const req = http.request({ host: '127.0.0.1', port, method: 'POST', path: '/api/site-update', headers: h }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                let json = null;
                try { json = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { /* not JSON */ }
                resolve({ status: res.statusCode, json });
            });
        });
        req.on('error', reject);
        req.end(body ?? undefined);
    });
}

function get(port, query, headers = {}) {
    return new Promise((resolve, reject) => {
        http.get({ host: '127.0.0.1', port, path: `/api/site-update?${query}`, headers }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const buf = Buffer.concat(chunks);
                let json = null;
                try { json = JSON.parse(buf.toString('utf8')); } catch { /* binary */ }
                resolve({ status: res.statusCode, json, buf, headers: res.headers });
            });
        }).on('error', reject);
    });
}

const R2_URL = 'https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com/lotrmp-files/site-packages/x/p.zip';
const envelopeMeta = (body, url, extra = {}) => makeMeta(body, { schema: 2, url, ...extra });
const readToken = (privateKey, extra = {}) => signed({ kind: 'lotrmp-release-read', schema: 1, createdAt: new Date(NOW - 60000).toISOString(), ...extra }, privateKey).headers;

test('envelope (POST, schema 2): the service downloads, checks and stores; the entry verifies like an upload', async (t) => {
    const s = await startReceiver({ siteUpdate: { allowLoopbackHttpDownload: true } });
    t.after(s.stop);
    const body = payload(500_000);
    const origin = await startOrigin({ '/site-packages/ab/pkg.zip': { body } });
    t.after(origin.stop);
    const url = `http://127.0.0.1:${origin.port}/site-packages/ab/pkg.zip?X-Amz-Signature=secret`;
    const r = await post(s.port, { headers: signed(envelopeMeta(body, url), s.keys.privateKey).headers });
    assert.equal(r.status, 201, JSON.stringify(r.json));
    assert.equal(r.json.state, 'stored');
    assert.match(r.json.id, ID_PATTERN);
    const zip = path.join(s.inbox, `${r.json.id}.zip`);
    const entryFile = path.join(s.inbox, `${r.json.id}.json`);
    assert.ok(fs.readFileSync(zip).equals(body));
    const entry = JSON.parse(fs.readFileSync(entryFile, 'utf8'));
    assert.deepEqual(entry.delivery, { method: 'download', host: `127.0.0.1:${origin.port}` });
    assert.equal((await verifyEnvelope({ zip, envelope: entry, publicKeyFile: s.config.siteUpdate.publicKeyFile })).ok, true);
    const cli = spawnSync(process.execPath, [CLI, 'verify', '--zip', zip, '--meta', entryFile, '--pub', s.config.siteUpdate.publicKeyFile], { encoding: 'utf8' });
    assert.equal(cli.status, 0, cli.stdout + cli.stderr);
    const out = JSON.parse(cli.stdout);
    assert.equal(out.schema, 2);
    assert.match(out.delivery, /^download from 127\.0\.0\.1:/);
    assert.equal((await get(s.port, `id=${r.json.id}`)).json.state, 'stored');
    assert.equal((await get(s.port, 'id=20990101-000000-00000000')).json.state, 'unknown');
    // The same package again: duplicate, and nothing is downloaded a second time.
    const hitsBefore = origin.hits.length;
    const again = await post(s.port, { headers: signed(envelopeMeta(body, url), s.keys.privateKey).headers });
    assert.equal(again.status, 409);
    assert.equal(origin.hits.length, hitsBefore);
});

test('envelope: refused hosts, schema/method mix-ups and a body are rejected before any download', async (t) => {
    const s = await startReceiver(); // allowLoopbackHttpDownload off, as on the server
    t.after(s.stop);
    const body = payload(2000);
    const origin = await startOrigin({ '/p.zip': { body } });
    t.after(origin.stop);
    const local = `http://127.0.0.1:${origin.port}/p.zip`;
    const r1 = await post(s.port, { headers: signed(envelopeMeta(body, local), s.keys.privateKey).headers });
    assert.equal(r1.status, 400);
    assert.equal(r1.json.error, 'bad_url');
    const r2 = await post(s.port, { headers: signed(envelopeMeta(body, 'https://evil.example/p.zip'), s.keys.privateKey).headers });
    assert.equal(r2.json.error, 'bad_url');
    // Schema 2 by PUT, schema 1 by POST, a POST with a body, a schema 1 meta carrying a url: all 400.
    const putSchema2 = await put(s.port, { headers: signed(envelopeMeta(body, R2_URL), s.keys.privateKey).headers, body, expect: true });
    assert.equal(putSchema2.status, 400);
    assert.equal(putSchema2.json.error, 'bad_meta');
    const postSchema1 = await post(s.port, { headers: signed(makeMeta(body), s.keys.privateKey).headers });
    assert.equal(postSchema1.json.error, 'bad_meta');
    const withBody = await post(s.port, { headers: signed(envelopeMeta(body, R2_URL), s.keys.privateKey).headers, body });
    assert.equal(withBody.json.error, 'body_not_empty');
    assert.ok(validateMeta(makeMeta(body, { url: 'https://x.example/a.zip' })).some((p) => p.startsWith('url')));
    // Forged envelope: 401.
    assert.equal((await post(s.port, { headers: signed(envelopeMeta(body, local), keyPair().privateKey).headers })).status, 401);
    assert.equal(origin.hits.length, 0, 'nothing was downloaded');
    assert.deepEqual(s.inboxFiles(), []);
});

test('envelope: tampered, short or missing download is refused and leaves nothing', async (t) => {
    const s = await startReceiver({ siteUpdate: { allowLoopbackHttpDownload: true } });
    t.after(s.stop);
    const body = payload(300_000);
    const tampered = Buffer.from(body);
    tampered[42] ^= 0xff;
    const origin = await startOrigin({ '/t.zip': { body: tampered }, '/short.zip': { body: body.subarray(5) } });
    t.after(origin.stop);
    const r1 = await post(s.port, { headers: signed(envelopeMeta(body, `http://127.0.0.1:${origin.port}/t.zip`), s.keys.privateKey).headers });
    assert.equal(r1.status, 400);
    assert.equal(r1.json.error, 'sha_mismatch');
    const r2 = await post(s.port, { headers: signed(envelopeMeta(body, `http://127.0.0.1:${origin.port}/gone.zip`), s.keys.privateKey).headers });
    assert.equal(r2.status, 502);
    assert.equal(r2.json.error, 'download_failed');
    assert.equal((await get(s.port, `id=${r2.json.id}`)).json.state, 'failed');
    const r3 = await post(s.port, { headers: signed(envelopeMeta(body, `http://127.0.0.1:${origin.port}/short.zip`), s.keys.privateKey).headers });
    assert.equal(r3.json.error, 'length_mismatch');
    assert.deepEqual(s.inboxFiles(), []);
    assert.equal(s.su.status().busy, null);
});

test('envelope: a slow download answers 202, keeps the mailbox busy, then GET ?id= says stored', async (t) => {
    const s = await startReceiver({ siteUpdate: { allowLoopbackHttpDownload: true, envelopeWaitSec: 0.2 } });
    t.after(s.stop);
    const body = payload(200_000);
    const origin = await startOrigin({ '/slow.zip': { body, holdMs: 800 } });
    t.after(origin.stop);
    const r = await post(s.port, { headers: signed(envelopeMeta(body, `http://127.0.0.1:${origin.port}/slow.zip`), s.keys.privateKey).headers });
    assert.equal(r.status, 202, JSON.stringify(r.json));
    assert.equal(r.json.state, 'downloading');
    assert.equal((await get(s.port, `id=${r.json.id}`)).json.state, 'downloading');
    const other = payload(1000);
    const busyPut = await put(s.port, { headers: signed(makeMeta(other), s.keys.privateKey).headers, body: other, expect: true });
    assert.equal(busyPut.status, 409);
    assert.equal(busyPut.json.error, 'busy');
    await s.su.job(r.json.id).done;
    assert.equal((await get(s.port, `id=${r.json.id}`)).json.state, 'stored');
    assert.equal(s.su.status().busy, null);
    assert.ok(fs.readFileSync(path.join(s.inbox, `${r.json.id}.zip`)).equals(body));
});

test('signed release reads (GET ?file=): only release files, only with a fresh read token', async (t) => {
    const s = await startReceiver({ siteUpdate: { readFailuresPerIpPerHour: 5 } });
    t.after(s.stop);
    const rel = s.config.queue.releasesDir;
    const id = '20261112-120000-0a1b2c3d';
    const objName = `${'ab'.padEnd(64, '7')}.zst`;
    fs.mkdirSync(path.join(rel, id, 'objects', 'ab'), { recursive: true });
    const manifest = Buffer.from('{"releaseId":"x"}');
    const object = payload(70_000);
    fs.writeFileSync(path.join(rel, 'pending.json'), '{"releaseId":"20261112-120000-0a1b2c3d","channel":"stable"}');
    fs.writeFileSync(path.join(rel, id, 'manifest.json'), manifest);
    fs.writeFileSync(path.join(rel, id, 'objects', 'ab', objName), object);
    fs.writeFileSync(path.join(rel, id, 'secret.txt'), 'no');
    const token = readToken(s.keys.privateKey);

    const m = await get(s.port, `file=${encodeURIComponent(`${id}/manifest.json`)}`, token);
    assert.equal(m.status, 200);
    assert.ok(m.buf.equals(manifest));
    assert.equal(m.headers['cache-control'], 'no-store');
    const o = await get(s.port, `file=${encodeURIComponent(`${id}/objects/ab/${objName}`)}`, token);
    assert.equal(o.status, 200);
    assert.ok(o.buf.equals(object));
    assert.equal((await get(s.port, 'file=pending.json', token)).json.channel, 'stable');
    assert.equal((await get(s.port, 'file=channels.json', token)).status, 404);
    // Paths outside the list: refused whatever the token.
    for (const bad of [`${id}/secret.txt`, '../data/secrets/x', `${id}/objects/cd/${objName}`, `${id}/../pending.json`, 'pending.json/']) {
        assert.equal((await get(s.port, `file=${encodeURIComponent(bad)}`, token)).status, 400, bad);
    }
    // Tokens: none, another key, the upload kind, too old.
    assert.equal((await get(s.port, 'file=pending.json')).status, 401);
    assert.equal((await get(s.port, 'file=pending.json', readToken(keyPair().privateKey))).status, 401);
    assert.equal((await get(s.port, 'file=pending.json', signed(makeMeta(payload(10)), s.keys.privateKey).headers)).status, 400);
    const old = await get(s.port, 'file=pending.json', readToken(s.keys.privateKey, { createdAt: new Date(NOW - 13 * 3600000).toISOString() }));
    assert.equal(old.status, 401);
    assert.equal(old.json.error, 'stale');
    // Refused attempts are limited per address (5 here: the 4 refused tokens above + 1), then even a good token waits.
    assert.equal((await get(s.port, 'file=pending.json')).status, 401);
    const limited = await get(s.port, 'file=pending.json', token);
    assert.equal(limited.status, 429);
    assert.ok(Number(limited.headers['retry-after']) > 0);
});

test('GET without a key is 404; GET without id/file or with a bad id is 400', async (t) => {
    const off = await startReceiver({ withKey: false });
    t.after(off.stop);
    assert.equal((await get(off.port, 'id=20261112-120000-0a1b2c3d')).status, 404);
    const s = await startReceiver();
    t.after(s.stop);
    assert.equal((await get(s.port, 'nothing=1')).status, 400);
    assert.equal((await get(s.port, 'id=../../x')).status, 400);
});

test('download URL allow list: R2 S3 endpoint, configured hosts, https on 443 only', () => {
    const r2 = `${R2_URL}?X-Amz-Signature=1`;
    assert.equal(checkDownloadUrl(r2), null);
    assert.match(checkDownloadUrl(r2.replace('https:', 'http:')), /https only/);
    assert.match(checkDownloadUrl(r2.replace('.com/', '.com:8443/')), /port/);
    assert.match(checkDownloadUrl('https://evil.r2.cloudflarestorage.com/a.zip'), /not allowed/);
    assert.match(checkDownloadUrl('https://middleearthevent.com/files/a.zip'), /not allowed/);
    assert.equal(checkDownloadUrl('https://middleearthevent.com/files/a.zip', { downloadHosts: ['MiddleEarthEvent.com'] }), null);
    assert.match(checkDownloadUrl('https://user:pw@middleearthevent.com/a.zip', { downloadHosts: ['middleearthevent.com'] }), /valid/);
    assert.match(checkDownloadUrl('http://127.0.0.1:9/a.zip'), /https only/);
    assert.equal(checkDownloadUrl('http://127.0.0.1:9/a.zip', { allowLoopbackHttpDownload: true }), null);
});
