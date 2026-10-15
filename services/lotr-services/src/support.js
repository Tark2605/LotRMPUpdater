'use strict';
// Support logs (CONTRACTS section 4): the updater posts its log, gets a one-time code LOTR-XXXX valid 72 h,
// the player pastes the code in a Discord ticket, the bot fetches the log once (then the code is burnt).
//
// Storage: <dataDir>\support\<CODE>.json while valid; after the fetch it becomes <CODE>.burnt (no log inside,
// only the dates) until the original expiry, so a code is never handed out twice within its lifetime and a
// second fetch answers "already used" instead of "unknown". The purge job deletes both kinds once expired.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

// No 0/O, 1/I/L: readable over voice chat and in any font.
const CODE_ALPHABET = '23456789ABCDEFGHJKMNPQRSTUVWXYZ';
const CODE_PATTERN = /^LOTR-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4,6}$/;
const INSTALL_ID_PATTERN = /^[A-Za-z0-9_-]{8,128}$/;
const APP_VERSION_PATTERN = /^[0-9A-Za-z][0-9A-Za-z.+_-]{0,31}$/;

function randomCode(length) {
    let s = '';
    for (let i = 0; i < length; i++) s += CODE_ALPHABET[crypto.randomInt(CODE_ALPHABET.length)];
    return `LOTR-${s}`;
}

// Accepts "lotr-7f3k", " LOTR-7F3K ", "7F3K"; returns the canonical code or null.
function normaliseCode(input) {
    if (typeof input !== 'string') return null;
    let s = input.trim().toUpperCase().replace(/\s+/g, '');
    if (!s.startsWith('LOTR-')) s = s.startsWith('LOTR') ? `LOTR-${s.slice(4)}` : `LOTR-${s}`;
    return CODE_PATTERN.test(s) ? s : null;
}

// --- user name masking -------------------------------------------------------------------------------------
// The updater already masks the Windows user name; this is the server-side second pass. It replaces the
// segment after Users\ (or Documents and Settings\, /home/) in any path spelling: C:\Users\x\, C:/Users/x/,
// JSON-escaped C:\\Users\\x\\, \\?\C:\Users\x, /c/Users/x (MSYS), Z:\home\x\ and /home/x/ (Wine/Proton).
// A name followed by a separator may contain spaces ("Jean Dupont"); at the end of a path it stops at
// whitespace or a quote. Shared folders (Public, Default, All Users) and placeholders are left alone.
// REVIEW-AFTER(mask-placeholder): CONTRACTS does not name the updater's placeholder; <user>, %USERNAME%, ***, {user}
// and $USER are treated as already masked, and this pass writes <user>.
const MASK = '<user>';
const KEEP = new Set(['public', 'default', 'default user', 'all users', 'defaultapppool']);
const SEP = '(?:\\\\\\\\|\\\\|/)';
const USER_PATH = new RegExp(
    `((?:[A-Za-z]:|/[A-Za-z]|^|(?<=[\\s"'(=\\[,;:]))${SEP}?(?:users|documents and settings|home)${SEP})` +
    `(?:([^\\\\/:*?"<>|\\r\\n\\t]+?)(?=${SEP})|([^\\\\/:*?"<>|\\s'),;\\]]+))`,
    'gi',
);

function isPlaceholder(name) {
    const n = name.trim().toLowerCase();
    if (KEEP.has(n)) return true;
    if (/^%[a-z_]+%$/.test(n)) return true; // %USERNAME%
    if (/^\$\{?[a-z_]+\}?$/.test(n)) return true; // $USER, ${USER}
    if (/^\*+$/.test(n) || /^\{[a-z_]+\}$/.test(n)) return true; // ***, {user}
    return false;
}

function maskUserPaths(text) {
    let count = 0;
    const out = text.replace(USER_PATH, (match, prefix, spaced, plain) => {
        const name = spaced !== undefined ? spaced : plain;
        if (!name || isPlaceholder(name)) return match;
        count++;
        return `${prefix}${MASK}`;
    });
    return { text: out, count };
}

// --- log header summary --------------------------------------------------------------------------------------
// REVIEW-AFTER(updater-log-header): the updater's log header format is not fixed in CONTRACTS yet. Read
// "Key: value" or "key=value" lines among the first 60, and count ERROR/FATAL lines when no error count is given.
const HEADER_KEYS = {
    appversion: 'appVersion', version: 'appVersion', updaterversion: 'appVersion', updater: 'appVersion',
    store: 'store', detectedstore: 'store', gamestore: 'store', platform: 'store',
    errors: 'errors', errorcount: 'errors', error_count: 'errors',
};

function summariseLog(log, appVersion) {
    const lines = log.split(/\r?\n/);
    const summary = { appVersion: appVersion || null, store: null, errors: null, errorsSource: null, lines: lines.length };
    for (const line of lines.slice(0, 60)) {
        const m = line.match(/^\s*(?:#\s*)?([A-Za-z][A-Za-z _-]{1,30}?)\s*[:=]\s*(.{1,80}?)\s*$/);
        if (!m) continue;
        const key = HEADER_KEYS[m[1].toLowerCase().replace(/[\s_-]+/g, '')];
        if (!key) continue;
        if (key === 'errors') {
            const n = Number.parseInt(m[2], 10);
            if (Number.isInteger(n) && n >= 0 && summary.errors === null) { summary.errors = n; summary.errorsSource = 'header'; }
        } else if (key === 'store' && !summary.store) {
            summary.store = m[2].slice(0, 40);
        } else if (key === 'appVersion' && !appVersion) {
            summary.appVersion = m[2].slice(0, 32);
        }
    }
    if (summary.errors === null) {
        summary.errors = lines.filter((l) => /\b(ERROR|FATAL)\b|\[(ERR|ERROR|FTL)\]/.test(l)).length;
        summary.errorsSource = 'counted';
    }
    return summary;
}

// --- validation ----------------------------------------------------------------------------------------------
// Refuses binary content: NUL and C0 control characters other than tab, CR, LF (ESC tolerated for ANSI colours).
function looksBinary(text) {
    return /[\u0000-\u0008\u000B\u000C\u000E-\u001A\u001C-\u001F\u007F]/.test(text) || text.includes('\uFFFD\uFFFD\uFFFD');
}

function validateSubmission(body) {
    const errors = [];
    if (!INSTALL_ID_PATTERN.test(String(body.installId ?? ''))) errors.push('installId: 8-128 characters, letters, digits, - and _');
    if (!APP_VERSION_PATTERN.test(String(body.appVersion ?? ''))) errors.push('appVersion: 1-32 characters, letters, digits, . + - _');
    if (typeof body.log !== 'string' || body.log.trim() === '') errors.push('log: non-empty text required');
    else if (looksBinary(body.log)) errors.push('log: binary content is not accepted, send the text log');
    return errors;
}

// --- rate limiter (sliding window, in memory) ---------------------------------------------------------------
class RateLimiter {
    constructor({ limit, windowMs, now = () => Date.now() }) {
        this.limit = limit;
        this.windowMs = windowMs;
        this.now = now;
        this.hits = new Map();
    }

    recent(key) {
        const t = this.now();
        const list = (this.hits.get(key) || []).filter((x) => x > t - this.windowMs);
        if (list.length) this.hits.set(key, list); else this.hits.delete(key);
        return list;
    }

    // Seconds until the next attempt is allowed, 0 when allowed now.
    retryAfter(key) {
        const list = this.recent(key);
        if (list.length < this.limit) return 0;
        return Math.max(1, Math.ceil((list[0] + this.windowMs - this.now()) / 1000));
    }

    record(key) {
        const list = this.recent(key);
        list.push(this.now());
        this.hits.set(key, list);
    }

    prune() {
        for (const key of [...this.hits.keys()]) this.recent(key);
    }
}

// --- store ---------------------------------------------------------------------------------------------------
class SupportStore {
    constructor({ dir, expiryHours = 72, codeLength = 4 }, { now = () => Date.now(), logger = null } = {}) {
        this.dir = dir;
        this.expiryMs = expiryHours * 3600 * 1000;
        this.codeLength = codeLength;
        this.now = now;
        this.log = logger;
        this.inFlight = new Set();
        fs.mkdirSync(dir, { recursive: true });
    }

    fileOf(code) { return path.join(this.dir, `${code}.json`); }
    burntOf(code) { return path.join(this.dir, `${code}.burnt`); }

    taken(code) {
        return this.inFlight.has(code) || fs.existsSync(this.fileOf(code)) || fs.existsSync(this.burntOf(code));
    }

    // Collision-safe: retries, then grows the code (4 -> 5 -> 6 characters) when the space gets crowded.
    newCode() {
        for (let length = this.codeLength; length <= 6; length++) {
            for (let attempt = 0; attempt < 25; attempt++) {
                const code = randomCode(length);
                if (!this.taken(code)) return code;
            }
        }
        throw new Error('no free support code (store full?)');
    }

    save({ installId, appVersion, log, maskedCount, summary }) {
        const code = this.newCode();
        this.inFlight.add(code);
        try {
            const createdAt = this.now();
            const record = {
                code,
                createdAt: new Date(createdAt).toISOString(),
                expiresAt: new Date(createdAt + this.expiryMs).toISOString(),
                installId,
                appVersion,
                maskedServerSide: maskedCount,
                summary,
                log,
            };
            const file = this.fileOf(code);
            const tmp = `${file}.${process.pid}.tmp`;
            fs.writeFileSync(tmp, JSON.stringify(record), { flag: 'wx' });
            fs.renameSync(tmp, file);
            return record;
        } finally {
            this.inFlight.delete(code);
        }
    }

    // { status: 'ok', record } | { status: 'unknown' } | { status: 'expired' } | { status: 'used' } | { status: 'busy' }
    // A successful read reserves the code; call burn(code) once the response is delivered, or unreserve(code).
    take(code) {
        if (this.inFlight.has(code)) return { status: 'busy' };
        const t = this.now();
        const burnt = this.burntOf(code);
        if (fs.existsSync(burnt)) return { status: 'used' };
        let record;
        try {
            record = JSON.parse(fs.readFileSync(this.fileOf(code), 'utf8'));
        } catch {
            return { status: 'unknown' };
        }
        if (Date.parse(record.expiresAt) <= t) {
            this.remove(code);
            return { status: 'expired' };
        }
        this.inFlight.add(code);
        return { status: 'ok', record };
    }

    unreserve(code) {
        this.inFlight.delete(code);
    }

    burn(code, expiresAt) {
        try {
            fs.writeFileSync(this.burntOf(code), JSON.stringify({ code, burntAt: new Date(this.now()).toISOString(), expiresAt }));
            fs.rmSync(this.fileOf(code), { force: true });
        } finally {
            this.inFlight.delete(code);
        }
    }

    remove(code) {
        fs.rmSync(this.fileOf(code), { force: true });
        fs.rmSync(this.burntOf(code), { force: true });
    }

    // Deletes expired logs and tombstones, and stray temp files. Returns how many files were removed.
    purge() {
        const t = this.now();
        let removed = 0;
        for (const name of fs.readdirSync(this.dir)) {
            const full = path.join(this.dir, name);
            try {
                if (name.endsWith('.tmp')) {
                    if (fs.statSync(full).mtimeMs < t - 3600000) { fs.rmSync(full, { force: true }); removed++; }
                    continue;
                }
                if (!/^LOTR-[A-Z0-9]{4,6}\.(json|burnt)$/.test(name)) continue;
                const rec = JSON.parse(fs.readFileSync(full, 'utf8'));
                if (!(Date.parse(rec.expiresAt) > t)) { fs.rmSync(full, { force: true }); removed++; }
            } catch {
                // unreadable record: drop it once it is older than the expiry period
                try {
                    if (fs.statSync(full).mtimeMs < t - this.expiryMs) { fs.rmSync(full, { force: true }); removed++; }
                } catch { /* gone */ }
            }
        }
        if (removed && this.log) this.log.info('support.purge', { removed });
        return removed;
    }

    count() {
        return fs.readdirSync(this.dir).filter((n) => /^LOTR-.*\.json$/.test(n)).length;
    }
}

module.exports = {
    SupportStore, RateLimiter, maskUserPaths, summariseLog, validateSubmission, normaliseCode, randomCode, looksBinary,
    CODE_ALPHABET, CODE_PATTERN, MASK,
};
