'use strict';
// Community hub API end to end: the real HTTP server on a random loopback port, a temporary SQLite file, a fake clock.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { loadConfig } = require('../src/config');
const { createApp } = require('../src/app');
const { cleanText } = require('../src/hub');

const HUB_SECRET = 'hub-secret-0123456789abcdef';
const ALICE = '76561198000000001';
const BOB = '76561198000000002';
const CAROL = '76561198000000003';
const DAVE = '76561198000000004';

async function startHub(hubOverrides = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-hub-'));
    const clock = { t: 1_800_000_000_000 };
    const config = loadConfig({ argv: [], env: {}, overrides: { root, port: 0, siteUpdate: { enabled: false }, hub: { enabled: true, ...hubOverrides } } });
    const app = createApp(config, { now: () => clock.t, secret: 'support-secret-0123456789', hubSecret: HUB_SECRET });
    const addr = await app.start();
    const base = `http://127.0.0.1:${addr.port}`;
    const call = async (method, p, { body, headers = {} } = {}) => {
        const init = { method, headers: { ...headers } };
        if (body !== undefined) { init.body = JSON.stringify(body); init.headers['content-type'] = 'application/json'; }
        const res = await fetch(base + p, init);
        const text = await res.text();
        return { status: res.status, json: text ? JSON.parse(text) : null };
    };
    const internal = (method, p, body) => call(method, p, { body, headers: { 'x-lotr-hub-secret': HUB_SECRET } });
    const tokens = {};
    const login = async (steamId, name) => {
        const r = await internal('POST', '/internal/hub/session', { steamId, name, server: 'test' });
        assert.equal(r.status, 200, JSON.stringify(r.json));
        tokens[steamId] = r.json.token;
        return r.json;
    };
    const as = (steamId) => ({
        get: (p) => call('GET', p, { headers: { authorization: `Bearer ${tokens[steamId]}` } }),
        post: (p, body) => call('POST', p, { body, headers: { authorization: `Bearer ${tokens[steamId]}` } }),
    });
    return { app, config, clock, call, internal, login, as, root, stop: () => app.stop() };
}

/** Alice founds [FELL] (approved by an admin), Bob joins and is accepted. */
async function withClan(s) {
    await s.login(ALICE, 'Alice');
    await s.login(BOB, 'Bob');
    assert.equal((await s.as(ALICE).post('/api/hub/clans', { tag: 'FELL', name: 'Fellowship', faction: 'gondor' })).status, 201);
    assert.equal((await s.internal('POST', '/internal/hub/clans/FELL/approve', { by: 'admin' })).status, 200);
    const req = await s.as(BOB).post('/api/hub/clans/FELL/join');
    assert.equal(req.status, 201);
    assert.equal((await s.as(ALICE).post(`/api/hub/requests/${req.json.id}/accept`)).status, 200);
}

test('hub off by default: routes are 404 and no database is created', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lotr-hub-off-'));
    const config = loadConfig({ argv: [], env: {}, overrides: { root, port: 0, siteUpdate: { enabled: false } } });
    const app = createApp(config, { secret: 'support-secret-0123456789' });
    const addr = await app.start();
    try {
        const res = await fetch(`http://127.0.0.1:${addr.port}/api/hub/factions`);
        assert.equal(res.status, 404);
        assert.equal(fs.existsSync(path.join(root, 'data', 'hub')), false);
    } finally {
        await app.stop();
    }
});

test('identity: only a token minted by a game server authenticates; dev header refused unless enabled', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    assert.equal((await s.call('GET', '/api/hub/me')).status, 401);
    assert.equal((await s.call('GET', '/api/hub/me', { headers: { authorization: 'Bearer nope-nope-nope-nope-nope-nope-nope-nope' } })).status, 401);
    assert.equal((await s.call('GET', '/api/hub/me', { headers: { 'x-hub-dev-player': `${ALICE}|Alice` } })).status, 401);
    // internal routes need the secret
    assert.equal((await s.call('POST', '/internal/hub/session', { body: { steamId: ALICE, name: 'Alice' } })).status, 401);
    assert.equal((await s.call('POST', '/internal/hub/session', { body: { steamId: ALICE }, headers: { 'x-lotr-hub-secret': 'wrong' } })).status, 401);
    // through the proxy (X-Forwarded-For) the internal routes do not exist
    assert.equal((await s.call('POST', '/internal/hub/session', { body: { steamId: ALICE }, headers: { 'x-lotr-hub-secret': HUB_SECRET, 'x-forwarded-for': '1.2.3.4' } })).status, 404);
    assert.equal((await s.internal('POST', '/internal/hub/session', { steamId: '123', name: 'x' })).status, 400);
    const session = await s.login(ALICE, 'Alice\u0000‮ of Gondor');
    assert.ok(session.token.length >= 40);
    const me = await s.as(ALICE).get('/api/hub/me');
    assert.equal(me.status, 200);
    assert.equal(me.json.player.name, 'Alice of Gondor');
    assert.equal(me.json.clan, null);
    // the token expires
    s.clock.t += 61 * 86400000;
    assert.equal((await s.as(ALICE).get('/api/hub/me')).status, 401);
});

test('dev identity on: loopback header names a player (bench only)', async (t) => {
    const s = await startHub({ devIdentity: true });
    t.after(s.stop);
    const r = await s.call('GET', '/api/hub/me', { headers: { 'x-hub-dev-player': `${CAROL}|Carol` } });
    assert.equal(r.status, 200);
    assert.equal(r.json.player.name, 'Carol');
    assert.equal((await s.call('GET', '/api/hub/me', { headers: { 'x-hub-dev-player': 'bad|x' } })).status, 400);
    assert.equal((await s.call('GET', '/api/hub/me', { headers: { 'x-hub-dev-player': `${CAROL}|Carol`, 'x-forwarded-for': '9.9.9.9' } })).status, 401);
});

test('clan life: request, admin approval, join request accepted, public views hide Steam ids', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await s.login(ALICE, 'Alice');
    await s.login(BOB, 'Bob');
    assert.equal((await s.as(ALICE).post('/api/hub/clans', { tag: 'F!', name: 'Fellowship', faction: 'gondor' })).json.error, 'bad_tag');
    assert.equal((await s.as(ALICE).post('/api/hub/clans', { tag: 'FELL', name: 'Fe', faction: 'gondor' })).json.error, 'bad_name');
    assert.equal((await s.as(ALICE).post('/api/hub/clans', { tag: 'FELL', name: 'Fellowship', faction: 'narnia' })).json.error, 'bad_faction');
    const created = await s.as(ALICE).post('/api/hub/clans', { tag: 'FELL', name: 'Fellowship', faction: 'gondor', description: 'Nine walkers' });
    assert.equal(created.status, 201);
    assert.deepEqual(created.json, { tag: 'FELL', status: 'pending' });
    // pending clans are not public and cannot be joined
    assert.equal((await s.call('GET', '/api/hub/clans')).json.clans.length, 0);
    assert.equal((await s.as(BOB).post('/api/hub/clans/FELL/join')).status, 404);
    // the tag is reserved while pending, whatever the case
    assert.equal((await s.as(BOB).post('/api/hub/clans', { tag: 'fell', name: 'Other', faction: 'mordor' })).json.error, 'tag_taken');
    assert.equal((await s.internal('POST', '/internal/hub/clans/FELL/approve', { by: 'Admin' })).status, 200);
    const list = await s.call('GET', '/api/hub/clans');
    assert.equal(list.json.clans.length, 1);
    assert.equal(list.json.clans[0].faction.name, 'Gondor');
    assert.equal(list.json.clans[0].leader, 'Alice');
    const join = await s.as(BOB).post('/api/hub/clans/fell/join');
    assert.equal(join.status, 201);
    assert.equal((await s.as(BOB).post('/api/hub/clans/FELL/join')).json.error, 'request_pending');
    const aliceView = await s.as(ALICE).get('/api/hub/me');
    assert.equal(aliceView.json.requestsToDecide.length, 1);
    assert.equal(aliceView.json.requestsToDecide[0].name, 'Bob');
    // Bob cannot accept his own request
    assert.equal((await s.as(BOB).post(`/api/hub/requests/${join.json.id}/accept`)).status, 403);
    assert.equal((await s.as(ALICE).post(`/api/hub/requests/${join.json.id}/accept`)).status, 200);
    const bobView = await s.as(BOB).get('/api/hub/me');
    assert.equal(bobView.json.clan.tag, 'FELL');
    assert.equal(bobView.json.clan.role, 'member');
    assert.equal(bobView.json.clan.roster.length, 2);
    assert.equal(bobView.json.clan.roster[0].role, 'leader');
    const pub = await s.call('GET', '/api/hub/clans/FELL');
    assert.deepEqual(pub.json.clan.roster.map((m) => Object.keys(m).sort()), [['name', 'role'], ['name', 'role']]);
});

test('roles: officers decide requests, kick members only; leader hands over; leader leaves last and closes', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await withClan(s);
    await s.login(CAROL, 'Carol');
    await s.login(DAVE, 'Dave');
    // only the leader changes roles
    assert.equal((await s.as(BOB).post(`/api/hub/clan/members/${ALICE}/role`, { role: 'member' })).status, 403);
    assert.equal((await s.as(ALICE).post(`/api/hub/clan/members/${BOB}/role`, { role: 'officer' })).status, 200);
    // an officer accepts Carol
    const carol = await s.as(CAROL).post('/api/hub/clans/FELL/join');
    assert.equal((await s.as(BOB).post(`/api/hub/requests/${carol.json.id}/accept`)).status, 200);
    // an officer rejects Dave
    const dave = await s.as(DAVE).post('/api/hub/clans/FELL/join');
    assert.equal((await s.as(BOB).post(`/api/hub/requests/${dave.json.id}/reject`)).status, 200);
    assert.equal((await s.as(DAVE).get('/api/hub/me')).json.clan, null);
    // officer kicks a member, not the leader
    assert.equal((await s.as(BOB).post(`/api/hub/clan/members/${ALICE}/kick`)).status, 403);
    assert.equal((await s.as(BOB).post(`/api/hub/clan/members/${CAROL}/kick`)).status, 200);
    // the leader cannot leave while others remain; hands over to Bob, then leaves
    assert.equal((await s.as(ALICE).post('/api/hub/clan/leave')).json.error, 'leader_must_hand_over');
    assert.equal((await s.as(ALICE).post(`/api/hub/clan/members/${BOB}/role`, { role: 'leader' })).status, 200);
    assert.equal((await s.as(ALICE).get('/api/hub/me')).json.clan.role, 'officer');
    assert.equal((await s.as(ALICE).post('/api/hub/clan/leave')).status, 200);
    // Bob alone leaves: the clan closes and its tag is free again
    assert.equal((await s.as(BOB).post('/api/hub/clan/leave')).status, 200);
    assert.equal((await s.call('GET', '/api/hub/clans')).json.clans.length, 0);
    assert.equal((await s.as(CAROL).post('/api/hub/clans', { tag: 'FELL', name: 'Second fellowship', faction: 'rohan' })).status, 201);
});

test('faction: leader or officer, dated in the history, published as an event', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await withClan(s);
    assert.equal((await s.as(BOB).post('/api/hub/clan/faction', { faction: 'rohan' })).status, 403);
    assert.equal((await s.as(ALICE).post('/api/hub/clan/faction', { faction: 'shire' })).json.error, 'bad_faction');
    assert.equal((await s.as(ALICE).post('/api/hub/clan/faction', { faction: 'rohan' })).status, 200);
    assert.equal((await s.call('GET', '/api/hub/clans/FELL')).json.clan.faction.id, 'rohan');
    const events = (await s.internal('GET', '/internal/hub/events?after=0')).json;
    assert.deepEqual(events.events.map((e) => e.type), ['clan.requested', 'clan.approved', 'request.created', 'member.joined', 'faction.changed']);
    assert.equal(events.events.at(-1).from, 'gondor');
    const later = (await s.internal('GET', `/internal/hub/events?after=${events.last}`)).json;
    assert.equal(later.events.length, 0);
    assert.equal(later.last, events.last);
});

test('admin rejection frees the tag and the founder', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await s.login(ALICE, 'Alice');
    await s.as(ALICE).post('/api/hub/clans', { tag: 'BAD', name: 'Bad name', faction: 'mordor' });
    assert.equal((await s.internal('POST', '/internal/hub/clans/BAD/reject', { by: 'Admin', reason: 'name' })).status, 200);
    assert.equal((await s.as(ALICE).get('/api/hub/me')).json.clan, null);
    assert.equal((await s.as(ALICE).post('/api/hub/clans', { tag: 'BAD', name: 'Better name', faction: 'mordor' })).status, 201);
    assert.equal((await s.internal('POST', '/internal/hub/clans/NOPE/approve', {})).status, 404);
});

test('Discord link: bot code, single use, expires, one Discord account per player', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await s.login(ALICE, 'Alice');
    await s.login(BOB, 'Bob');
    assert.equal((await s.internal('POST', '/internal/hub/link-code', { discordId: 'abc' })).status, 400);
    const code = (await s.internal('POST', '/internal/hub/link-code', { discordId: '123456789012345678', discordName: 'alice#1' })).json;
    assert.match(code.code, /^LINK-[A-Z0-9]{6}$/);
    assert.equal((await s.as(ALICE).post('/api/hub/link', { code: 'LINK-ZZZZZZ' })).status, 404);
    const linked = await s.as(ALICE).post('/api/hub/link', { code: code.code.toLowerCase() });
    assert.equal(linked.status, 200);
    assert.equal(linked.json.discordName, 'alice#1');
    assert.equal((await s.as(BOB).post('/api/hub/link', { code: code.code })).status, 404);
    assert.equal((await s.internal('GET', '/internal/hub/players/by-discord/123456789012345678')).json.player.id, ALICE);
    // the same Discord account linked again (to Bob) moves the link
    const again = (await s.internal('POST', '/internal/hub/link-code', { discordId: '123456789012345678' })).json;
    await s.as(BOB).post('/api/hub/link', { code: again.code });
    assert.equal((await s.as(ALICE).get('/api/hub/me')).json.player.discordLinked, false);
    // expiry
    const old = (await s.internal('POST', '/internal/hub/link-code', { discordId: '223456789012345678' })).json;
    s.clock.t += 16 * 60000;
    assert.equal((await s.as(ALICE).post('/api/hub/link', { code: old.code })).status, 404);
});

test('limits: writes per minute, request expiry, sessions capped at five per player', async (t) => {
    const s = await startHub({ writesPerMinute: 3 });
    t.after(s.stop);
    await withClan(s); // Alice: 1 write (create) + 1 (accept); Bob: 1 (join)
    await s.as(ALICE).post('/api/hub/clan/faction', { faction: 'rohan' });
    assert.equal((await s.as(ALICE).post('/api/hub/clan/faction', { faction: 'gondor' })).status, 429);
    s.clock.t += 61000;
    assert.equal((await s.as(ALICE).post('/api/hub/clan/faction', { faction: 'gondor' })).status, 200);
    await s.login(CAROL, 'Carol');
    await s.as(CAROL).post('/api/hub/clans/FELL/join');
    s.clock.t += 15 * 86400000;
    s.app.sweep();
    assert.equal((await s.as(CAROL).get('/api/hub/me')).json.pendingRequest, null);
    for (let i = 0; i < 6; i++) await s.login(DAVE, 'Dave');
    assert.equal(s.app.hub.store.get('SELECT COUNT(*) AS n FROM sessions WHERE steam_id = ?', DAVE).n, 5);
});

test('internal clan list carries Discord ids and roles for the bot', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await withClan(s);
    const code = (await s.internal('POST', '/internal/hub/link-code', { discordId: '323456789012345678' })).json;
    await s.as(BOB).post('/api/hub/link', { code: code.code });
    const clans = (await s.internal('GET', '/internal/hub/clans')).json.clans;
    assert.equal(clans.length, 1);
    const bob = clans[0].roster.find((m) => m.id === BOB);
    assert.equal(bob.discordId, '323456789012345678');
    assert.equal(bob.role, 'member');
});

test('cleanText strips control and bidi characters, collapses spaces, caps length', () => {
    assert.equal(cleanText('  a\u0007b‮ c  ', 10), 'ab c');
    assert.equal(cleanText('x'.repeat(50), 5), 'xxxxx');
    assert.equal(cleanText('   ', 5), null);
    assert.equal(cleanText(42, 5), null);
});

test('import: an existing Discord clan comes in once its leader has linked his account', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await s.login(ALICE, 'Alice');
    const body = { tag: 'OLD', name: 'Old guard', faction: 'gondor', leaderDiscordId: '423456789012345678', by: 'Admin' };
    assert.equal((await s.internal('POST', '/internal/hub/clans/import', body)).json.error, 'leader_not_linked');
    const code = (await s.internal('POST', '/internal/hub/link-code', { discordId: '423456789012345678' })).json;
    await s.as(ALICE).post('/api/hub/link', { code: code.code });
    const imported = await s.internal('POST', '/internal/hub/clans/import', body);
    assert.equal(imported.status, 201);
    assert.deepEqual(imported.json, { tag: 'OLD', status: 'active' });
    const me = (await s.as(ALICE).get('/api/hub/me')).json;
    assert.equal(me.clan.tag, 'OLD');
    assert.equal(me.clan.role, 'leader');
    assert.equal((await s.internal('POST', '/internal/hub/clans/import', body)).json.error, 'already_in_clan');
    const events = (await s.internal('GET', '/internal/hub/events?after=0')).json.events;
    assert.equal(events.at(-1).type, 'clan.approved');
    assert.equal(events.at(-1).imported, true);
});

test('erasure (GDPR): the player disappears, his clan passes to an officer, histories keep no trace of him', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await withClan(s);
    await s.login(CAROL, 'Carol');
    const carol = await s.as(CAROL).post('/api/hub/clans/FELL/join');
    await s.as(ALICE).post(`/api/hub/requests/${carol.json.id}/accept`);
    await s.as(ALICE).post(`/api/hub/clan/members/${CAROL}/role`, { role: 'officer' });
    assert.equal((await s.internal('POST', '/internal/hub/players/forget', { steamId: '76561198999999999' })).status, 404);
    assert.equal((await s.internal('POST', '/internal/hub/players/forget', { steamId: ALICE, by: 'Admin' })).status, 200);
    // her token no longer works, the officer leads now
    assert.equal((await s.as(ALICE).get('/api/hub/me')).status, 401);
    const clan = (await s.call('GET', '/api/hub/clans/FELL')).json.clan;
    assert.equal(clan.leader, 'Carol');
    assert.equal(clan.members, 2);
    const db = s.app.hub.store;
    assert.equal(db.get('SELECT COUNT(*) AS n FROM players WHERE steam_id = ?', ALICE).n, 0);
    assert.equal(db.get('SELECT COUNT(*) AS n FROM clan_history WHERE by_player = ? OR detail LIKE ?', ALICE, `%${ALICE}%`).n, 0);
    assert.equal(db.get('SELECT COUNT(*) AS n FROM clans WHERE created_by = ?', ALICE).n, 0);
    // she can come back later as a new player
    await s.login(ALICE, 'Alice');
    assert.equal((await s.as(ALICE).get('/api/hub/me')).json.clan, null);
});

/** Two active clans: [FELL] led by Alice (Bob member), [MORG] led by Carol (Dave officer). */
async function withTwoClans(s) {
    await withClan(s);
    await s.login(CAROL, 'Carol');
    await s.login(DAVE, 'Dave');
    await s.as(CAROL).post('/api/hub/clans', { tag: 'MORG', name: 'Host of Morgul', faction: 'mordor' });
    await s.internal('POST', '/internal/hub/clans/MORG/approve', { by: 'admin' });
    const d = await s.as(DAVE).post('/api/hub/clans/MORG/join');
    await s.as(CAROL).post(`/api/hub/requests/${d.json.id}/accept`);
    await s.as(CAROL).post(`/api/hub/clan/members/${DAVE}/role`, { role: 'officer' });
}

test('matches: challenge, counter, accept, sign-ups capped, result reported and confirmed, events for the bot', async (t) => {
    const s = await startHub();
    t.after(s.stop);
    await withTwoClans(s);
    const inTwoDays = s.clock.t + 2 * 86400000;
    // a member cannot challenge; bad inputs refused
    assert.equal((await s.as(BOB).post('/api/hub/matches', { opponent: 'MORG', at: inTwoDays, mode: 'Battle', size: 2 })).status, 403);
    assert.equal((await s.as(ALICE).post('/api/hub/matches', { opponent: 'FELL', at: inTwoDays, mode: 'Battle', size: 2 })).json.error, 'same_clan');
    assert.equal((await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at: inTwoDays, mode: 'Chess', size: 2 })).json.error, 'bad_mode');
    assert.equal((await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at: s.clock.t + 60000, mode: 'Battle', size: 2 })).json.error, 'bad_date');
    const created = await s.as(ALICE).post('/api/hub/matches', { opponent: 'morg', at: new Date(inTwoDays).toISOString(), mode: 'Battle', size: 2, note: 'best of 3' });
    assert.equal(created.status, 201);
    const id = created.json.id;
    assert.equal(created.json.waitingFor, 'MORG');
    // FELL cannot answer its own proposal; Dave (MORG officer) counters with another date
    assert.equal((await s.as(ALICE).post(`/api/hub/matches/${id}/accept`)).json.error, 'not_waiting_for_you');
    const countered = await s.as(DAVE).post(`/api/hub/matches/${id}/counter`, { at: inTwoDays + 3600000 });
    assert.equal(countered.json.waitingFor, 'FELL');
    assert.equal((await s.as(BOB).post(`/api/hub/matches/${id}/accept`)).status, 403);
    assert.equal((await s.as(ALICE).post(`/api/hub/matches/${id}/accept`)).json.status, 'accepted');
    // sign-ups: two per side, toggle off and on
    assert.equal((await s.as(ALICE).post(`/api/hub/matches/${id}/signup`)).json.signedUp, true);
    assert.equal((await s.as(BOB).post(`/api/hub/matches/${id}/signup`)).json.signedUp, true);
    await s.login('76561198000000005', 'Eve');
    const eve = await s.as('76561198000000005').post('/api/hub/clans/FELL/join');
    await s.as(ALICE).post(`/api/hub/requests/${eve.json.id}/accept`);
    assert.equal((await s.as('76561198000000005').post(`/api/hub/matches/${id}/signup`)).json.error, 'lineup_full');
    assert.equal((await s.as(BOB).post(`/api/hub/matches/${id}/signup`)).json.signedUp, false);
    assert.equal((await s.as('76561198000000005').post(`/api/hub/matches/${id}/signup`)).json.signedUp, true);
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${id}/signup`)).json.signedUp, true);
    const me = (await s.as(ALICE).get('/api/hub/me')).json;
    assert.equal(me.matches.length, 1);
    assert.equal(me.matches[0].mine.signedUp, true);
    assert.deepEqual(me.matches[0].a.lineup.map((x) => x.name), ['Alice', 'Eve']);
    // no result before the start; after it, MORG reports, FELL confirms
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${id}/report`, { winner: 'MORG' })).json.error, 'not_started');
    s.clock.t = inTwoDays + 2 * 3600000;
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${id}/report`, { winner: 'GOND' })).json.error, 'bad_winner');
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${id}/report`, { winner: 'MORG', score: '2-1' })).json.status, 'reported');
    assert.equal((await s.as(DAVE).post(`/api/hub/matches/${id}/confirm`)).json.error, 'not_yours');
    const played = await s.as(ALICE).post(`/api/hub/matches/${id}/confirm`);
    assert.equal(played.json.status, 'played');
    assert.deepEqual(played.json.result, { winner: 'MORG', score: '2-1', reportedBy: 'MORG' });
    const types = (await s.internal('GET', '/internal/hub/events?after=0')).json.events.map((e) => e.type).filter((x) => x.startsWith('match.'));
    assert.deepEqual(types, ['match.proposed', 'match.countered', 'match.accepted', 'match.reported', 'match.played']);
    const pub = (await s.call('GET', '/api/hub/matches')).json.matches;
    assert.equal(pub.length, 1);
    assert.deepEqual(Object.keys(pub[0].a.lineup[0]), ['name']);
});

test('matches: decline, cancel before the start only, disputed result settled by the admins, open-match cap', async (t) => {
    const s = await startHub({ maxOpenMatchesPerClan: 2 });
    t.after(s.stop);
    await withTwoClans(s);
    const at = s.clock.t + 86400000;
    const m1 = (await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at, mode: 'Siege', size: 5 })).json.id;
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${m1}/decline`)).json.status, 'declined');
    const m2 = (await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at, mode: 'Battle', size: 5 })).json.id;
    const m3 = (await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at: at + 3600000, mode: 'Battle', size: 5 })).json.id;
    assert.equal((await s.as(ALICE).post('/api/hub/matches', { opponent: 'MORG', at, mode: 'Battle', size: 5 })).json.error, 'too_many_matches');
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${m3}/cancel`)).json.status, 'cancelled');
    await s.as(CAROL).post(`/api/hub/matches/${m2}/accept`);
    s.clock.t = at + 600000;
    assert.equal((await s.as(ALICE).post(`/api/hub/matches/${m2}/cancel`)).json.error, 'started');
    await s.as(ALICE).post(`/api/hub/matches/${m2}/report`, { winner: 'FELL' });
    assert.equal((await s.as(CAROL).post(`/api/hub/matches/${m2}/dispute`)).json.status, 'disputed');
    const settled = await s.internal('POST', `/internal/hub/matches/${m2}/settle`, { winner: 'draw', score: '1-1', by: 'Admin' });
    assert.equal(settled.status, 200);
    assert.deepEqual(settled.json.result.winner, 'draw');
    assert.equal(settled.json.status, 'played');
});
