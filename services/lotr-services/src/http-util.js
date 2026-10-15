'use strict';
// Small HTTP helpers on top of node:http (no framework).
const crypto = require('node:crypto');

const LOOPBACK = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);

function isLoopback(address) {
    return LOOPBACK.has(String(address || ''));
}

// Client IP as seen by Caddy. Caddy (2.5+) replaces an X-Forwarded-For coming from an untrusted client with the
// real peer address, and appends otherwise, so the RIGHT-MOST entry is the address Caddy saw. The header is
// believed only when the TCP peer is loopback (Caddy on the same machine); anything else is the peer itself.
function clientIp(req, trustForwardedFor = true) {
    const peer = req.socket.remoteAddress || '';
    if (trustForwardedFor && isLoopback(peer)) {
        const xff = req.headers['x-forwarded-for'];
        if (typeof xff === 'string' && xff.trim()) {
            const parts = xff.split(',').map((p) => p.trim()).filter(Boolean);
            if (parts.length) return normaliseIp(parts[parts.length - 1]);
        }
    }
    return normaliseIp(peer);
}

function normaliseIp(ip) {
    const s = String(ip || '').trim();
    return s.startsWith('::ffff:') ? s.slice(7) : s;
}

// True when the request came through the reverse proxy (Caddy adds these). Used to refuse internal routes
// that must only be called directly on loopback (the Discord bot), never through the public site.
function isProxied(req) {
    return Boolean(req.headers['x-forwarded-for'] || req.headers['x-forwarded-host'] || req.headers.via);
}

function sendJson(res, status, body, headers = {}) {
    const text = JSON.stringify(body);
    res.writeHead(status, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': Buffer.byteLength(text),
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff',
        ...headers,
    });
    res.end(text);
}

class HttpError extends Error {
    constructor(status, code, message, extra = {}) {
        super(message || code);
        this.status = status;
        this.code = code;
        this.extra = extra;
    }
}

// Reads the whole body as a Buffer, refusing more than maxBytes (413) without buffering the excess.
function readBody(req, maxBytes) {
    return new Promise((resolve, reject) => {
        const declared = Number(req.headers['content-length']);
        if (Number.isFinite(declared) && declared > maxBytes) {
            reject(new HttpError(413, 'too_large', `Body larger than ${maxBytes} bytes`));
            req.resume();
            return;
        }
        const chunks = [];
        let total = 0;
        let failed = false;
        req.on('data', (chunk) => {
            if (failed) return;
            total += chunk.length;
            if (total > maxBytes) {
                failed = true;
                chunks.length = 0;
                reject(new HttpError(413, 'too_large', `Body larger than ${maxBytes} bytes`));
                req.resume();
                return;
            }
            chunks.push(chunk);
        });
        req.on('end', () => { if (!failed) resolve(Buffer.concat(chunks, total)); });
        req.on('error', (error) => { if (!failed) { failed = true; reject(error); } });
    });
}

const utf8 = new TextDecoder('utf-8', { fatal: true });

// JSON body: content type must be JSON, bytes must be valid UTF-8 (binary is refused), top level an object.
async function readJson(req, maxBytes) {
    const type = String(req.headers['content-type'] || '').toLowerCase();
    if (!type.startsWith('application/json')) throw new HttpError(415, 'json_required', 'Content-Type must be application/json');
    const raw = await readBody(req, maxBytes);
    let text;
    try {
        text = utf8.decode(raw);
    } catch {
        throw new HttpError(400, 'not_utf8', 'Body is not valid UTF-8 text');
    }
    let value;
    try {
        value = JSON.parse(text);
    } catch {
        throw new HttpError(400, 'bad_json', 'Body is not valid JSON');
    }
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'bad_json', 'Body must be a JSON object');
    return value;
}

// Constant-time comparison of two secrets of any length.
function safeEqual(a, b) {
    const ha = crypto.createHash('sha256').update(String(a)).digest();
    const hb = crypto.createHash('sha256').update(String(b)).digest();
    return crypto.timingSafeEqual(ha, hb) && String(a).length === String(b).length;
}

module.exports = { isLoopback, clientIp, isProxied, sendJson, readBody, readJson, HttpError, safeEqual, normaliseIp };
