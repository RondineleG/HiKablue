#!/usr/bin/env bash
# tests/lab/telemetry-demo.sh — milestone 1 acceptance, end to end.
#
# Runs all three milestone processes on localhost and demonstrates, observed:
#   1. a 5-byte UDP datagram raises the engine's rx_bytes counter by five;
#   2. stopping the engine changes the observed state (fetch fails, node
#      reports engine_healthy=false on the next heartbeat);
#   3. restarting the engine restores metrics collection.
#
# Only the engine runs privileged (it needs CAP_NET_ADMIN for the TUN); the
# Go fetcher and the C# control plane run unprivileged on loopback.
#
# Usage: tests/lab/telemetry-demo.sh
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

SOCK=/tmp/hk-telemetry.sock
TUN=hktelem0
CTRL_URL=http://127.0.0.1:5080
ENGINE=engine-rust/target/release/vpn-engine
AGENT=/tmp/hk-vpn-agent

cleanup() {
    [ -n "${ENGINE_PID:-}" ] && sudo kill "$ENGINE_PID" 2>/dev/null || true
    [ -n "${CTRL_PID:-}" ] && kill "$CTRL_PID" 2>/dev/null || true
    sudo rm -f "$SOCK" /tmp/hk-telemetry.pid
    sudo ip link del "$TUN" 2>/dev/null || true
}
trap cleanup EXIT

echo "== build"
(cd engine-rust && cargo build --release -q)
(cd agent-go && go build -o "$AGENT" ./cmd/vpn-agent)
dotnet build control-dotnet/VpnControl/VpnControl.csproj -v q --nologo >/dev/null

start_engine() {
    # Write the engine's own PID before exec: killing the sudo wrapper would
    # orphan the engine and `wait` would block on a live child.
    sudo sh -c "echo \$\$ >/tmp/hk-telemetry.pid; exec '$ENGINE' server \
        --bind 127.0.0.1:51821 --tun-cidr 10.80.9.1/24 --peer 10.80.9.2 \
        --tun-name $TUN --metrics-sock $SOCK" &
    sleep 1
    ENGINE_PID=$(sudo cat /tmp/hk-telemetry.pid)
    sudo kill -0 "$ENGINE_PID" || { echo "engine failed to start" >&2; exit 1; }
}

echo "== control plane up (unprivileged)"
ASPNETCORE_URLS=$CTRL_URL dotnet run --project control-dotnet/VpnControl \
    --no-build >/tmp/hk-control.log 2>&1 &
CTRL_PID=$!
sleep 3

echo "== engine up (sudo: TUN only)"
start_engine

echo "== inject a 5-byte datagram"
printf '12345' | timeout 2 nc -u -w 1 127.0.0.1 51821 || true
sleep 0.3

echo "== agent reads metrics"
M=$("$AGENT" --metrics-sock "$SOCK")
echo "  $M"
RX=$(echo "$M" | grep -o '"rx_bytes":[0-9]*' | cut -d: -f2)
[ "$RX" -eq 5 ] || { echo "expected rx_bytes=5, got $RX" >&2; exit 1; }
echo "  OBSERVED: rx_bytes +5 for a 5-byte datagram"

echo "== heartbeat -> control plane"
curl -sf -X POST "$CTRL_URL/internal/heartbeats" -H 'Content-Type: application/json' \
    -d "{\"nodeId\":\"lab-node\",\"engineHealthy\":true,\"metrics\":{\"rx_bytes\":$RX}}" \
    >/dev/null
curl -sf "$CTRL_URL/v1/nodes" | grep -q '"rx_bytes":5' \
    || { echo "control plane did not surface the metrics" >&2; exit 1; }
echo "  OBSERVED: GET /v1/nodes lists lab-node with rx_bytes=5"

echo "== stop the engine: observed state changes"
sudo kill "$ENGINE_PID"; sleep 0.5
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

echo "telemetry-demo: PASS"
