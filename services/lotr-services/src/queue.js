'use strict';
// Core download queue (CONTRACTS section 4).
//
// At most maxConcurrent install ids download Core objects at the same time. Everyone else waits in a FIFO line:
// one place per install id (asking again keeps the place and refreshes it), the head of the line gets the next
// free slot, a newcomer never overtakes someone already waiting. A slot lives slotTtlSec after its last sign of
// life (grant, heartbeat, object download seen by forward_auth); a waiting place lives waitTtlSec after its last
// poll. The whole state is a plain object saved to disk by the server (debounced) and reloaded at start, so a
// crash or a restart keeps both the slots and the line.
const crypto = require('node:crypto');

const CLIENT_PATTERN = /^[A-Za-z0-9_-]{8,128}$/;

function newToken() {
    return `q_${crypto.randomBytes(24).toString('base64url')}`;
}

class DownloadQueue {
    constructor(options, { now = () => Date.now(), onChange = () => {}, logger = null } = {}) {
        this.opts = {
            kinds: ['core'], maxConcurrent: 40, slotTtlSec: 180, waitTtlSec: 90,
            retryAfterMinSec: 5, retryAfterMaxSec: 60, maxWaitingPerIp: 6, ...options,
        };
        this.now = now;
        this.onChange = onChange;
        this.log = logger;
        this.kinds = {};
        for (const kind of this.opts.kinds) this.kinds[kind] = { active: new Map(), byClient: new Map(), waiting: [] };
    }

    static validClient(client) {
        return typeof client === 'string' && CLIENT_PATTERN.test(client);
    }

    state(kind) {
        return this.kinds[kind] || null;
    }

    // Drops dead slots and stale waiting places. Returns true when something changed.
    sweep() {
        const t = this.now();
        let changed = false;
        for (const [kind, st] of Object.entries(this.kinds)) {
            for (const [token, slot] of st.active) {
                if (slot.expiresAt <= t) {
                    st.active.delete(token);
                    if (st.byClient.get(slot.client) === token) st.byClient.delete(slot.client);
                    changed = true;
                    if (this.log) this.log.info('queue.slot.expired', { kind, client: slot.client.slice(0, 8), token: token.slice(0, 8) });
                }
            }
            const before = st.waiting.length;
            st.waiting = st.waiting.filter((w) => w.lastSeen + this.opts.waitTtlSec * 1000 > t);
            if (st.waiting.length !== before) changed = true;
        }
        if (changed) this.onChange();
        return changed;
    }

    retryAfterFor(position) {
        const { retryAfterMinSec: min, retryAfterMaxSec: max, maxConcurrent } = this.opts;
        // Roughly: the further back, the slower the polling; a quarter of the slots turning over per step.
        const steps = Math.ceil(position / Math.max(1, Math.floor(maxConcurrent / 4)));
        return Math.min(max, Math.max(min, min * steps));
    }

    // Returns { granted: true, token, expiresAt } or { granted: false, position, retryAfter, reason? }.
    requestSlot({ kind, client, ip = '' }) {
        const st = this.state(kind);
        if (!st) throw Object.assign(new Error(`unknown kind ${kind}`), { code: 'bad_kind' });
        this.sweep();
        const t = this.now();
        const ttl = this.opts.slotTtlSec * 1000;

        // Already downloading: same slot, refreshed (fair: one slot per install id).
        const existing = st.byClient.get(client);
        if (existing && st.active.has(existing)) {
            const slot = st.active.get(existing);
            slot.expiresAt = t + ttl;
            this.onChange();
            return { granted: true, token: existing, expiresAt: slot.expiresAt, reused: true };
        }

        const free = this.opts.maxConcurrent - st.active.size;
        let index = st.waiting.findIndex((w) => w.client === client);
        const ahead = index >= 0 ? index : st.waiting.length;
        if (free > 0 && ahead < free) {
            if (index >= 0) st.waiting.splice(index, 1);
            const token = newToken();
            st.active.set(token, { client, ip, issuedAt: t, expiresAt: t + ttl, lastSeen: t });
            st.byClient.set(client, token);
            this.onChange();
            if (this.log) this.log.info('queue.slot.granted', { kind, client: client.slice(0, 8), token: token.slice(0, 8), active: st.active.size, waiting: st.waiting.length });
            return { granted: true, token, expiresAt: t + ttl };
        }

        if (index < 0) {
            const sameIp = ip ? st.waiting.filter((w) => w.ip === ip).length : 0;
            if (ip && sameIp >= this.opts.maxWaitingPerIp) {
                return { granted: false, position: null, retryAfter: this.opts.retryAfterMaxSec, reason: 'too_many_waiting_from_ip' };
            }
            st.waiting.push({ client, ip, firstSeen: t, lastSeen: t });
            index = st.waiting.length - 1;
        } else {
            st.waiting[index].lastSeen = t;
            if (ip) st.waiting[index].ip = ip;
        }
        this.onChange();
        const position = index + 1;
        return { granted: false, position, retryAfter: this.retryAfterFor(position) };
    }

    findSlot(token) {
        if (typeof token !== 'string') return null;
        for (const [kind, st] of Object.entries(this.kinds)) {
            const slot = st.active.get(token);
            if (slot) return { kind, st, slot };
        }
        return null;
    }

    // Keeps a slot alive. Returns the new expiry, or null when the token is unknown or already expired.
    heartbeat(token) {
        this.sweep();
        const found = this.findSlot(token);
        if (!found) return null;
        const t = this.now();
        found.slot.expiresAt = t + this.opts.slotTtlSec * 1000;
        found.slot.lastSeen = t;
        this.onChange();
        return found.slot.expiresAt;
    }

    release(token) {
        const found = this.findSlot(token);
        if (!found) return false;
        found.st.active.delete(token);
        if (found.st.byClient.get(found.slot.client) === token) found.st.byClient.delete(found.slot.client);
        this.onChange();
        if (this.log) this.log.info('queue.slot.released', { kind: found.kind, client: found.slot.client.slice(0, 8), token: token.slice(0, 8) });
        return true;
    }

    // forward_auth: is this token a live slot? A hit counts as a sign of life (an object download in progress).
    check(token) {
        this.sweep();
        const found = this.findSlot(token);
        if (!found) return false;
        const t = this.now();
        found.slot.lastSeen = t;
        found.slot.expiresAt = t + this.opts.slotTtlSec * 1000;
        this.onChange();
        return true;
    }

    stats() {
        const out = {};
        for (const [kind, st] of Object.entries(this.kinds)) out[kind] = { active: st.active.size, waiting: st.waiting.length, max: this.opts.maxConcurrent };
        return out;
    }

    toJSON() {
        const kinds = {};
        for (const [kind, st] of Object.entries(this.kinds)) {
            kinds[kind] = {
                active: [...st.active].map(([token, s]) => ({ token, ...s })),
                waiting: st.waiting.map((w) => ({ ...w })),
            };
        }
        return { version: 1, savedAt: new Date(this.now()).toISOString(), kinds };
    }

    // Restores a saved state; unknown kinds and malformed entries are ignored, expired ones dropped.
    load(saved) {
        if (!saved || saved.version !== 1 || typeof saved.kinds !== 'object') return 0;
        let restored = 0;
        for (const [kind, data] of Object.entries(saved.kinds)) {
            const st = this.state(kind);
            if (!st || !data) continue;
            for (const s of Array.isArray(data.active) ? data.active : []) {
                if (typeof s.token !== 'string' || !DownloadQueue.validClient(s.client) || !Number.isFinite(s.expiresAt)) continue;
                st.active.set(s.token, { client: s.client, ip: String(s.ip || ''), issuedAt: s.issuedAt, expiresAt: s.expiresAt, lastSeen: s.lastSeen || s.issuedAt });
                st.byClient.set(s.client, s.token);
                restored++;
            }
            for (const w of Array.isArray(data.waiting) ? data.waiting : []) {
                if (!DownloadQueue.validClient(w.client) || !Number.isFinite(w.lastSeen) || st.waiting.some((x) => x.client === w.client)) continue;
                st.waiting.push({ client: w.client, ip: String(w.ip || ''), firstSeen: w.firstSeen || w.lastSeen, lastSeen: w.lastSeen });
                restored++;
            }
        }
        this.sweep();
        return restored;
    }
}

module.exports = { DownloadQueue, CLIENT_PATTERN };
