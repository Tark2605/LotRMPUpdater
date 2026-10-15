'use strict';
// Structured logs: one JSON object per line in <logDir>\lotr-services-YYYY-MM-DD.log.
// Rotation: a new file every UTC day, and within a day "-1", "-2"... once a file reaches maxFileBytes.
// Files older than keepDays are deleted at start and at every day change.
// Never log support log contents, full tokens or secrets: callers pass short prefixes only.
const fs = require('node:fs');
const path = require('node:path');

const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 };

function createLogger({ dir, level = 'info', maxFileBytes = 10 * 1024 * 1024, keepDays = 14, console: toConsole = true, now = () => new Date() } = {}) {
    const threshold = LEVELS[level] ?? LEVELS.info;
    let day = null;
    let index = 0;
    let size = 0;
    let file = null;
    if (dir) fs.mkdirSync(dir, { recursive: true });

    function fileFor(d, i) {
        return path.join(dir, `lotr-services-${d}${i ? `-${i}` : ''}.log`);
    }

    function prune() {
        if (!dir) return;
        const limit = now().getTime() - keepDays * 86400000;
        for (const name of fs.readdirSync(dir)) {
            const m = name.match(/^lotr-services-(\d{4}-\d{2}-\d{2})(?:-\d+)?\.log$/);
            if (m && Date.parse(`${m[1]}T23:59:59Z`) < limit) {
                try { fs.unlinkSync(path.join(dir, name)); } catch { /* in use: next time */ }
            }
        }
    }

    function target(lineBytes) {
        const d = now().toISOString().slice(0, 10);
        if (d !== day) {
            day = d;
            index = 0;
            prune();
            while (fs.existsSync(fileFor(day, index)) && fs.statSync(fileFor(day, index)).size >= maxFileBytes) index++;
            file = fileFor(day, index);
            size = fs.existsSync(file) ? fs.statSync(file).size : 0;
        }
        if (size > 0 && size + lineBytes > maxFileBytes) {
            index++;
            file = fileFor(day, index);
            size = fs.existsSync(file) ? fs.statSync(file).size : 0;
        }
        return file;
    }

    function write(lvl, msg, fields) {
        if ((LEVELS[lvl] ?? 0) < threshold) return;
        const entry = { t: now().toISOString(), level: lvl, msg, ...fields };
        let line;
        try { line = JSON.stringify(entry); } catch { line = JSON.stringify({ t: entry.t, level: lvl, msg, note: 'unserialisable fields' }); }
        line += '\n';
        if (dir) {
            try {
                const bytes = Buffer.byteLength(line);
                fs.appendFileSync(target(bytes), line);
                size += bytes;
            } catch { /* disk problem: keep the console output */ }
        }
        if (toConsole) process.stdout.write(line);
    }

    return {
        debug: (msg, fields) => write('debug', msg, fields),
        info: (msg, fields) => write('info', msg, fields),
        warn: (msg, fields) => write('warn', msg, fields),
        error: (msg, fields) => write('error', msg, fields),
    };
}

// Logger that drops everything (tests).
const silentLogger = { debug() {}, info() {}, warn() {}, error() {} };

module.exports = { createLogger, silentLogger };
