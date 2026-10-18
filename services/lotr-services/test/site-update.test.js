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
