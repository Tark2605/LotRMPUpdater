'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { DownloadQueue, ipKey } = require('../src/queue');

function clock(start = 1_800_000_000_000) {
    const c = { t: start, now: () => c.t, advance: (sec) => { c.t += sec * 1000; } };
    return c;
}
const id = (n) => `install-${String(n).padStart(4, '0')}`;

function makeQueue(opts = {}, c = clock()) {
    return { q: new DownloadQueue({ maxConcurrent: 2, slotTtlSec: 60, waitTtlSec: 30, retryAfterMinSec: 5, retryAfterMaxSec: 30, maxWaitingPerIp: 3, ...opts }, { now: c.now }), c };
}

test('grants up to maxConcurrent, then queues with position and retryAfter', () => {
    const { q } = makeQueue();
    assert.equal(q.requestSlot({ kind: 'core', client: id(1) }).granted, true);
    assert.equal(q.requestSlot({ kind: 'core', client: id(2) }).granted, true);
    const third = q.requestSlot({ kind: 'core', client: id(3) });
    assert.equal(third.granted, false);
    assert.equal(third.position, 1);
    assert.ok(third.retryAfter >= 5 && third.retryAfter <= 30);
    const fourth = q.requestSlot({ kind: 'core', client: id(4) });
    assert.equal(fourth.position, 2);
});

test('one slot per install id: asking again returns the same token', () => {
    const { q } = makeQueue();
    const a = q.requestSlot({ kind: 'core', client: id(1) });
    const b = q.requestSlot({ kind: 'core', client: id(1) });
    assert.equal(a.token, b.token);
    assert.equal(q.stats().core.active, 1);
});

test('one place per install id in the line: polling keeps the position', () => {
    const { q } = makeQueue({ maxConcurrent: 1 });
    q.requestSlot({ kind: 'core', client: id(1) });
    assert.equal(q.requestSlot({ kind: 'core', client: id(2) }).position, 1);
    assert.equal(q.requestSlot({ kind: 'core', client: id(3) }).position, 2);
    assert.equal(q.requestSlot({ kind: 'core', client: id(2) }).position, 1);
    assert.equal(q.stats().core.waiting, 2);
});

test('FIFO: a freed slot goes to the head of the line, a newcomer cannot overtake', () => {
    const { q } = makeQueue({ maxConcurrent: 1 });
    const first = q.requestSlot({ kind: 'core', client: id(1) });
    q.requestSlot({ kind: 'core', client: id(2) }); // waiting #1
    q.requestSlot({ kind: 'core', client: id(3) }); // waiting #2
    assert.equal(q.release(first.token), true);
    const newcomer = q.requestSlot({ kind: 'core', client: id(9) });
    assert.equal(newcomer.granted, false, 'newcomer must wait behind the line');
    assert.equal(newcomer.position, 3);
    const third = q.requestSlot({ kind: 'core', client: id(3) });
    assert.equal(third.granted, false, 'second in line cannot take the head place');
    const head = q.requestSlot({ kind: 'core', client: id(2) });
    assert.equal(head.granted, true);
    assert.equal(q.requestSlot({ kind: 'core', client: id(3) }).position, 1);
});

test('two free slots: the first two waiting clients both get in', () => {
    const { q } = makeQueue({ maxConcurrent: 2 });
    const a = q.requestSlot({ kind: 'core', client: id(1) });
    const b = q.requestSlot({ kind: 'core', client: id(2) });
    q.requestSlot({ kind: 'core', client: id(3) });
    q.requestSlot({ kind: 'core', client: id(4) });
    q.release(a.token); q.release(b.token);
    assert.equal(q.requestSlot({ kind: 'core', client: id(4) }).granted, true, 'position 2 with two free slots');
    assert.equal(q.requestSlot({ kind: 'core', client: id(3) }).granted, true);
});

test('slot expires without heartbeat; heartbeat keeps it alive', () => {
    const { q, c } = makeQueue({ maxConcurrent: 1 });
    const a = q.requestSlot({ kind: 'core', client: id(1) });
    c.advance(50);
    assert.ok(q.heartbeat(a.token));
    c.advance(50);
    assert.equal(q.check(a.token), true, 'still alive 100 s after grant thanks to the heartbeat');
    c.advance(61);
    assert.equal(q.heartbeat(a.token), null, 'expired after 61 s of silence');
    assert.equal(q.stats().core.active, 0);
    assert.equal(q.requestSlot({ kind: 'core', client: id(2) }).granted, true, 'slot reusable after expiry');
});

test('a waiting client that stops polling loses its place', () => {
    const { q, c } = makeQueue({ maxConcurrent: 1 });
    q.requestSlot({ kind: 'core', client: id(1) });
    q.requestSlot({ kind: 'core', client: id(2) });
    q.requestSlot({ kind: 'core', client: id(3) });
    c.advance(20);
    q.requestSlot({ kind: 'core', client: id(3) }); // id(3) polls, id(2) does not
    q.heartbeat(q.kinds.core.byClient.get(id(1)));
    c.advance(15);
    assert.equal(q.requestSlot({ kind: 'core', client: id(3) }).position, 1, 'id(2) dropped after waitTtl');
});

test('one IP cannot flood the line with install ids', () => {
    const { q } = makeQueue({ maxConcurrent: 1, maxWaitingPerIp: 2 });
    q.requestSlot({ kind: 'core', client: id(1), ip: '1.1.1.1' });
    assert.equal(q.requestSlot({ kind: 'core', client: id(2), ip: '9.9.9.9' }).position, 1);
    assert.equal(q.requestSlot({ kind: 'core', client: id(3), ip: '9.9.9.9' }).position, 2);
    const refused = q.requestSlot({ kind: 'core', client: id(4), ip: '9.9.9.9' });
    assert.equal(refused.position, null);
    assert.equal(refused.reason, 'too_many_waiting_from_ip');
    assert.equal(q.requestSlot({ kind: 'core', client: id(5), ip: '8.8.8.8' }).position, 3);
});

test('release is idempotent and unknown tokens are refused', () => {
    const { q } = makeQueue();
    const a = q.requestSlot({ kind: 'core', client: id(1) });
    assert.equal(q.release(a.token), true);
    assert.equal(q.release(a.token), false);
    assert.equal(q.check('q_nope'), false);
    assert.throws(() => q.requestSlot({ kind: 'nope', client: id(1) }), /unknown kind/);
});

test('crash-safe: state saved and loaded keeps slots and the line, drops what expired', () => {
    const c = clock();
    const { q } = makeQueue({ maxConcurrent: 1 }, c);
    const a = q.requestSlot({ kind: 'core', client: id(1) });
    q.requestSlot({ kind: 'core', client: id(2) });
    const saved = JSON.parse(JSON.stringify(q.toJSON()));
    const { q: q2 } = makeQueue({ maxConcurrent: 1 }, c);
    assert.equal(q2.load(saved), 2);
    assert.equal(q2.check(a.token), true);
    assert.equal(q2.requestSlot({ kind: 'core', client: id(2) }).position, 1);
    c.advance(120);
    const { q: q3 } = makeQueue({ maxConcurrent: 1 }, c);
    q3.load(saved);
    assert.equal(q3.stats().core.active, 0);
    assert.equal(q3.stats().core.waiting, 0);
});

test('client id validation', () => {
    assert.equal(DownloadQueue.validClient('0f8fad5b-d9cb-469f-a165-70867728950e'), true);
    assert.equal(DownloadQueue.validClient('short'), false);
    assert.equal(DownloadQueue.validClient('has space in it'), false);
    assert.equal(DownloadQueue.validClient(42), false);
});

test('one address cannot hold every slot with made-up install ids', () => {
    const { q } = makeQueue({ maxConcurrent: 10, maxActivePerIp: 2 });
    assert.equal(q.requestSlot({ kind: 'core', client: id(1), ip: '203.0.113.5' }).granted, true);
    assert.equal(q.requestSlot({ kind: 'core', client: id(2), ip: '203.0.113.5' }).granted, true);
    const third = q.requestSlot({ kind: 'core', client: id(3), ip: '203.0.113.5' });
    assert.equal(third.granted, false);
    assert.equal(third.reason, 'too_many_active_from_ip');
    // Same /64 in IPv6 counts as one address; another address still gets a slot.
    assert.equal(q.requestSlot({ kind: 'core', client: id(4), ip: '2001:db8:0:1::10' }).granted, true);
    assert.equal(q.requestSlot({ kind: 'core', client: id(5), ip: '2001:db8:0:1::20' }).granted, true);
    assert.equal(q.requestSlot({ kind: 'core', client: id(6), ip: '2001:db8:0:1:ffff::1' }).granted, false);
    assert.equal(q.requestSlot({ kind: 'core', client: id(7), ip: '198.51.100.7' }).granted, true);
    // The slot already held is refreshed, never refused.
    assert.equal(q.requestSlot({ kind: 'core', client: id(1), ip: '203.0.113.5' }).granted, true);
    assert.equal(ipKey('::ffff:203.0.113.5'), '203.0.113.5');
});
