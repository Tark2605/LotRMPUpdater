'use strict';
// Community hub API (bannerlordmodagent docs/COMMUNITY-HUB-PLAN.md, phases 2-3): player accounts, clans with a
// faction of choice, join requests decided by the leader or an officer, Discord linking, and an event log that the
// Discord bot reads to keep roles in step. One SQLite file (node:sqlite, built into Node 24), no dependency.
//
// Identity. A player is his Steam ID 64, authenticated by TaleWorlds when he joins one of our game servers: the game
// server (loopback, shared secret) asks for a hub token for him and hands it to his game client. The hub module sends
// it as "Authorization: Bearer <token>". Only the token's SHA-256 is stored. The Steam ID or name a client sends on
// its own is never trusted. Dev only: with hub.devIdentity on, loopback requests may name a player in X-Hub-Dev-Player.
//
// Public routes (through Caddy, /api/hub/...):
//   GET  /api/hub/factions                     list of factions (config)
//   GET  /api/hub/clans                        active clans (tag, name, faction, members, leader name)
//   GET  /api/hub/clans/<tag>                  one clan: members' names and roles
//   GET  /api/hub/me                     auth  player, clan (with member ids for its members), pending request,
//                                              requests to decide (leader/officers)
//   POST /api/hub/clans                  auth  { tag, name, faction, description? } -> clan "pending" (admin approval)
//   POST /api/hub/clans/<tag>/join       auth  -> join request
//   POST /api/hub/requests/<id>/accept   auth  leader or officer of that clan
//   POST /api/hub/requests/<id>/reject   auth  leader or officer of that clan
//   POST /api/hub/requests/<id>/cancel   auth  the player who asked
//   POST /api/hub/clan/leave             auth  member or officer (a leader hands over first; alone, the clan closes)
//   POST /api/hub/clan/members/<id>/role auth  leader: { role: "officer" | "member" | "leader" } (leader = hand over)
//   POST /api/hub/clan/members/<id>/kick auth  leader (anyone), officer (members only)
//   POST /api/hub/clan/faction           auth  leader or officer: { faction }
//   POST /api/hub/link                   auth  { code } from the bot's /hub link: ties the Discord account
// Matches between clans (phase 3):
//   GET  /api/hub/matches                      upcoming and recent matches (public: clans, date, mode, size, status)
//   POST /api/hub/matches                auth  leader/officer: { opponent, at, mode, size, note? } -> challenge
//   POST /api/hub/matches/<id>/accept    auth  leader/officer of the clan the proposal waits for
//   POST /api/hub/matches/<id>/decline   auth  same
//   POST /api/hub/matches/<id>/counter   auth  same: { at } -> new date, now waiting for the other clan
//   POST /api/hub/matches/<id>/cancel    auth  leader/officer of either clan, before it is played
//   POST /api/hub/matches/<id>/signup    auth  member of either clan: toggles his place in the lineup (cap: size)
//   POST /api/hub/matches/<id>/report    auth  leader/officer, after the start: { winner: tag | "draw", score? }
//   POST /api/hub/matches/<id>/confirm   auth  leader/officer of the other clan: the result stands
//   POST /api/hub/matches/<id>/dispute   auth  same: the admins settle it
// Internal routes (loopback, not proxied, header X-LotR-Hub-Secret):
//   POST /internal/hub/session                 { steamId, name, server? } -> { token, expiresAt }   (game server)
//   POST /internal/hub/link-code               { discordId, discordName? } -> { code, expiresAt }    (bot)
//   GET  /internal/hub/events?after=<id>       -> { events, last }                                  (bot)
//   GET  /internal/hub/clans                   every clan with members, roles, Discord ids          (bot)
//   POST /internal/hub/clans/<tag>/approve     { by }                                               (bot, admins)
//   POST /internal/hub/clans/<tag>/reject      { by, reason? }                                      (bot, admins)
//   GET  /internal/hub/players/by-discord/<id> -> player and clan                                   (bot)
//   GET  /internal/hub/matches                 every match with lineups' Discord ids               (bot)
//   POST /internal/hub/matches/<id>/settle     { winner, score?, by } -> admins settle a disputed result (bot)
//   POST /internal/hub/players/forget          { steamId? | discordId?, by } -> erases a player (GDPR request, bot admins):
//                                              sessions, link, requests, membership (a leader's clan passes to an
//                                              officer, else the oldest member, else closes), name in histories
//   POST /internal/hub/clans/import            { tag, name, faction, leaderDiscordId, by } -> active clan (bot, admins:
//                                              brings an existing Discord clan in once its leader has linked his account)
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const { isLoopback, isProxied, sendJson, readJson, HttpError, safeEqual } = require('./http-util');
const { silentLogger } = require('./log');

const SECRET_HEADER = 'x-lotr-hub-secret';
const DEV_HEADER = 'x-hub-dev-player';
const STEAM_ID = /^7656119\d{10}$/;
const TAG = /^[A-Za-z0-9]{2,6}$/;
const ROLES = ['leader', 'officer', 'member'];

const DEFAULT_FACTIONS = [
    { id: 'gondor', name: 'Gondor', side: 'good' },
    { id: 'rohan', name: 'Rohan', side: 'good' },
    { id: 'elves', name: 'Elves', side: 'good' },
    { id: 'arnor', name: 'Arnor', side: 'good' },
    { id: 'dwarves', name: 'Dwarves', side: 'good' },
    { id: 'mordor', name: 'Mordor', side: 'evil' },
    { id: 'rhun', name: 'Rhun', side: 'evil' },
    { id: 'harad', name: 'Harad', side: 'evil' },
    { id: 'isengard', name: 'Isengard', side: 'evil' },
    { id: 'dunland', name: 'Dunland', side: 'evil' },
    { id: 'dol_guldur', name: 'Dol Guldur', side: 'evil' },
    { id: 'gundabad', name: 'Gundabad', side: 'evil' },
];

// --- small helpers ------------------------------------------------------------------------------------------------

const sha256 = (s) => crypto.createHash('sha256').update(s).digest('hex');

/** Printable text, control characters removed, trimmed, at most `max` characters; null when empty. */
function cleanText(value, max) {
    if (typeof value !== 'string') return null;
    // eslint-disable-next-line no-control-regex
    const s = value.replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮⁦-⁩]/g, '').replace(/\s+/g, ' ').trim();
    if (!s) return null;
    return [...s].slice(0, max).join('');
}

function loadHubSecret(file, env = process.env) {
    if (env.LOTR_HUB_SECRET) return env.LOTR_HUB_SECRET.trim();
    if (fs.existsSync(file)) {
        const secret = fs.readFileSync(file, 'utf8').trim();
        if (secret.length < 16) throw new Error(`${file}: secret shorter than 16 characters`);
        return secret;
    }
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const secret = crypto.randomBytes(32).toString('base64url');
    fs.writeFileSync(file, `${secret}\n`, { flag: 'wx' });
    return secret;
}

/** Fixed-window limiter per key (writes per player). */
class WindowLimiter {
    constructor(limit, windowMs, now) { this.limit = limit; this.windowMs = windowMs; this.now = now; this.map = new Map(); }
    take(key) {
        const t = this.now();
        const slot = this.map.get(key);
        if (!slot || t - slot.start >= this.windowMs) { this.map.set(key, { start: t, count: 1 }); return true; }
        if (slot.count >= this.limit) return false;
        slot.count++;
        return true;
    }
    prune() { const t = this.now(); for (const [k, v] of this.map) if (t - v.start >= this.windowMs) this.map.delete(k); }
}

// --- schema -------------------------------------------------------------------------------------------------------

const MIGRATIONS = [
    `CREATE TABLE players (
        steam_id     TEXT PRIMARY KEY,
        name         TEXT NOT NULL,
        discord_id   TEXT UNIQUE,
        discord_name TEXT,
        created_at   INTEGER NOT NULL,
        last_seen_at INTEGER NOT NULL
    );
    CREATE TABLE sessions (
        token_hash TEXT PRIMARY KEY,
        steam_id   TEXT NOT NULL REFERENCES players(steam_id),
        server     TEXT,
        created_at INTEGER NOT NULL,
        expires_at INTEGER NOT NULL
    );
    CREATE INDEX sessions_player ON sessions(steam_id);
    CREATE TABLE clans (
        id          INTEGER PRIMARY KEY,
        tag         TEXT NOT NULL,
        tag_key     TEXT NOT NULL,
        name        TEXT NOT NULL,
        faction     TEXT NOT NULL,
        description TEXT,
        status      TEXT NOT NULL CHECK (status IN ('pending','active','rejected','closed')),
        created_by  TEXT NOT NULL,
        created_at  INTEGER NOT NULL,
        decided_by  TEXT,
        decided_at  INTEGER,
        reason      TEXT
    );
    CREATE UNIQUE INDEX clans_live_tag ON clans(tag_key) WHERE status IN ('pending','active');
    CREATE TABLE members (
        clan_id   INTEGER NOT NULL REFERENCES clans(id),
        steam_id  TEXT NOT NULL UNIQUE REFERENCES players(steam_id),
        role      TEXT NOT NULL CHECK (role IN ('leader','officer','member')),
        joined_at INTEGER NOT NULL,
        PRIMARY KEY (clan_id, steam_id)
    );
    CREATE TABLE requests (
        id         INTEGER PRIMARY KEY,
        clan_id    INTEGER NOT NULL REFERENCES clans(id),
        steam_id   TEXT NOT NULL REFERENCES players(steam_id),
        status     TEXT NOT NULL CHECK (status IN ('pending','accepted','rejected','cancelled','expired')),
        created_at INTEGER NOT NULL,
        decided_by TEXT,
        decided_at INTEGER
    );
    CREATE UNIQUE INDEX requests_one_pending ON requests(steam_id) WHERE status = 'pending';
    CREATE TABLE link_codes (
        code_hash    TEXT PRIMARY KEY,
        discord_id   TEXT NOT NULL,
        discord_name TEXT,
        expires_at   INTEGER NOT NULL
    );
    CREATE TABLE events (
        id         INTEGER PRIMARY KEY,
        type       TEXT NOT NULL,
        payload    TEXT NOT NULL,
        created_at INTEGER NOT NULL
    );
    CREATE TABLE clan_history (
        id        INTEGER PRIMARY KEY,
        clan_id   INTEGER NOT NULL REFERENCES clans(id),
        what      TEXT NOT NULL,
        detail    TEXT,
        by_player TEXT,
        at        INTEGER NOT NULL
    );`,
    `CREATE TABLE matches (
        id          INTEGER PRIMARY KEY,
        clan_a      INTEGER NOT NULL REFERENCES clans(id),
        clan_b      INTEGER NOT NULL REFERENCES clans(id),
        waiting_for INTEGER REFERENCES clans(id),
        status      TEXT NOT NULL CHECK (status IN ('proposed','accepted','declined','cancelled','reported','played','disputed')),
        starts_at   INTEGER NOT NULL,
        mode        TEXT NOT NULL,
        size        INTEGER NOT NULL,
        note        TEXT,
        winner      INTEGER,
        draw        INTEGER NOT NULL DEFAULT 0,
        score       TEXT,
        reported_by INTEGER,
        created_by  TEXT NOT NULL,
        created_at  INTEGER NOT NULL,
        updated_at  INTEGER NOT NULL
    );
    CREATE INDEX matches_start ON matches(starts_at);
    CREATE TABLE lineups (
        match_id  INTEGER NOT NULL REFERENCES matches(id),
        clan_id   INTEGER NOT NULL REFERENCES clans(id),
        steam_id  TEXT NOT NULL,
        joined_at INTEGER NOT NULL,
        PRIMARY KEY (match_id, steam_id)
    );`,
];

const MATCH_MODES = ['Battle', 'Siege', 'Skirmish', 'Captain', 'TeamDeathmatch', 'Duel'];

class HubStore {
    constructor(file, { now = () => Date.now() } = {}) {
        if (file !== ':memory:') fs.mkdirSync(path.dirname(file), { recursive: true });
        this.db = new DatabaseSync(file);
        this.now = now;
        this.db.exec('PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;');
        this.db.exec('CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)');
        const row = this.db.prepare('SELECT version FROM schema_version').get();
        let version = row ? row.version : 0;
        if (!row) this.db.prepare('INSERT INTO schema_version (version) VALUES (0)').run();
        for (; version < MIGRATIONS.length; version++) {
            this.tx(() => {
                this.db.exec(MIGRATIONS[version]);
                this.db.prepare('UPDATE schema_version SET version = ?').run(version + 1);
            });
        }
    }

    tx(fn) {
        this.db.exec('BEGIN IMMEDIATE');
        try {
            const result = fn();
            this.db.exec('COMMIT');
            return result;
        } catch (error) {
            try { this.db.exec('ROLLBACK'); } catch { /* already rolled back */ }
            throw error;
        }
    }

    get(sql, ...args) { return this.db.prepare(sql).get(...args); }
    all(sql, ...args) { return this.db.prepare(sql).all(...args); }
    run(sql, ...args) { return this.db.prepare(sql).run(...args); }

    event(type, payload) {
        this.run('INSERT INTO events (type, payload, created_at) VALUES (?, ?, ?)', type, JSON.stringify(payload), this.now());
    }

    history(clanId, what, detail, by) {
        this.run('INSERT INTO clan_history (clan_id, what, detail, by_player, at) VALUES (?, ?, ?, ?, ?)', clanId, what, detail ?? null, by ?? null, this.now());
    }

    close() { this.db.close(); }
}

// --- service ------------------------------------------------------------------------------------------------------

function createHub(config, { now = () => Date.now(), logger = silentLogger, env = process.env, secret = null } = {}) {
    const hubConfig = config.hub;
    const log = logger;
    const store = new HubStore(hubConfig.dbFile, { now });
    const hubSecret = secret ?? loadHubSecret(hubConfig.secretFile, env);
    const factions = Array.isArray(hubConfig.factions) && hubConfig.factions.length ? hubConfig.factions : DEFAULT_FACTIONS;
    const factionIds = new Set(factions.map((f) => f.id));
    const writes = new WindowLimiter(hubConfig.writesPerMinute, 60000, now);
    const tokenTtlMs = hubConfig.tokenTtlDays * 86400000;
    if (hubConfig.devIdentity) log.warn('hub.dev_identity.enabled', { note: 'X-Hub-Dev-Player accepted from loopback: never in production' });

    // --- identity ---
    const authPlayer = (req) => {
        const header = req.headers.authorization;
        if (typeof header === 'string' && header.startsWith('Bearer ')) {
            const token = header.slice(7).trim();
            if (token.length >= 32) {
                const row = store.get('SELECT s.steam_id, s.expires_at FROM sessions s WHERE s.token_hash = ?', sha256(token));
                if (row && row.expires_at > now()) return row.steam_id;
            }
            throw new HttpError(401, 'unauthorised', 'hub token missing, unknown or expired: join an event server to get a new one');
        }
        if (hubConfig.devIdentity && isLoopback(req.socket.remoteAddress) && !isProxied(req) && typeof req.headers[DEV_HEADER] === 'string') {
            const [steamId, ...nameParts] = String(req.headers[DEV_HEADER]).split('|');
            if (!STEAM_ID.test(steamId)) throw new HttpError(400, 'bad_dev_player', 'X-Hub-Dev-Player: <steamid64>|<name>');
            ensurePlayer(steamId, cleanText(nameParts.join('|'), 64) || `Player ${steamId.slice(-4)}`);
            return steamId;
        }
        throw new HttpError(401, 'unauthorised', 'hub token missing: join an event server to activate the hub');
    };

    const ensurePlayer = (steamId, name) => {
        const t = now();
        const existing = store.get('SELECT steam_id FROM players WHERE steam_id = ?', steamId);
        if (existing) store.run('UPDATE players SET name = ?, last_seen_at = ? WHERE steam_id = ?', name, t, steamId);
        else store.run('INSERT INTO players (steam_id, name, created_at, last_seen_at) VALUES (?, ?, ?, ?)', steamId, name, t, t);
    };

    const requireInternal = (req) => {
        if (!isLoopback(req.socket.remoteAddress) || isProxied(req)) throw new HttpError(404, 'not_found');
        const given = req.headers[SECRET_HEADER];
        if (typeof given !== 'string' || !safeEqual(given, hubSecret)) {
            log.warn('hub.internal.unauthorised', { ip: req.socket.remoteAddress });
            throw new HttpError(401, 'unauthorised', 'missing or wrong X-LotR-Hub-Secret');
        }
    };

    const limitWrite = (steamId) => {
        if (!writes.take(steamId)) throw new HttpError(429, 'rate_limited', 'too many changes, wait a minute');
    };

    // --- reads ---
    const membership = (steamId) => store.get(
        `SELECT m.role, c.id, c.tag, c.name, c.faction, c.status FROM members m JOIN clans c ON c.id = m.clan_id WHERE m.steam_id = ?`, steamId);

    const clanByTag = (tag, statuses = ['active']) => {
        if (typeof tag !== 'string' || !TAG.test(tag)) return null;
        const row = store.get('SELECT * FROM clans WHERE tag_key = ? ORDER BY id DESC LIMIT 1', tag.toUpperCase());
        return row && statuses.includes(row.status) ? row : null;
    };

    const members = (clanId, withIds) => store.all(
        `SELECT p.steam_id, p.name, m.role, m.joined_at FROM members m JOIN players p ON p.steam_id = m.steam_id
         WHERE m.clan_id = ? ORDER BY CASE m.role WHEN 'leader' THEN 0 WHEN 'officer' THEN 1 ELSE 2 END, p.name COLLATE NOCASE`, clanId)
        .map((r) => (withIds ? { id: r.steam_id, name: r.name, role: r.role, joinedAt: r.joined_at } : { name: r.name, role: r.role }));

    /** Discord id of a player (null when not linked): carried in the events so the bot can act on the right member. */
    const discordOf = (steamId) => (store.get('SELECT discord_id FROM players WHERE steam_id = ?', steamId) || {}).discord_id || null;

    const factionOf = (id) => factions.find((f) => f.id === id) || { id, name: id, side: null };

    const clanSummary = (c) => {
        const count = store.get('SELECT COUNT(*) AS n FROM members WHERE clan_id = ?', c.id).n;
        const leader = store.get(`SELECT p.name FROM members m JOIN players p ON p.steam_id = m.steam_id WHERE m.clan_id = ? AND m.role = 'leader'`, c.id);
        return { tag: c.tag, name: c.name, faction: factionOf(c.faction), description: c.description, members: count, leader: leader ? leader.name : null, status: c.status };
    };

    const me = (steamId) => {
        const player = store.get('SELECT steam_id, name, discord_id, discord_name FROM players WHERE steam_id = ?', steamId);
        const m = membership(steamId);
        let clan = null;
        let toDecide = [];
        if (m) {
            clan = { ...clanSummary(store.get('SELECT * FROM clans WHERE id = ?', m.id)), role: m.role, roster: members(m.id, true) };
            if (m.role === 'leader' || m.role === 'officer') {
                toDecide = store.all(
                    `SELECT r.id, p.name, r.created_at FROM requests r JOIN players p ON p.steam_id = r.steam_id
                     WHERE r.clan_id = ? AND r.status = 'pending' ORDER BY r.created_at`, m.id)
                    .map((r) => ({ id: r.id, name: r.name, createdAt: r.created_at }));
            }
        }
        const pending = store.get(
            `SELECT r.id, c.tag, c.name, r.created_at FROM requests r JOIN clans c ON c.id = r.clan_id WHERE r.steam_id = ? AND r.status = 'pending'`, steamId);
        const matches = m ? store.all(
            `SELECT * FROM matches WHERE (clan_a = ? OR clan_b = ?) AND status NOT IN ('declined','cancelled') AND (starts_at >= ? OR status IN ('reported','disputed')) ORDER BY starts_at LIMIT 20`,
            m.id, m.id, now() - 7 * 86400000).map((row) => {
            const v = matchView(row, true);
            v.mine = { signedUp: [...v.a.lineup, ...v.b.lineup].some((x) => x.id === steamId), side: row.clan_a === m.id ? 'a' : 'b' };
            return v;
        }) : [];
        return {
            matches,
            player: { id: player.steam_id, name: player.name, discordLinked: Boolean(player.discord_id), discordName: player.discord_name },
            clan,
            pendingRequest: pending ? { id: pending.id, tag: pending.tag, name: pending.name, createdAt: pending.created_at } : null,
            requestsToDecide: toDecide,
        };
    };

    // --- writes (each in one transaction) ---
    const createClan = (steamId, body) => store.tx(() => {
        if (membership(steamId)) throw new HttpError(409, 'already_in_clan', 'leave your clan before founding one');
        const tag = typeof body.tag === 'string' ? body.tag.trim() : '';
        if (!TAG.test(tag)) throw new HttpError(400, 'bad_tag', 'tag: 2 to 6 letters or digits');
        const name = cleanText(body.name, 32);
        if (!name || [...name].length < 3) throw new HttpError(400, 'bad_name', 'name: 3 to 32 characters');
        if (!factionIds.has(body.faction)) throw new HttpError(400, 'bad_faction', 'unknown faction');
        const description = cleanText(body.description, 300);
        if (store.get(`SELECT id FROM clans WHERE tag_key = ? AND status IN ('pending','active')`, tag.toUpperCase())) {
            throw new HttpError(409, 'tag_taken', 'this tag is already used');
        }
        const t = now();
        const result = store.run(
            `INSERT INTO clans (tag, tag_key, name, faction, description, status, created_by, created_at) VALUES (?, ?, ?, ?, ?, 'pending', ?, ?)`,
            tag, tag.toUpperCase(), name, body.faction, description, steamId, t);
        const clanId = Number(result.lastInsertRowid);
        store.run(`INSERT INTO members (clan_id, steam_id, role, joined_at) VALUES (?, ?, 'leader', ?)`, clanId, steamId, t);
        store.run(`UPDATE requests SET status = 'cancelled', decided_at = ? WHERE steam_id = ? AND status = 'pending'`, t, steamId);
        store.history(clanId, 'founded', `${tag} ${name}, ${body.faction}`, steamId);
        store.event('clan.requested', { tag, name, faction: body.faction, leader: steamId });
        log.info('hub.clan.requested', { tag, leader: steamId });
        return { tag, status: 'pending' };
    });

    const requestJoin = (steamId, tag) => store.tx(() => {
        if (membership(steamId)) throw new HttpError(409, 'already_in_clan', 'leave your clan first');
        const clan = clanByTag(tag);
        if (!clan) throw new HttpError(404, 'unknown_clan', 'no active clan with this tag');
        if (store.get(`SELECT id FROM requests WHERE steam_id = ? AND status = 'pending'`, steamId)) {
            throw new HttpError(409, 'request_pending', 'you already have a pending request: cancel it first');
        }
        const result = store.run(`INSERT INTO requests (clan_id, steam_id, status, created_at) VALUES (?, ?, 'pending', ?)`, clan.id, steamId, now());
        store.event('request.created', { tag: clan.tag, player: steamId });
        return { id: Number(result.lastInsertRowid), tag: clan.tag };
    });

    const decide = (steamId, requestId, accept) => store.tx(() => {
        const r = store.get(`SELECT * FROM requests WHERE id = ?`, requestId);
        if (!r || r.status !== 'pending') throw new HttpError(404, 'unknown_request', 'no pending request with this id');
        const m = membership(steamId);
        if (!m || m.id !== r.clan_id || (m.role !== 'leader' && m.role !== 'officer')) throw new HttpError(403, 'forbidden', 'only the leader or an officer decides');
        const t = now();
        const clan = store.get('SELECT * FROM clans WHERE id = ?', r.clan_id);
        if (accept) {
            if (membership(r.steam_id)) {
                store.run(`UPDATE requests SET status = 'expired', decided_by = ?, decided_at = ? WHERE id = ?`, steamId, t, r.id);
                throw new HttpError(409, 'already_in_clan', 'this player joined another clan meanwhile');
            }
            store.run(`INSERT INTO members (clan_id, steam_id, role, joined_at) VALUES (?, ?, 'member', ?)`, r.clan_id, r.steam_id, t);
            store.history(r.clan_id, 'joined', null, r.steam_id);
            store.event('member.joined', { tag: clan.tag, player: r.steam_id, discordId: discordOf(r.steam_id), by: steamId });
        } else {
            store.event('request.rejected', { tag: clan.tag, player: r.steam_id, by: steamId });
        }
        store.run(`UPDATE requests SET status = ?, decided_by = ?, decided_at = ? WHERE id = ?`, accept ? 'accepted' : 'rejected', steamId, t, r.id);
        return { id: r.id, status: accept ? 'accepted' : 'rejected' };
    });

    const cancelRequest = (steamId, requestId) => store.tx(() => {
        const r = store.get(`SELECT * FROM requests WHERE id = ? AND steam_id = ? AND status = 'pending'`, requestId, steamId);
        if (!r) throw new HttpError(404, 'unknown_request', 'no pending request of yours with this id');
        store.run(`UPDATE requests SET status = 'cancelled', decided_at = ? WHERE id = ?`, now(), r.id);
        return { id: r.id, status: 'cancelled' };
    });

    const closeClan = (clan, by, why) => {
        store.run(`UPDATE clans SET status = 'closed', decided_by = ?, decided_at = ?, reason = ? WHERE id = ?`, by, now(), why, clan.id);
        store.run(`UPDATE requests SET status = 'expired', decided_at = ? WHERE clan_id = ? AND status = 'pending'`, now(), clan.id);
        store.history(clan.id, 'closed', why, by);
        store.event('clan.closed', { tag: clan.tag, by, reason: why });
    };

    const leave = (steamId) => store.tx(() => {
        const m = membership(steamId);
        if (!m) throw new HttpError(409, 'not_in_clan', 'you are not in a clan');
        const clan = store.get('SELECT * FROM clans WHERE id = ?', m.id);
        const count = store.get('SELECT COUNT(*) AS n FROM members WHERE clan_id = ?', m.id).n;
        if (m.role === 'leader' && count > 1) throw new HttpError(409, 'leader_must_hand_over', 'hand the leadership to another member before leaving');
        store.run('DELETE FROM members WHERE steam_id = ?', steamId);
        store.history(m.id, 'left', null, steamId);
        store.event('member.left', { tag: clan.tag, player: steamId, discordId: discordOf(steamId) });
        if (count === 1) closeClan(clan, steamId, 'last member left');
        return { left: clan.tag };
    });

    const setRole = (steamId, targetId, role) => store.tx(() => {
        if (!ROLES.includes(role)) throw new HttpError(400, 'bad_role', 'role: leader, officer or member');
        const m = membership(steamId);
        if (!m || m.role !== 'leader') throw new HttpError(403, 'forbidden', 'only the leader changes roles');
        const target = membership(targetId);
        if (!target || target.id !== m.id || targetId === steamId) throw new HttpError(404, 'unknown_member', 'not a member of your clan');
        const clan = store.get('SELECT * FROM clans WHERE id = ?', m.id);
        if (role === 'leader') {
            store.run(`UPDATE members SET role = 'officer' WHERE steam_id = ?`, steamId);
            store.run(`UPDATE members SET role = 'leader' WHERE steam_id = ?`, targetId);
            store.history(m.id, 'leader', targetId, steamId);
            store.event('role.changed', { tag: clan.tag, player: targetId, discordId: discordOf(targetId), role: 'leader', by: steamId });
            store.event('role.changed', { tag: clan.tag, player: steamId, discordId: discordOf(steamId), role: 'officer', by: steamId });
        } else {
            store.run('UPDATE members SET role = ? WHERE steam_id = ?', role, targetId);
            store.history(m.id, 'role', `${targetId} ${role}`, steamId);
            store.event('role.changed', { tag: clan.tag, player: targetId, discordId: discordOf(targetId), role, by: steamId });
        }
        return { player: targetId, role };
    });

    const kick = (steamId, targetId) => store.tx(() => {
        const m = membership(steamId);
        const target = membership(targetId);
        if (!m || !target || target.id !== m.id || targetId === steamId) throw new HttpError(404, 'unknown_member', 'not a member of your clan');
        const allowed = m.role === 'leader' || (m.role === 'officer' && target.role === 'member');
        if (!allowed) throw new HttpError(403, 'forbidden', 'the leader removes anyone, an officer removes members');
        const clan = store.get('SELECT * FROM clans WHERE id = ?', m.id);
        store.run('DELETE FROM members WHERE steam_id = ?', targetId);
        store.history(m.id, 'kicked', targetId, steamId);
        store.event('member.kicked', { tag: clan.tag, player: targetId, discordId: discordOf(targetId), by: steamId });
        return { kicked: targetId };
    });

    const setFaction = (steamId, faction) => store.tx(() => {
        if (!factionIds.has(faction)) throw new HttpError(400, 'bad_faction', 'unknown faction');
        const m = membership(steamId);
        if (!m || (m.role !== 'leader' && m.role !== 'officer')) throw new HttpError(403, 'forbidden', 'only the leader or an officer changes the faction');
        const clan = store.get('SELECT * FROM clans WHERE id = ?', m.id);
        if (clan.faction === faction) return { faction };
        store.run('UPDATE clans SET faction = ? WHERE id = ?', faction, clan.id);
        store.history(clan.id, 'faction', `${clan.faction} -> ${faction}`, steamId);
        store.event('faction.changed', { tag: clan.tag, from: clan.faction, to: faction, by: steamId });
        return { faction };
    });

    const link = (steamId, code) => store.tx(() => {
        const normal = typeof code === 'string' ? code.trim().toUpperCase().replace(/^LINK-?/, '') : '';
        if (!/^[A-Z0-9]{6}$/.test(normal)) throw new HttpError(400, 'bad_code', 'code looks like LINK-7F3K2Q');
        const row = store.get('SELECT * FROM link_codes WHERE code_hash = ?', sha256(normal));
        if (!row || row.expires_at <= now()) throw new HttpError(404, 'unknown_code', 'unknown or expired code: ask the bot for a new one');
        store.run('DELETE FROM link_codes WHERE code_hash = ?', row.code_hash);
        store.run('UPDATE players SET discord_id = NULL, discord_name = NULL WHERE discord_id = ?', row.discord_id);
        store.run('UPDATE players SET discord_id = ?, discord_name = ? WHERE steam_id = ?', row.discord_id, row.discord_name, steamId);
        const linkedClan = membership(steamId);
        store.event('link.done', { player: steamId, discordId: row.discord_id, tag: linkedClan ? linkedClan.tag : null });
        return { discordLinked: true, discordName: row.discord_name };
    });

    // --- internal ---
    const mintSession = (body) => store.tx(() => {
        if (typeof body.steamId !== 'string' || !STEAM_ID.test(body.steamId)) throw new HttpError(400, 'bad_steam_id', 'steamId: Steam ID 64');
        const name = cleanText(body.name, 64) || `Player ${body.steamId.slice(-4)}`;
        ensurePlayer(body.steamId, name);
        const token = crypto.randomBytes(32).toString('base64url');
        const t = now();
        store.run('INSERT INTO sessions (token_hash, steam_id, server, created_at, expires_at) VALUES (?, ?, ?, ?, ?)',
            sha256(token), body.steamId, cleanText(body.server, 64), t, t + tokenTtlMs);
        // keep the 5 newest sessions of the player (several PCs, reinstalls)
        store.run(`DELETE FROM sessions WHERE steam_id = ? AND token_hash NOT IN
                   (SELECT token_hash FROM sessions WHERE steam_id = ? ORDER BY created_at DESC LIMIT 5)`, body.steamId, body.steamId);
        return { token, expiresAt: t + tokenTtlMs };
    });

    const linkCode = (body) => store.tx(() => {
        if (typeof body.discordId !== 'string' || !/^\d{15,22}$/.test(body.discordId)) throw new HttpError(400, 'bad_discord_id', 'discordId: snowflake');
        store.run('DELETE FROM link_codes WHERE discord_id = ? OR expires_at <= ?', body.discordId, now());
        const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
        let code = '';
        for (const b of crypto.randomBytes(6)) code += alphabet[b % alphabet.length];
        const expiresAt = now() + hubConfig.linkCodeTtlMinutes * 60000;
        store.run('INSERT INTO link_codes (code_hash, discord_id, discord_name, expires_at) VALUES (?, ?, ?, ?)',
            sha256(code), body.discordId, cleanText(body.discordName, 64), expiresAt);
        return { code: `LINK-${code}`, expiresAt };
    });

    const decideClan = (tag, approve, body) => store.tx(() => {
        const clan = clanByTag(tag, ['pending']);
        if (!clan) throw new HttpError(404, 'unknown_clan', 'no pending clan with this tag');
        const by = cleanText(body.by, 64) || 'admin';
        const reason = cleanText(body.reason, 200);
        const t = now();
        if (approve) {
            store.run(`UPDATE clans SET status = 'active', decided_by = ?, decided_at = ? WHERE id = ?`, by, t, clan.id);
            store.history(clan.id, 'approved', null, by);
            store.event('clan.approved', { tag: clan.tag, name: clan.name, faction: clan.faction, by });
        } else {
            store.run(`UPDATE clans SET status = 'rejected', decided_by = ?, decided_at = ?, reason = ? WHERE id = ?`, by, t, reason, clan.id);
            store.run('DELETE FROM members WHERE clan_id = ?', clan.id);
            store.history(clan.id, 'rejected', reason, by);
            store.event('clan.rejected', { tag: clan.tag, by, reason });
        }
        return { tag: clan.tag, status: approve ? 'active' : 'rejected' };
    });

    const importClan = (body) => store.tx(() => {
        const tag = typeof body.tag === 'string' ? body.tag.trim() : '';
        if (!TAG.test(tag)) throw new HttpError(400, 'bad_tag', 'tag: 2 to 6 letters or digits');
        const name = cleanText(body.name, 32) || tag;
        if (!factionIds.has(body.faction)) throw new HttpError(400, 'bad_faction', 'unknown faction');
        const leader = typeof body.leaderDiscordId === 'string' ? store.get('SELECT steam_id FROM players WHERE discord_id = ?', body.leaderDiscordId) : null;
        if (!leader) throw new HttpError(404, 'leader_not_linked', 'the leader has not linked his Discord account in the hub yet (/hub link)');
        if (membership(leader.steam_id)) throw new HttpError(409, 'already_in_clan', 'the leader is already in a hub clan');
        if (store.get(`SELECT id FROM clans WHERE tag_key = ? AND status IN ('pending','active')`, tag.toUpperCase())) throw new HttpError(409, 'tag_taken', 'this tag is already used in the hub');
        const by = cleanText(body.by, 64) || 'admin';
        const t = now();
        const result = store.run(
            `INSERT INTO clans (tag, tag_key, name, faction, status, created_by, created_at, decided_by, decided_at) VALUES (?, ?, ?, ?, 'active', ?, ?, ?, ?)`,
            tag, tag.toUpperCase(), name, body.faction, leader.steam_id, t, by, t);
        const clanId = Number(result.lastInsertRowid);
        store.run(`INSERT INTO members (clan_id, steam_id, role, joined_at) VALUES (?, ?, 'leader', ?)`, clanId, leader.steam_id, t);
        store.run(`UPDATE requests SET status = 'cancelled', decided_at = ? WHERE steam_id = ? AND status = 'pending'`, t, leader.steam_id);
        store.history(clanId, 'imported', `from Discord by ${by}`, leader.steam_id);
        store.event('clan.approved', { tag, name, faction: body.faction, by, imported: true });
        return { tag, status: 'active' };
    });

    const forgetPlayer = (body) => store.tx(() => {
        let steamId = typeof body.steamId === 'string' && STEAM_ID.test(body.steamId) ? body.steamId : null;
        if (!steamId && typeof body.discordId === 'string') steamId = (store.get('SELECT steam_id FROM players WHERE discord_id = ?', body.discordId) || {}).steam_id || null;
        if (!steamId || !store.get('SELECT steam_id FROM players WHERE steam_id = ?', steamId)) throw new HttpError(404, 'unknown_player', 'no hub player with this Steam or Discord id');
        const by = cleanText(body.by, 64) || 'admin';
        const t = now();
        const m = membership(steamId);
        if (m) {
            const clan = store.get('SELECT * FROM clans WHERE id = ?', m.id);
            store.run('DELETE FROM members WHERE steam_id = ?', steamId);
            store.event('member.left', { tag: clan.tag, player: steamId, discordId: discordOf(steamId) });
            if (m.role === 'leader') {
                const heir = store.get(`SELECT steam_id FROM members WHERE clan_id = ? ORDER BY CASE role WHEN 'officer' THEN 0 ELSE 1 END, joined_at LIMIT 1`, m.id);
                if (heir) {
                    store.run(`UPDATE members SET role = 'leader' WHERE steam_id = ?`, heir.steam_id);
                    store.history(m.id, 'leader', heir.steam_id, 'erasure');
                    store.event('role.changed', { tag: clan.tag, player: heir.steam_id, discordId: discordOf(heir.steam_id), role: 'leader', by: 'erasure' });
                } else if (clan.status === 'active' || clan.status === 'pending') {
                    closeClan(clan, 'erasure', 'last member erased');
                }
            }
        }
        store.run('DELETE FROM sessions WHERE steam_id = ?', steamId);
        store.run('DELETE FROM requests WHERE steam_id = ?', steamId);
        store.run('DELETE FROM lineups WHERE steam_id = ?', steamId);
        // histories keep the facts (a member joined, left) without the person
        store.run(`UPDATE clan_history SET by_player = 'erased' WHERE by_player = ?`, steamId);
        store.run(`UPDATE clan_history SET detail = REPLACE(detail, ?, 'erased') WHERE detail LIKE ?`, steamId, `%${steamId}%`);
        store.run(`UPDATE clans SET created_by = 'erased' WHERE created_by = ?`, steamId);
        store.run(`UPDATE requests SET decided_by = 'erased' WHERE decided_by = ?`, steamId);
        store.run('DELETE FROM players WHERE steam_id = ?', steamId);
        store.event('player.erased', { by, at: t });
        log.info('hub.player.erased', { by });
        return { erased: true };
    });

    // --- matches (phase 3) ---
    const clanById = (id) => store.get('SELECT * FROM clans WHERE id = ?', id);

    const matchView = (row, withIds) => {
        const a = clanById(row.clan_a);
        const b = clanById(row.clan_b);
        const lineup = (clanId) => store.all(
            `SELECT p.steam_id, p.name, p.discord_id FROM lineups l JOIN players p ON p.steam_id = l.steam_id WHERE l.match_id = ? AND l.clan_id = ? ORDER BY l.joined_at`, row.id, clanId)
            .map((r) => (withIds ? { id: r.steam_id, name: r.name, discordId: r.discord_id } : { name: r.name }));
        const tagOf = (id) => (id === row.clan_a ? a.tag : id === row.clan_b ? b.tag : null);
        return {
            id: row.id,
            status: row.status,
            startsAt: row.starts_at,
            mode: row.mode,
            size: row.size,
            note: row.note,
            a: { tag: a.tag, name: a.name, faction: factionOf(a.faction), lineup: lineup(a.id) },
            b: { tag: b.tag, name: b.name, faction: factionOf(b.faction), lineup: lineup(b.id) },
            waitingFor: tagOf(row.waiting_for),
            result: row.status === 'reported' || row.status === 'played' || row.status === 'disputed'
                ? { winner: row.draw ? 'draw' : tagOf(row.winner), score: row.score, reportedBy: tagOf(row.reported_by) } : null,
        };
    };

    const matchForStaff = (steamId, id) => {
        const row = store.get('SELECT * FROM matches WHERE id = ?', id);
        if (!row) throw new HttpError(404, 'unknown_match', 'no match with this id');
        const m = membership(steamId);
        if (!m || (m.id !== row.clan_a && m.id !== row.clan_b)) throw new HttpError(403, 'forbidden', 'your clan is not in this match');
        return { row, m, staff: m.role === 'leader' || m.role === 'officer' };
    };

    const parseStart = (at) => {
        const t = typeof at === 'number' ? at : Date.parse(at);
        if (!Number.isFinite(t)) throw new HttpError(400, 'bad_date', 'at: a date and time (ISO 8601 or Unix ms)');
        if (t < now() + 15 * 60000) throw new HttpError(400, 'bad_date', 'the match must start at least 15 minutes from now');
        if (t > now() + 120 * 86400000) throw new HttpError(400, 'bad_date', 'the match must start within 120 days');
        return t;
    };

    const matchEvent = (type, row, extra = {}) => {
        const a = clanById(row.clan_a);
        const b = clanById(row.clan_b);
        store.event(type, { match: row.id, a: a.tag, b: b.tag, startsAt: row.starts_at, mode: row.mode, size: row.size, ...extra });
    };

    const challenge = (steamId, body) => store.tx(() => {
        const m = membership(steamId);
        if (!m || (m.role !== 'leader' && m.role !== 'officer')) throw new HttpError(403, 'forbidden', 'only a leader or an officer challenges another clan');
        if (m.status !== 'active') throw new HttpError(409, 'clan_not_active', 'your clan is not approved yet');
        const opponent = clanByTag(body.opponent);
        if (!opponent) throw new HttpError(404, 'unknown_clan', 'no active clan with this tag');
        if (opponent.id === m.id) throw new HttpError(400, 'same_clan', 'a clan cannot challenge itself');
        if (!MATCH_MODES.includes(body.mode)) throw new HttpError(400, 'bad_mode', `mode: ${MATCH_MODES.join(', ')}`);
        const size = Number(body.size);
        if (!Number.isInteger(size) || size < 1 || size > 100) throw new HttpError(400, 'bad_size', 'size: players per side, 1 to 100');
        const at = parseStart(body.at);
        const open = store.get(`SELECT COUNT(*) AS n FROM matches WHERE (clan_a = ? OR clan_b = ?) AND status IN ('proposed','accepted')`, m.id, m.id).n;
        if (open >= hubConfig.maxOpenMatchesPerClan) throw new HttpError(409, 'too_many_matches', 'your clan already has too many open matches');
        const t = now();
        const result = store.run(
            `INSERT INTO matches (clan_a, clan_b, waiting_for, status, starts_at, mode, size, note, created_by, created_at, updated_at)
             VALUES (?, ?, ?, 'proposed', ?, ?, ?, ?, ?, ?, ?)`, m.id, opponent.id, opponent.id, at, body.mode, size, cleanText(body.note, 200), steamId, t, t);
        const row = store.get('SELECT * FROM matches WHERE id = ?', Number(result.lastInsertRowid));
        matchEvent('match.proposed', row);
        return matchView(row, false);
    });

    const respond = (steamId, id, action, body) => store.tx(() => {
        const { row, m, staff } = matchForStaff(steamId, id);
        if (!staff) throw new HttpError(403, 'forbidden', 'only a leader or an officer answers');
        if (row.status !== 'proposed' || row.waiting_for !== m.id) throw new HttpError(409, 'not_waiting_for_you', 'this proposal is not waiting for your clan');
        const t = now();
        if (action === 'accept') {
            store.run(`UPDATE matches SET status = 'accepted', waiting_for = NULL, updated_at = ? WHERE id = ?`, t, row.id);
        } else if (action === 'decline') {
            store.run(`UPDATE matches SET status = 'declined', waiting_for = NULL, updated_at = ? WHERE id = ?`, t, row.id);
        } else {
            const at = parseStart(body.at);
            const other = m.id === row.clan_a ? row.clan_b : row.clan_a;
            store.run(`UPDATE matches SET starts_at = ?, waiting_for = ?, updated_at = ? WHERE id = ?`, at, other, t, row.id);
        }
        const updated = store.get('SELECT * FROM matches WHERE id = ?', row.id);
        matchEvent(action === 'accept' ? 'match.accepted' : action === 'decline' ? 'match.declined' : 'match.countered', updated, { by: clanById(m.id).tag });
        return matchView(updated, false);
    });

    const cancelMatch = (steamId, id) => store.tx(() => {
        const { row, m, staff } = matchForStaff(steamId, id);
        if (!staff) throw new HttpError(403, 'forbidden', 'only a leader or an officer cancels');
        if (!['proposed', 'accepted'].includes(row.status)) throw new HttpError(409, 'not_open', 'this match can no longer be cancelled');
        if (row.status === 'accepted' && row.starts_at <= now()) throw new HttpError(409, 'started', 'the match has started: report the result instead');
        store.run(`UPDATE matches SET status = 'cancelled', waiting_for = NULL, updated_at = ? WHERE id = ?`, now(), row.id);
        const updated = store.get('SELECT * FROM matches WHERE id = ?', row.id);
        matchEvent('match.cancelled', updated, { by: clanById(m.id).tag });
        return matchView(updated, false);
    });

    const signup = (steamId, id) => store.tx(() => {
        const { row, m } = matchForStaff(steamId, id);
        if (!['proposed', 'accepted'].includes(row.status) || row.starts_at <= now()) throw new HttpError(409, 'not_open', 'sign-ups are closed for this match');
        const mine = store.get('SELECT 1 AS x FROM lineups WHERE match_id = ? AND steam_id = ?', row.id, steamId);
        if (mine) {
            store.run('DELETE FROM lineups WHERE match_id = ? AND steam_id = ?', row.id, steamId);
            return { signedUp: false };
        }
        const count = store.get('SELECT COUNT(*) AS n FROM lineups WHERE match_id = ? AND clan_id = ?', row.id, m.id).n;
        if (count >= row.size) throw new HttpError(409, 'lineup_full', 'your clan\'s lineup is full');
        store.run('INSERT INTO lineups (match_id, clan_id, steam_id, joined_at) VALUES (?, ?, ?, ?)', row.id, m.id, steamId, now());
        return { signedUp: true };
    });

    const resultArgs = (row, body) => {
        if (body.winner === 'draw') return { winner: null, draw: 1 };
        const a = clanById(row.clan_a);
        const b = clanById(row.clan_b);
        const w = typeof body.winner === 'string' ? body.winner.toUpperCase() : '';
        if (w === a.tag_key) return { winner: a.id, draw: 0 };
        if (w === b.tag_key) return { winner: b.id, draw: 0 };
        throw new HttpError(400, 'bad_winner', `winner: ${a.tag}, ${b.tag} or draw`);
    };

    const report = (steamId, id, body) => store.tx(() => {
        const { row, m, staff } = matchForStaff(steamId, id);
        if (!staff) throw new HttpError(403, 'forbidden', 'only a leader or an officer reports the result');
        if (row.status !== 'accepted' && row.status !== 'reported') throw new HttpError(409, 'not_reportable', 'only an accepted match gets a result');
        if (row.starts_at > now()) throw new HttpError(409, 'not_started', 'the match has not started yet');
        const r = resultArgs(row, body);
        store.run(`UPDATE matches SET status = 'reported', winner = ?, draw = ?, score = ?, reported_by = ?, updated_at = ? WHERE id = ?`,
            r.winner, r.draw, cleanText(body.score, 40), m.id, now(), row.id);
        const updated = store.get('SELECT * FROM matches WHERE id = ?', row.id);
        matchEvent('match.reported', updated, { by: clanById(m.id).tag });
        return matchView(updated, false);
    });

    const confirmOrDispute = (steamId, id, confirm) => store.tx(() => {
        const { row, m, staff } = matchForStaff(steamId, id);
        if (!staff) throw new HttpError(403, 'forbidden', 'only a leader or an officer answers');
        if (row.status !== 'reported' || row.reported_by === m.id) throw new HttpError(409, 'not_yours', 'the other clan confirms or disputes a reported result');
        store.run(`UPDATE matches SET status = ?, updated_at = ? WHERE id = ?`, confirm ? 'played' : 'disputed', now(), row.id);
        const updated = store.get('SELECT * FROM matches WHERE id = ?', row.id);
        matchEvent(confirm ? 'match.played' : 'match.disputed', updated, { by: clanById(m.id).tag, ...matchView(updated, false).result });
        return matchView(updated, false);
    });

    const settle = (id, body) => store.tx(() => {
        const row = store.get('SELECT * FROM matches WHERE id = ?', id);
        if (!row || !['reported', 'disputed', 'accepted'].includes(row.status)) throw new HttpError(404, 'unknown_match', 'no match waiting for a result with this id');
        const r = resultArgs(row, body);
        store.run(`UPDATE matches SET status = 'played', winner = ?, draw = ?, score = ?, updated_at = ? WHERE id = ?`, r.winner, r.draw, cleanText(body.score, 40), now(), row.id);
        const updated = store.get('SELECT * FROM matches WHERE id = ?', row.id);
        matchEvent('match.played', updated, { by: cleanText(body.by, 64) || 'admin', settled: true, ...matchView(updated, false).result });
        return matchView(updated, false);
    });

    const listMatches = (withIds) => {
        const since = now() - 14 * 86400000;
        return store.all(`SELECT * FROM matches WHERE status NOT IN ('declined','cancelled') AND (starts_at >= ? OR status IN ('reported','disputed')) ORDER BY starts_at LIMIT 100`, since)
            .map((row) => matchView(row, withIds));
    };

    const allClansInternal = () => store.all(`SELECT * FROM clans WHERE status IN ('pending','active') ORDER BY tag_key`).map((c) => ({
        ...clanSummary(c),
        roster: store.all(
            `SELECT p.steam_id, p.name, p.discord_id, m.role FROM members m JOIN players p ON p.steam_id = m.steam_id WHERE m.clan_id = ?`, c.id)
            .map((r) => ({ id: r.steam_id, name: r.name, discordId: r.discord_id, role: r.role })),
    }));

    // --- routing ---
    async function handle(req, res, route, url) {
        const p = url.pathname;
        if (!p.startsWith('/api/hub/') && !p.startsWith('/internal/hub/')) return false;

        if (route === 'GET /api/hub/factions') return sendJson(res, 200, { factions }), true;
        if (route === 'GET /api/hub/clans') {
            const clans = store.all(`SELECT * FROM clans WHERE status = 'active' ORDER BY tag_key`).map(clanSummary);
            return sendJson(res, 200, { clans }), true;
        }
        let m = /^\/api\/hub\/clans\/([^/]+)$/.exec(p);
        if (req.method === 'GET' && m) {
            const clan = clanByTag(decodeURIComponent(m[1]));
            if (!clan) throw new HttpError(404, 'unknown_clan', 'no active clan with this tag');
            return sendJson(res, 200, { clan: { ...clanSummary(clan), roster: members(clan.id, false) } }), true;
        }
        if (route === 'GET /api/hub/me') return sendJson(res, 200, me(authPlayer(req))), true;
        if (route === 'GET /api/hub/matches') return sendJson(res, 200, { matches: listMatches(false) }), true;

        if (req.method === 'POST' && p.startsWith('/api/hub/')) {
            const steamId = authPlayer(req);
            limitWrite(steamId);
            // actions without fields (join, accept, leave...) may come without a body
            const body = req.headers['content-type'] || Number(req.headers['content-length']) > 0 ? await readJson(req, 4096) : {};
            if (p === '/api/hub/clans') return sendJson(res, 201, createClan(steamId, body)), true;
            if ((m = /^\/api\/hub\/clans\/([^/]+)\/join$/.exec(p))) return sendJson(res, 201, requestJoin(steamId, decodeURIComponent(m[1]))), true;
            if ((m = /^\/api\/hub\/requests\/(\d+)\/(accept|reject|cancel)$/.exec(p))) {
                const id = Number(m[1]);
                if (m[2] === 'cancel') return sendJson(res, 200, cancelRequest(steamId, id)), true;
                return sendJson(res, 200, decide(steamId, id, m[2] === 'accept')), true;
            }
            if (p === '/api/hub/clan/leave') return sendJson(res, 200, leave(steamId)), true;
            if ((m = /^\/api\/hub\/clan\/members\/(\d{17})\/role$/.exec(p))) return sendJson(res, 200, setRole(steamId, m[1], body.role)), true;
            if ((m = /^\/api\/hub\/clan\/members\/(\d{17})\/kick$/.exec(p))) return sendJson(res, 200, kick(steamId, m[1])), true;
            if (p === '/api/hub/clan/faction') return sendJson(res, 200, setFaction(steamId, body.faction)), true;
            if (p === '/api/hub/link') return sendJson(res, 200, link(steamId, body.code)), true;
            if (p === '/api/hub/matches') return sendJson(res, 201, challenge(steamId, body)), true;
            if ((m = /^\/api\/hub\/matches\/(\d+)\/(accept|decline|counter|cancel|signup|report|confirm|dispute)$/.exec(p))) {
                const id = Number(m[1]);
                switch (m[2]) {
                    case 'accept': case 'decline': case 'counter': return sendJson(res, 200, respond(steamId, id, m[2], body)), true;
                    case 'cancel': return sendJson(res, 200, cancelMatch(steamId, id)), true;
                    case 'signup': return sendJson(res, 200, signup(steamId, id)), true;
                    case 'report': return sendJson(res, 200, report(steamId, id, body)), true;
                    default: return sendJson(res, 200, confirmOrDispute(steamId, id, m[2] === 'confirm')), true;
                }
            }
            throw new HttpError(404, 'not_found');
        }

        if (p.startsWith('/internal/hub/')) {
            requireInternal(req);
            if (route === 'POST /internal/hub/session') return sendJson(res, 200, mintSession(await readJson(req, 4096))), true;
            if (route === 'POST /internal/hub/link-code') return sendJson(res, 200, linkCode(await readJson(req, 4096))), true;
            if (route === 'GET /internal/hub/events') {
                const after = Math.max(0, Number(url.searchParams.get('after')) || 0);
                const limit = Math.min(500, Math.max(1, Number(url.searchParams.get('limit')) || 200));
                const rows = store.all('SELECT * FROM events WHERE id > ? ORDER BY id LIMIT ?', after, limit)
                    .map((e) => ({ id: e.id, type: e.type, at: e.created_at, ...JSON.parse(e.payload) }));
                return sendJson(res, 200, { events: rows, last: rows.length ? rows[rows.length - 1].id : after }), true;
            }
            if (route === 'GET /internal/hub/clans') return sendJson(res, 200, { clans: allClansInternal() }), true;
            if (route === 'GET /internal/hub/matches') return sendJson(res, 200, { matches: listMatches(true) }), true;
            if (req.method === 'POST' && (m = /^\/internal\/hub\/matches\/(\d+)\/settle$/.exec(p))) return sendJson(res, 200, settle(Number(m[1]), await readJson(req, 4096))), true;
            if (route === 'POST /internal/hub/players/forget') return sendJson(res, 200, forgetPlayer(await readJson(req, 4096))), true;
            if (route === 'POST /internal/hub/clans/import') return sendJson(res, 201, importClan(await readJson(req, 4096))), true;
            if (req.method === 'POST' && (m = /^\/internal\/hub\/clans\/([^/]+)\/(approve|reject)$/.exec(p))) {
                return sendJson(res, 200, decideClan(decodeURIComponent(m[1]), m[2] === 'approve', await readJson(req, 4096))), true;
            }
            if (req.method === 'GET' && (m = /^\/internal\/hub\/players\/by-discord\/(\d{15,22})$/.exec(p))) {
                const player = store.get('SELECT steam_id FROM players WHERE discord_id = ?', m[1]);
                if (!player) throw new HttpError(404, 'unknown_player', 'no player linked to this Discord account');
                return sendJson(res, 200, me(player.steam_id)), true;
            }
            throw new HttpError(404, 'not_found');
        }
        throw new HttpError(404, 'not_found');
    }

    function sweep() {
        const t = now();
        store.run('DELETE FROM sessions WHERE expires_at <= ?', t);
        store.run('DELETE FROM link_codes WHERE expires_at <= ?', t);
        // join requests left unanswered expire after the configured days
        store.run(`UPDATE requests SET status = 'expired', decided_at = ? WHERE status = 'pending' AND created_at <= ?`, t, t - hubConfig.requestTtlDays * 86400000);
        writes.prune();
    }

    return { handle, sweep, store, close: () => store.close(), secret: hubSecret };
}

module.exports = { createHub, HubStore, cleanText, DEFAULT_FACTIONS, SECRET_HEADER, DEV_HEADER };
