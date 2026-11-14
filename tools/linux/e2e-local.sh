#!/usr/bin/env bash
# End-to-end test of lotr-updater against a LOCAL fake release (docs/LINUX.md, "Testing").
#
#   tools/linux/e2e-local.sh <lotr-updater> <lotr-updater-devserver> [port]
#
# Linux / Steam Deck / WSL: the linux-x64 builds of both programs. It also runs in Git Bash on Windows with the win-x64
# builds (paths are converted with cygpath), which is how it was first run.
#
# Everything happens in a new temp folder: a release signed with a throw-away key, a fake game, a fresh updater home per
# scenario. Never the real game, never the real server, never a real key. Nothing is installed system-wide.
#
# Scenarios: status before install (10), install (0) and a byte-for-byte comparison, second run "Nothing to do", status (0),
# a damaged + a deleted file (verify 6, verify --repair 0), the download queue answering ok / 404 / 503 / 530 / never /
# busy (all 0), a tampered manifest (4), the event-night lock (8).
set -u

if [ $# -lt 2 ]; then
    echo "usage: $0 <lotr-updater> <lotr-updater-devserver> [port]" >&2
    exit 2
fi
UPD=$1
DEV=$2
PORT=${3:-8797}
WORK=$(mktemp -d "${TMPDIR:-/tmp}/lotr-e2e.XXXXXX")
PASS=0
FAIL=0
SRV=

native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
say() { printf '%s\n' "$*"; }
ok() { PASS=$((PASS + 1)); say "  PASS  $*"; }
ko() { FAIL=$((FAIL + 1)); say "  FAIL  $*"; }

stop_server() {
    if [ -n "$SRV" ]; then
        kill "$SRV" 2>/dev/null
        wait "$SRV" 2>/dev/null
        SRV=
    fi
}
trap stop_server EXIT

# start_server <files root> <log name> [devserver serve flags...]
start_server() {
    local root=$1 name=$2
    shift 2
    stop_server
    "$DEV" serve "$(native "$root")" "$PORT" 0 "$@" >"$WORK/server-$name.log" 2>&1 &
    SRV=$!
    for _ in $(seq 1 100); do
        if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then return 0; fi
        sleep 0.1
    done
    say "the test server did not start (see $WORK/server-$name.log)"
    exit 1
}

# fresh_install_dir <name>: a copy of the untouched fake game and an empty updater home.
fresh_install_dir() {
    RUN="$WORK/run-$1"
    mkdir -p "$RUN"
    cp -R "$BENCH/game" "$RUN/game"
    GAME="$RUN/game/steamapps/common/Mount & Blade II Bannerlord"
    HOMEDIR="$RUN/home"
}

# upd <name> <args...>: runs lotr-updater in dev mode; sets CODE, output in $WORK/<name>.out
upd() {
    local name=$1
    shift
    "$UPD" "$@" --plain --dev --server "http://localhost:$PORT/" --home "$(native "$HOMEDIR")" --game "$(native "$GAME")" --trust "$(native "$KEY")" \
        </dev/null >"$WORK/$name.out" 2>&1
    CODE=$?
}

expect_code() { # <name> <expected> <what>
    if [ "$CODE" -eq "$2" ]; then ok "$3 (exit $CODE)"; else ko "$3: exit $CODE, expected $2 (see $WORK/$1.out)"; fi
}

expect_text() { # <name> <text> <what>
    if grep -qF -- "$2" "$WORK/$1.out"; then ok "$3"; else ko "$3: \"$2\" not in $WORK/$1.out"; fi
}

same_files() { # <what>
    local bad=0 d
    for d in "$BENCH/expected"/*/; do
        d=${d%/}
        diff -r -q "$d" "$GAME/Modules/$(basename "$d")" >"$WORK/diff.txt" 2>&1 || { bad=1; cat "$WORK/diff.txt"; }
    done
    if [ $bad -eq 0 ]; then ok "$1: installed files identical to the release"; else ko "$1: installed files differ"; fi
}

say "lotr-updater end-to-end test in $WORK"
"$UPD" version || { say "cannot run $UPD"; exit 1; }

BENCH="$WORK/bench"
"$DEV" demo "$(native "$BENCH")" >"$WORK/demo.out" 2>&1 || { cat "$WORK/demo.out"; exit 1; }
RELEASE=$(sed -n 's/^release=//p' "$WORK/demo.out" | tr -d '\r')
FILES="$BENCH/server/files"
KEY="$BENCH/test-key.pub.pem"
say "release $RELEASE"

# ---------------------------------------------------------------- queue ok: the whole player journey
say "[1] install, second run, damage and repair (queue grants a slot)"
start_server "$FILES" ok --queue ok
fresh_install_dir main
upd status-before status; expect_code status-before 10 "status before install says an update is available"
upd install install --yes; expect_code install 0 "install"
expect_text install "Done: release $RELEASE is installed." "install reports the release"
same_files "install"
grep -q "POST /api/queue/release" "$WORK/server-ok.log" && ok "the queue slot was released" || ko "the queue slot was not released"
upd second install --yes; expect_code second 0 "second run"
expect_text second "Nothing to do" "second run has nothing to do"
upd status-after status; expect_code status-after 0 "status after install"
DLL="$GAME/Modules/LotRMPEvent/bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll"
printf 'XXXXXXXX' | dd of="$DLL" bs=1 seek=1000 conv=notrunc 2>/dev/null
rm -f "$GAME/Modules/LotRMPEvent/ModuleData/event_items.xml"
upd verify verify; expect_code verify 6 "verify finds the damage"
expect_text verify "1 missing, 1 different" "verify names one missing and one different file"
upd repair verify --repair --yes; expect_code repair 0 "verify --repair"
expect_text repair "Repaired: your install is healthy." "repair re-verifies"
same_files "repair"

# ---------------------------------------------------------------- the queue in every state
for mode in 404 503 530 hang; do
    say "[2] queue answers $mode"
    start_server "$FILES" "q$mode" --queue "$mode"
    fresh_install_dir "q$mode"
    upd "q$mode" install --yes; expect_code "q$mode" 0 "install with the queue answering $mode"
    same_files "queue $mode"
    if [ "$mode" != 404 ]; then expect_text "q$mode" "downloading directly" "queue $mode: the updater says it downloads directly"; fi
done

say "[3] busy queue (3 x 429, then a slot)"
start_server "$FILES" busy --queue ok --busy 3
fresh_install_dir busy
upd busy install --yes; expect_code busy 0 "install after waiting in line"
same_files "busy queue"
n=$(grep -c "POST /api/queue/slot" "$WORK/server-busy.log")
[ "$n" -eq 4 ] && ok "4 slot requests (3 busy, 1 granted)" || ko "slot requests: $n, expected 4"

# ---------------------------------------------------------------- refusals
say "[4] tampered manifest"
cp -R "$FILES" "$WORK/tampered"
sed -i.bak 's/Local test release/Local test releasX/' "$WORK/tampered/releases/$RELEASE/manifest.json"
start_server "$WORK/tampered" tampered --queue ok
fresh_install_dir tampered
upd tampered install --yes; expect_code tampered 4 "a tampered manifest is refused"
[ -d "$GAME/Modules/LotRMPEvent" ] && ko "the tampered release changed the game" || ok "nothing installed from the tampered release"

say "[5] event-night lock"
cp "$BENCH/status-lock.json" "$FILES/site/status.json"
start_server "$FILES" lock --lock
fresh_install_dir lock
upd lock install --yes; expect_code lock 8 "downloads paused"
expect_text lock "Downloads are paused" "the pause notice is shown"
rm -f "$FILES/site/status.json"

stop_server
say ""
say "$PASS passed, $FAIL failed. Logs: $WORK"
[ "$FAIL" -eq 0 ]
