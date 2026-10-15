'use strict';
// Configuration: built-in defaults, overridden by a JSON file (path from --config <file>, the
// LOTR_SERVICES_CONFIG environment variable, or config.json next to package.json), then by a few
// environment variables. Every path ends up absolute. See config.example.json and README.md.
const fs = require('node:fs');
const path = require('node:path');

const DEFAULTS = Object.freeze({
    host: '127.0.0.1',
    port: 3010,
    // Deploy root (C:\www\lotrmp on the server). data\ and logs\ live under it unless set explicitly.
    root: null,
    dataDir: null,
    logDir: null,
    // Never listen on anything but loopback unless this is true (tests and local experiments only).
    allowNonLoopback: false,
    // Client IP: the right-most X-Forwarded-For entry, trusted only when the TCP peer is loopback (Caddy).
    trustForwardedFor: true,
    log: {
        level: 'info',
        maxFileBytes: 10 * 1024 * 1024,
        keepDays: 14,
        console: true,
    },
    queue: {
        kinds: ['core'],
        maxConcurrent: 40,
        // A slot dies when no heartbeat (or object download through forward_auth) refreshes it for this long.
        slotTtlSec: 180,
        // A waiting client keeps its place while it polls at least this often.
        waitTtlSec: 90,
        retryAfterMinSec: 5,
        retryAfterMaxSec: 60,
        // Distinct install ids one IP may keep in the waiting line (a family behind one router: a few).
        maxWaitingPerIp: 6,
        // forward_auth on core objects: "advisory" answers 200 and only counts, "enforce" answers 403 without a valid slot.
        // REVIEW-AFTER(queue-enforcement): CONTRACTS section 4 keeps enforcement OPEN.
        enforcement: 'advisory',
        stateFile: null,
        // Published releases (manifests tell which objects belong to role "core" modules). Default <root>\files\releases.
        releasesDir: null,
        persistDebounceMs: 1000,
    },
    support: {
        maxBodyBytes: 2 * 1024 * 1024,
        expiryHours: 72,
        purgeIntervalSec: 600,
        perIpPerHour: 3,
        perInstallPerHour: 3,
        codeLength: 4,
        dir: null,
        // Shared secret of GET /internal/support/<code> (header X-LotR-Secret). Read from this file; created with a
        // random value at first start when missing. The environment variable LOTR_SUPPORT_SECRET wins over it.
        secretFile: null,
    },
});

function deepMerge(base, extra) {
    const out = Array.isArray(base) ? [...base] : { ...base };
    for (const [key, value] of Object.entries(extra || {})) {
        if (key.startsWith('_')) continue; // "_comment" keys of the example file
        if (value && typeof value === 'object' && !Array.isArray(value) && base[key] && typeof base[key] === 'object' && !Array.isArray(base[key])) {
            out[key] = deepMerge(base[key], value);
        } else {
            out[key] = value;
        }
    }
    return out;
}

function configPathFromArgs(argv) {
    const i = argv.indexOf('--config');
    if (i >= 0 && argv[i + 1]) return argv[i + 1];
    return null;
}

function loadConfig({ argv = process.argv.slice(2), env = process.env, overrides = null, baseDir = path.join(__dirname, '..') } = {}) {
    let file = configPathFromArgs(argv) || env.LOTR_SERVICES_CONFIG || null;
    if (!file && fs.existsSync(path.join(baseDir, 'config.json'))) file = path.join(baseDir, 'config.json');
    let fromFile = {};
    if (file) {
        const text = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
        try {
            fromFile = JSON.parse(text);
        } catch (error) {
            throw new Error(`Config file ${file} is not valid JSON: ${error.message}`);
        }
    }
    let config = deepMerge(DEFAULTS, fromFile);
    if (env.LOTR_SERVICES_ROOT) config.root = env.LOTR_SERVICES_ROOT;
    if (env.LOTR_SERVICES_PORT) config.port = Number(env.LOTR_SERVICES_PORT);
    if (overrides) config = deepMerge(config, overrides);
    return finalize(config, file, baseDir);
}

function finalize(config, file, baseDir) {
    const root = config.root ? path.resolve(config.root) : path.resolve(baseDir, 'local-root');
    const dataDir = path.resolve(config.dataDir || path.join(root, 'data'));
    const logDir = path.resolve(config.logDir || path.join(root, 'logs', 'lotr-services'));
    const result = {
        ...config,
        configFile: file,
        root,
        dataDir,
        logDir,
        queue: {
            ...config.queue,
            stateFile: path.resolve(config.queue.stateFile || path.join(dataDir, 'lotr-services', 'queue-state.json')),
            releasesDir: path.resolve(config.queue.releasesDir || path.join(root, 'files', 'releases')),
        },
        support: {
            ...config.support,
            dir: path.resolve(config.support.dir || path.join(dataDir, 'support')),
            secretFile: path.resolve(config.support.secretFile || path.join(dataDir, 'secrets', 'support-service.secret')),
        },
    };
    validate(result);
    return result;
}

function validate(c) {
    const problems = [];
    const loopback = ['127.0.0.1', '::1', 'localhost'];
    if (!loopback.includes(c.host) && !c.allowNonLoopback) problems.push(`host ${c.host} is not loopback (set allowNonLoopback only for tests)`);
    if (!Number.isInteger(c.port) || c.port < 0 || c.port > 65535) problems.push('port must be an integer 0-65535');
    const q = c.queue;
    if (!Number.isInteger(q.maxConcurrent) || q.maxConcurrent < 1) problems.push('queue.maxConcurrent must be >= 1');
    if (!(q.slotTtlSec > 0) || !(q.waitTtlSec > 0)) problems.push('queue.slotTtlSec and queue.waitTtlSec must be > 0');
    if (!['advisory', 'enforce'].includes(q.enforcement)) problems.push('queue.enforcement must be "advisory" or "enforce"');
    if (!Array.isArray(q.kinds) || q.kinds.length === 0) problems.push('queue.kinds must list at least one kind');
    const s = c.support;
    if (!Number.isInteger(s.codeLength) || s.codeLength < 4 || s.codeLength > 6) problems.push('support.codeLength must be 4 to 6');
    if (!(s.maxBodyBytes > 0) || s.maxBodyBytes > 16 * 1024 * 1024) problems.push('support.maxBodyBytes must be 1 byte to 16 MB');
    if (!(s.expiryHours > 0)) problems.push('support.expiryHours must be > 0');
    if (problems.length) throw new Error(`Invalid configuration: ${problems.join('; ')}`);
}

module.exports = { loadConfig, DEFAULTS, deepMerge };
