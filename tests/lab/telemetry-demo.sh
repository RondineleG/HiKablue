#!/usr/bin/env bash
# tests/lab/telemetry-demo.sh — milestone 1 acceptance, end to end.
#
# Runs all three milestone processes on localhost and demonstrates, observed:
#   1. a 5-byte UDP datagram raises the engine's rx_bytes counter by five;
#   2. stopping the engine changes the observed state (fetch fails, node
#      reports engineHealthy=false on the next heartbeat);
#   3. restarting the engine restores metrics collection.
#
# Only the engine runs privileged (it needs CAP_NET_ADMIN for the TUN); the
# Go fetcher and the C# control plane run unprivileged on loopback. All
# run-local state lives in an exclusive mktemp directory the script owns;
# cleanup only ever removes what this run created (LAB-01/LAB-02).
#
# The Go agent is a fetcher: it queries the socket and prints metrics.
# Forwarding them to the control plane is this script's job — the
# heartbeat POST here is the demo's integration step, not agent behaviour.
# The real push channel is the gRPC NodeChannel of a later milestone.
#
# Usage: tests/lab/telemetry-demo.sh
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

TUN=hktelem0
CTRL_URL=http://127.0.0.1:5080
CTRL_PORT=5080
ENGINE_PORT=51821
ENGINE=engine-rust/target/release/vpn-engine

# LAB-02: refuse to disturb anything we did not create.
if ip link show "$TUN" >/dev/null 2>&1; then
    echo "$TUN already exists; refusing to proceed" >&2
    exit 1
fi
if ss -lun | grep -q ":${ENGINE_PORT}\b"; then
    echo "UDP :$ENGINE_PORT already in use; refusing to proceed" >&2
    exit 1
fi
if ss -ltn | grep -q ":${CTRL_PORT}\b"; then
    echo "TCP :$CTRL_PORT already in use; refusing to proceed" >&2
    exit 1
fi

# Exclusive per-run directory: socket, pidfile, agent binary and control log
# all live inside it, and cleanup removes the directory — never a file we
# did not create. Group-traversable so the unprivileged agent (same primary
# group as the caller) reaches the 0660 socket, per the lab procedure.
RUN_DIR=$(mktemp -d /tmp/hk-telemetry.XXXXXX)
chgrp "$(id -gn)" "$RUN_DIR"
chmod 0750 "$RUN_DIR"
SOCK="$RUN_DIR/engine.sock"
PIDFILE="$RUN_DIR/engine.pid"
AGENT="$RUN_DIR/vpn-agent"
CTRL_LOG="$RUN_DIR/control.log"
CREATED_TUN=0

cleanup() {
    [ -n "${ENGINE_PID:-}" ] && sudo kill "$ENGINE_PID" 2>/dev/null || true
    [ -n "${CTRL_PID:-}" ] && kill "$CTRL_PID" 2>/dev/null || true
    # We own RUN_DIR: removing it cannot touch foreign files, whatever
    # ownership the engine gave its contents.
    rm -rf "$RUN_DIR"
    [ "$CREATED_TUN" -eq 1 ] && sudo ip link del "$TUN" 2>/dev/null || true
}
trap cleanup EXIT

# wait_ready <seconds> <command...>: poll until the check succeeds.
wait_ready() {
    local secs=$1; shift
    for _ in $(seq "$secs"); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    return 1
}

echo "== build"
(cd engine-rust && cargo build --release -q)
(cd agent-go && go build -o "$AGENT" ./cmd/vpn-agent)
dotnet build control-dotnet/VpnControl/VpnControl.csproj -v q --nologo >/dev/null

start_engine() {
    # Write the engine's own PID before exec: killing the sudo wrapper would
    # orphan the engine and `wait` would block on a live child.
    # --metrics-group gives the unprivileged agent's group access to the
    # 0660 socket, per the lab procedure (dedicated group + traversal).
    sudo sh -c "echo \$\$ >'$PIDFILE'; exec '$ENGINE' server \
        --bind 127.0.0.1:$ENGINE_PORT --tun-cidr 10.80.9.1/24 --peer 10.80.9.2 \
        --tun-name $TUN --metrics-sock '$SOCK' --metrics-group $(id -gn)" &
    # The pidfile appears before exec — grab the PID first so cleanup can
    # always kill the engine and drop the TUN, even if the socket is late.
    wait_ready 10 test -f "$PIDFILE" || { echo "engine never wrote its pid" >&2; exit 1; }
    ENGINE_PID=$(sudo cat "$PIDFILE")
    CREATED_TUN=1
    if ! wait_ready 10 test -S "$SOCK"; then
        echo "engine socket never appeared" >&2
        exit 1 # cleanup kills ENGINE_PID and removes the TUN via trap
    fi
    sudo kill -0 "$ENGINE_PID" || { echo "engine failed to start" >&2; exit 1; }
}

echo "== control plane up (unprivileged, loopback only)"
ASPNETCORE_URLS=$CTRL_URL dotnet run --project control-dotnet/VpnControl \
    --no-build >"$CTRL_LOG" 2>&1 &
CTRL_PID=$!
wait_ready 15 curl -sf "$CTRL_URL/v1/nodes" \
    || { echo "control plane never came up" >&2; exit 1; }

echo "== engine up (sudo: TUN only)"
start_engine

echo "== inject a 5-byte datagram"
printf '12345' | timeout 2 nc -u -w 1 127.0.0.1 "$ENGINE_PORT" || true
sleep 0.3

echo "== agent reads metrics"
M=$("$AGENT" --metrics-sock "$SOCK")
echo "  $M"
RX=$(echo "$M" | grep -o '"rx_bytes":[0-9]*' | cut -d: -f2)
[ "$RX" -eq 5 ] || { echo "expected rx_bytes=5, got $RX" >&2; exit 1; }
echo "  OBSERVED: rx_bytes +5 for a 5-byte datagram"

echo "== heartbeat -> control plane (demo forwards it; the agent only fetches)"
curl -sf -X POST "$CTRL_URL/internal/heartbeats" -H 'Content-Type: application/json' \
    -d "{\"nodeId\":\"lab-node\",\"engineHealthy\":true,\"metrics\":{\"rx_bytes\":$RX}}" \
    >/dev/null
curl -sf "$CTRL_URL/v1/nodes" | grep -q '"rx_bytes":5' \
    || { echo "control plane did not surface the metrics" >&2; exit 1; }
echo "  OBSERVED: GET /v1/nodes lists lab-node with rx_bytes=5"

echo "== stop the engine: observed state changes"
sudo kill "$ENGINE_PID"
wait_ready 10 test ! -S "$SOCK" || true
if "$AGENT" --metrics-sock "$SOCK" --timeout 1s >/dev/null 2>&1; then
    echo "agent fetch should fail with engine down" >&2; exit 1
fi
curl -sf -X POST "$CTRL_URL/internal/heartbeats" -H 'Content-Type: application/json' \
    -d '{"nodeId":"lab-node","engineHealthy":false}' >/dev/null
curl -sf "$CTRL_URL/v1/nodes" | grep -q '"engineHealthy":false' \
    || { echo "control plane did not reflect engine outage" >&2; exit 1; }
echo "  OBSERVED: fetch fails, node reports engineHealthy=false"

echo "== restart the engine: metrics collection recovers"
start_engine
M=$("$AGENT" --metrics-sock "$SOCK")
echo "  $M"
echo "$M" | grep -q '"rx_bytes"' || exit 1
echo "  OBSERVED: GetMetrics answers again"

# Recovery must also reach the control plane: the next heartbeat reports the
# engine healthy again and GET /v1/nodes reflects it.
curl -sf -X POST "$CTRL_URL/internal/heartbeats" -H 'Content-Type: application/json' \
    -d '{"nodeId":"lab-node","engineHealthy":true}' >/dev/null
curl -sf "$CTRL_URL/v1/nodes" | grep -q '"engineHealthy":true' \
    || { echo "control plane did not reflect recovery" >&2; exit 1; }
echo "  OBSERVED: node reports engineHealthy=true again"

echo "telemetry-demo: PASS"
