# lotr-services

Small HTTP service behind Caddy for the LotR MP Event updater (CONTRACTS.md section 4):

- **Core download queue**: at most N players (default 40) download the Core module at the same time; the others
  wait in a fair FIFO line and are told their position and when to ask again.
- **Support logs**: the updater uploads its log and gets a one-time code `LOTR-XXXX` valid 72 hours; the Discord
  bot fetches the log once with that code and attaches it to the player's ticket thread.

Node.js 24, no framework, **no npm dependency at all** (the lockfile is pinned and empty). Listens on
`127.0.0.1:3010` only; Caddy publishes the two public routes. Deployment: see [DEPLOY.md](DEPLOY.md).

## Endpoints

| Method and path | Who | Body | Answers |
|---|---|---|---|
| `POST /api/queue/slot` | updater (public) | `{ "kind": "core", "client": "<install id>" }` | `200 { token, expiresAt, heartbeatSec }` or `429 { position, retryAfter }` (+ `Retry-After` header) |
| `POST /api/queue/heartbeat` | updater (public) | `{ "token" }` | `200 { expiresAt }` or `410 { error: "slot_expired" }` |
| `POST /api/queue/release` | updater (public) | `{ "token" }` | `200 { released: true/false }` (idempotent) |
| `POST /api/support` | updater (public) | `{ "installId", "appVersion", "log" }`, JSON, max 2 MB | `200 { code, expiresAt }`, `400`, `413`, `415`, `429 { retryAfter }` |
| `GET /internal/queue/check` | Caddy `forward_auth` (loopback) | slot in `?slot=` or in `X-Forwarded-Uri` | `200`, or `403` in enforce mode |
| `GET /internal/support/<code>` | Discord bot (loopback, header `X-LotR-Secret`) | | `200 { code, createdAt, expiresAt, installId, appVersion, summary, maskedServerSide, log }`, `401`, `404 unknown_code`, `410 expired / already_used`, `409 busy` |
| `GET /healthz` | monitoring (loopback) | | `200 { ok, version, uptimeSec, queue, support }` |
| `PUT /api/site-update` | owner's PC (`site/scripts/site-update.mjs send`), **second listener** `127.0.0.1:3011` | the deploy zip; headers `X-LotR-Update-Meta` + `X-LotR-Update-Signature` | `201 { id }`, `401` bad signature, `400` (stale, sha mismatch, bad meta), `404` no public key, `409` duplicate/busy, `413`, `429`, `507` |
| `GET /status` | second listener, loopback only | | `200 { enabled, fingerprint, pending, busy }` |

Errors are always JSON `{ "error": "<code>", "message": "..." }`.

The signed mailbox for website updates (`src/site-update.js`, `siteUpdate` in the config, 30 min request timeout,
600 MB limit) is described in `site/docs/SITE-UPDATES.md`. It only stores the zip in `<dataDir>\site-update\inbox`;
`Update-Site.ps1` re-verifies it with `node src/site-update.js verify --zip <zip> --meta <entry.json> --pub <pem>`
and installs it.

### Queue rules

- `client` is the updater's install id (8-128 characters `A-Z a-z 0-9 - _`). One slot and one place in the line
  per install id: asking again returns the same token (refreshed) or the same position.
- A newcomer never overtakes someone already waiting: a slot is given only when the number of people ahead is
  smaller than the number of free slots.
- A slot lives `slotTtlSec` (180 s) after its last sign of life: grant, `heartbeat`, or an object download seen by
  `forward_auth`. The updater should heartbeat every `heartbeatSec` (a third of the TTL) and release at the end.
- A waiting place lives `waitTtlSec` (90 s) after its last poll; `retryAfter` is 5 to 60 s depending on the
  position. One IP may hold at most `maxWaitingPerIp` (6) places, against flooding with made-up install ids.
- The state is written to `<data>\lotr-services\queue-state.json` (atomic, at most once per second) and reloaded
  at start: a crash or a service restart keeps the slots and the line.
- `forward_auth` (optional Caddy route on `/files/releases/*/objects/*`): the service reads every published
  `manifest.json` to know which objects belong to modules with `"role": "core"`. Other objects always pass. Core
  objects: **advisory** (default) answers 200 and only counts missing/invalid slots (visible in `/healthz`);
  **enforce** answers 403 without a live slot. An object the manifests do not know counts as Core.

### Support log rules

- Body: `Content-Type: application/json`, valid UTF-8, at most `maxBodyBytes` (2 MB) — bigger is cut at 2 MB with
  `413` without buffering the rest. `installId` like above, `appVersion` 1-32 characters `A-Z a-z 0-9 . + - _`,
  `log` non-empty text; NUL and other control characters (except tab, CR, LF, ESC) mean binary and are refused.
- The Windows user name is masked again server-side: the folder after `Users\`, `Documents and Settings\`,
  `home/` in every spelling (`C:\Users\x\`, `C:/Users/x/`, JSON-escaped `C:\\Users\\x\\`, `\\?\C:\Users\x`,
  `/c/Users/x`, `Z:\home\x`, `/home/x/`) becomes `<user>`. `Public`, `Default`, `All Users` and placeholders such
  as `%USERNAME%` or `<user>` are kept.
- Summary computed at upload and returned to the bot: app version, store and error count read from the log
  header (`Key: value` lines such as `Store: Steam`, `Errors: 2`), or the number of `ERROR`/`FATAL` lines.
- Code `LOTR-` + 4 characters from `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (no 0/O, 1/I/L). Collision-safe: a code is
  never reused while its log or its tombstone exists; when the 4-character space gets crowded the code grows to 5,
  then 6 characters. Codes are accepted case-insensitively and without the `LOTR-` prefix.
- Stored as `<data>\support\<CODE>.json`. The first successful fetch burns the code: the file is replaced by a
  tombstone `<CODE>.burnt` (no log inside) until the original expiry, so a second fetch says `already_used`. A fetch
  interrupted before the answer is delivered keeps the code valid. A purge job deletes expired files.
- Rate limits: 3 uploads per hour per client IP and 3 per hour per install id (sliding window, in memory). The client
  IP is the right-most `X-Forwarded-For` entry, believed only when the TCP peer is loopback (Caddy): Caddy replaces
  a forged header from an untrusted client, so the right-most entry is the address Caddy saw.
- `/internal/support/<code>`: loopback peer only, refused when the request carries proxy headers (`X-Forwarded-For`,
  `X-Forwarded-Host`, `Via`), and the `X-LotR-Secret` header must match the shared secret (constant-time compare).

## Configuration

Defaults are built in; a JSON file overrides them (`--config <file>`, else `LOTR_SERVICES_CONFIG`, else
`config.json` next to `package.json`). `config.example.json` lists every key. Environment overrides:
`LOTR_SERVICES_ROOT`, `LOTR_SERVICES_PORT`, `LOTR_SUPPORT_SECRET`.

| Path | Default |
|---|---|
| data | `<root>\data` (`support\`, `lotr-services\queue-state.json`) |
| shared secret | `<root>\data\secrets\support-service.secret` (created with a random value at first start when missing) |
| releases (manifests) | `<root>\files\releases` |
| logs | `<root>\logs\lotr-services\lotr-services-YYYY-MM-DD[-n].log` |

Logs are JSON lines (`t`, `level`, `msg`, fields), a new file every UTC day and every `maxFileBytes` (10 MB), kept
`keepDays` (14) days. Log contents, full tokens and secrets are never logged (8-character prefixes only).

The service refuses to listen on anything but loopback (`allowNonLoopback` exists for experiments only).

## Run and test locally

```
npm test                      # node:test, 33 tests: queue fairness/timeouts, restart, forward_auth, size limits,
                              # binary refusal, masking, codes, expiry/burn, rate limits, internal route auth
node src/server.js            # data under .\local-root unless LOTR_SERVICES_ROOT or config.root is set
curl http://127.0.0.1:3010/healthz
```

## Discord bot side

The team's bot (`LOTR\dcbot`) has `/support code:LOTR-XXXX`, usable in any open ticket thread (and an **Updater
help** ticket button). It needs `SUPPORT_SERVICE_URL` (default `http://127.0.0.1:3010`) and
`SUPPORT_SERVICE_SECRET` or `SUPPORT_SERVICE_SECRET_FILE` in its `.env`. The bot must run on the same machine as
the service (loopback only). See the bot's README, "Updater support logs".
