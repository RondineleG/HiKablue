#!/usr/bin/env bash
# tests/lab/telemetry-demo-safety.test.sh — LAB-02 proof: the demo refuses
# to touch resources it did not create.
#
# Cases, each verified:
#   1. a preexisting $TUN interface => demo aborts, interface survives.
#   2. the engine's UDP port occupied => demo aborts, occupier survives.
#
# Usage: sudo tests/lab/telemetry-demo-safety.test.sh
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

TUN=hktelem0
ENGINE_PORT=51821
CASES=0

# --- 1. preexisting TUN is never touched ---
if ip link show "$TUN" >/dev/null 2>&1; then
    echo "SKIP: $TUN already exists in this environment"
else
    ip tuntap add dev "$TUN" mode tun
    if tests/lab/telemetry-demo.sh >/dev/null 2>&1; then
        echo "FAIL: demo ran despite preexisting $TUN" >&2
        ip link del "$TUN"; exit 1
    fi
    ip link show "$TUN" >/dev/null \
        || { echo "FAIL: demo removed the foreign $TUN" >&2; exit 1; }
    ip link del "$TUN"
    echo "PASS: preexisting TUN refused and preserved"
    CASES=$((CASES+1))
fi

# --- 2. occupied engine port is respected ---
# The tun check runs first, so prove the tun is free before blaming the port.
ip link show "$TUN" >/dev/null 2>&1 && { echo "SKIP: $TUN exists"; exit 0; }
nc -u -l 127.0.0.1 "$ENGINE_PORT" >/dev/null 2>&1 &
HOLD_PID=$!
sleep 0.5
OUT=$(tests/lab/telemetry-demo.sh 2>&1) && {
    echo "FAIL: demo ran despite :$ENGINE_PORT being occupied" >&2
    kill "$HOLD_PID"; exit 1
}
# A generic failure is not a pass: it must be the port refusal specifically.
echo "$OUT" | grep -q "already in use"     || { echo "FAIL: demo failed for another reason: $OUT" >&2; kill "$HOLD_PID"; exit 1; }
kill -0 "$HOLD_PID" || { echo "FAIL: demo killed the port occupier" >&2; exit 1; }
kill "$HOLD_PID" 2>/dev/null || true
echo "PASS: occupied port refused, occupier preserved"
CASES=$((CASES+1))

echo "telemetry-demo-safety: $CASES case(s) PASSED"
