'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
    SupportStore, RateLimiter, maskUserPaths, summariseLog, validateSubmission, normaliseCode, randomCode, CODE_PATTERN,
} = require('../src/support');

const tmpDir = () => fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-support-'));

test('masking: every path spelling of a Windows user folder', () => {
    const cases = [
        ['C:\\Users\\jean\\AppData\\Local\\x.log', 'C:\\Users\\<user>\\AppData\\Local\\x.log'],
        ['c:\\users\\Jean Dupont\\Documents\\Mount and Blade II Bannerlord', 'c:\\users\\<user>\\Documents\\Mount and Blade II Bannerlord'],
        ['C:/Users/jean/AppData', 'C:/Users/<user>/AppData'],
        ['{"p":"C:\\\\Users\\\\jean\\\\AppData"}', '{"p":"C:\\\\Users\\\\<user>\\\\AppData"}'],
        ['\\\\?\\C:\\Users\\jean\\x', '\\\\?\\C:\\Users\\<user>\\x'],
        ['/c/Users/jean/Desktop', '/c/Users/<user>/Desktop'],
        ['C:\\Documents and Settings\\jean\\x', 'C:\\Documents and Settings\\<user>\\x'],
        ['Z:\\home\\jean\\.steam', 'Z:\\home\\<user>\\.steam'],
        ['path=/home/deck/.local/share/Steam', 'path=/home/<user>/.local/share/Steam'],
        ['profile C:\\Users\\jean', 'profile C:\\Users\\<user>'],
        ['"C:\\Users\\jean"', '"C:\\Users\\<user>"'],
        ['D:\\Users\\jean\\x', 'D:\\Users\\<user>\\x'],
    ];
    for (const [input, expected] of cases) assert.equal(maskUserPaths(input).text, expected, input);
});

test('masking: shared folders and placeholders are kept, second pass changes nothing', () => {
    for (const s of ['C:\\Users\\Public\\Documents', 'C:\\Users\\Default\\x', 'C:\\Users\\%USERNAME%\\x', 'C:\\Users\\<user>\\x', 'C:\\Users\\***\\x']) {
        const r = maskUserPaths(s);
        assert.equal(r.text, s, s);
        assert.equal(r.count, 0, s);
    }
    const once = maskUserPaths('C:\\Users\\jean\\a and C:/Users/jean/b').text;
    assert.equal(maskUserPaths(once).text, once);
    assert.equal(maskUserPaths('C:\\Users\\jean\\a and C:/Users/jean/b').count, 2);
});

test('masking: unrelated paths are untouched', () => {
    for (const s of ['D:\\SteamLibrary\\steamapps\\common\\Mount & Blade II Bannerlord\\Modules', 'C:\\Program Files (x86)\\Steam', 'users: 3 online']) {
        assert.equal(maskUserPaths(s).text, s, s);
    }
});

test('summary: header keys, store, error count from header or counted', () => {
    const header = 'LotRMPUpdater log\nApp version: 0.3.1\nDetected store: Xbox (Game Pass)\nErrors: 2\n---\n[ERROR] x\n';
    const s = summariseLog(header, null);
    assert.equal(s.appVersion, '0.3.1');
    assert.equal(s.store, 'Xbox (Game Pass)');
    assert.equal(s.errors, 2);
    assert.equal(s.errorsSource, 'header');
    const counted = summariseLog('store=Steam\n12:00 INFO ok\n12:01 ERROR one\n12:02 [ERR] two\n12:03 error lower case ignored\n', '1.0.0');
    assert.equal(counted.appVersion, '1.0.0', 'the submitted appVersion wins');
    assert.equal(counted.store, 'Steam');
    assert.equal(counted.errors, 2);
    assert.equal(counted.errorsSource, 'counted');
});

test('validation: fields and binary refusal', () => {
    const ok = { installId: '0f8fad5b-d9cb-469f-a165-70867728950e', appVersion: '0.3.1', log: 'hello\r\n\tworld \u001b[31mred\u001b[0m' };
    assert.deepEqual(validateSubmission(ok), []);
    assert.equal(validateSubmission({ ...ok, installId: 'x' }).length, 1);
    assert.equal(validateSubmission({ ...ok, appVersion: '' }).length, 1);
    assert.equal(validateSubmission({ ...ok, appVersion: '1.0; rm -rf' }).length, 1);
    assert.equal(validateSubmission({ ...ok, log: '' }).length, 1);
    assert.equal(validateSubmission({ ...ok, log: 42 }).length, 1);
    assert.match(validateSubmission({ ...ok, log: 'MZ\u0000\u0003\u0000' })[0], /binary/);
});

test('codes: format, unambiguous alphabet, normalisation', () => {
    for (let i = 0; i < 500; i++) {
        const c = randomCode(4);
        assert.match(c, CODE_PATTERN);
        assert.doesNotMatch(c.slice(5), /[01OIL]/);
    }
    assert.equal(normaliseCode(' lotr-7f3k '), 'LOTR-7F3K');
    assert.equal(normaliseCode('7F3K'), 'LOTR-7F3K');
    assert.equal(normaliseCode('LOTR7F3K'), 'LOTR-7F3K');
    assert.equal(normaliseCode('LOTR-7F3KAB'), 'LOTR-7F3KAB');
    assert.equal(normaliseCode('LOTR-0O1I'), null, 'ambiguous characters never exist');
    assert.equal(normaliseCode('LOTR-ABC'), null);
    assert.equal(normaliseCode('LOTR-ABCDEFG'), null);
    assert.equal(normaliseCode('../../etc'), null);
});

test('store: collision-safe, grows the code when the 4-character space is crowded', () => {
    const dir = tmpDir();
    const store = new SupportStore({ dir, expiryHours: 72, codeLength: 4 });
    const realTaken = store.taken.bind(store);
    store.taken = (code) => code.length === 9 || realTaken(code); // every 4-character code "taken"
    const rec = store.save({ installId: 'abcdefgh', appVersion: '1', log: 'x', maskedCount: 0, summary: {} });
    assert.equal(rec.code.length, 10, 'LOTR- plus 5 characters');
    store.taken = realTaken;
    const codes = new Set();
    for (let i = 0; i < 200; i++) codes.add(store.save({ installId: 'abcdefgh', appVersion: '1', log: 'x', maskedCount: 0, summary: {} }).code);
    assert.equal(codes.size, 200);
});

test('store: one-time fetch, burn, expiry, purge', () => {
    const dir = tmpDir();
    const c = { t: 1_800_000_000_000 };
    const store = new SupportStore({ dir, expiryHours: 72, codeLength: 4 }, { now: () => c.t });
    const rec = store.save({ installId: 'abcdefgh', appVersion: '1', log: 'secret log', maskedCount: 0, summary: {} });
    const first = store.take(rec.code);
    assert.equal(first.status, 'ok');
    assert.equal(store.take(rec.code).status, 'busy', 'a concurrent fetch cannot get it twice');
    store.burn(rec.code, rec.expiresAt);
    assert.equal(store.take(rec.code).status, 'used');
    assert.equal(fs.readFileSync(path.join(dir, `${rec.code}.burnt`), 'utf8').includes('secret log'), false, 'tombstone holds no log');
    assert.equal(store.taken(rec.code), true, 'burnt code never reissued before its expiry');

    const old = store.save({ installId: 'abcdefgh', appVersion: '1', log: 'x', maskedCount: 0, summary: {} });
    c.t += 72 * 3600 * 1000 + 1;
    assert.equal(store.take(old.code).status, 'expired');
    assert.equal(store.take(old.code).status, 'unknown');
    const stale = store.save({ installId: 'abcdefgh', appVersion: '1', log: 'x', maskedCount: 0, summary: {} });
    c.t += 73 * 3600 * 1000;
    assert.ok(store.purge() >= 2, 'expired log and expired tombstone removed');
    assert.equal(fs.existsSync(path.join(dir, `${stale.code}.json`)), false);
    assert.equal(fs.existsSync(path.join(dir, `${rec.code}.burnt`)), false);
    assert.equal(store.take('LOTR-ZZZZ').status, 'unknown');
});

test('store: unreserve after a failed delivery keeps the code usable', () => {
    const store = new SupportStore({ dir: tmpDir(), expiryHours: 72, codeLength: 4 });
    const rec = store.save({ installId: 'abcdefgh', appVersion: '1', log: 'x', maskedCount: 0, summary: {} });
    assert.equal(store.take(rec.code).status, 'ok');
    store.unreserve(rec.code);
    assert.equal(store.take(rec.code).status, 'ok');
});

test('rate limiter: 3 per hour sliding window', () => {
    const c = { t: 0 };
    const rl = new RateLimiter({ limit: 3, windowMs: 3600000, now: () => c.t });
    for (let i = 0; i < 3; i++) { assert.equal(rl.retryAfter('k'), 0); rl.record('k'); c.t += 60000; }
    const wait = rl.retryAfter('k');
    assert.ok(wait > 3000 && wait <= 3600, `wait ${wait}`);
    assert.equal(rl.retryAfter('other'), 0, 'keys are independent');
    c.t = 3600000 + 1;
    assert.equal(rl.retryAfter('k'), 0, 'first hit left the window');
});
