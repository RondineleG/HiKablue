#!/usr/bin/env sh
# Integration test for the Phase 1 loop using Linux network namespaces:
# two nodes joined by a veth pair, one vpn-engine server and one client,
# then a real ping between the tunnel's virtual IPs in both directions.
#
# Requirements: root (or CAP_NET_ADMIN + CAP_SYS_ADMIN), iproute2, a built
# release binary. No Docker, no persistent interfaces — the namespaces and
# the transient TUN devices vanish on cleanup.
#
# Usage: sudo tests/lab/netns-ping.sh [path/to/vpn-engine]

set -eu

ENGINE="${1:-engine-rust/target/release/vpn-engine}"
NS_A="hik-lab-a"
NS_B="hik-lab-b"
OUTER_A="192.168.57.1"
OUTER_B="192.168.57.2"
VIP_A="10.80.1.1"
VIP_B="10.80.1.2"
PORT=51820
PID_A=""
PID_B=""

cleanup() {
    [ -n "$PID_A" ] && kill "$PID_A" 2>/dev/null || true
    [ -n "$PID_B" ] && kill "$PID_B" 2>/dev/null || true
    ip netns del "$NS_A" 2>/dev/null || true
    ip netns del "$NS_B" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

if [ "$(id -u)" -ne 0 ]; then
    echo "netns-ping.sh: run as root (needs ip netns and /dev/net/tun)" >&2
    exit 1
fi
[ -x "$ENGINE" ] || {
    echo "netns-ping.sh: engine not found at $ENGINE (cargo build --release first)" >&2
    exit 1
}

cleanup # stale state from a previous run

ip netns add "$NS_A"
ip netns add "$NS_B"
ip link add hkl-a type veth peer name hkl-b
ip link set hkl-a netns "$NS_A"
ip link set hkl-b netns "$NS_B"
ip netns exec "$NS_A" ip addr add "$OUTER_A/24" dev hkl-a
ip netns exec "$NS_A" ip link set hkl-a up
ip netns exec "$NS_B" ip addr add "$OUTER_B/24" dev hkl-b
ip netns exec "$NS_B" ip link set hkl-b up

ip netns exec "$NS_A" "$ENGINE" server \
    --bind "0.0.0.0:$PORT" --tun-cidr "$VIP_A/24" --peer "$VIP_B" &
PID_A=$!
ip netns exec "$NS_B" "$ENGINE" client \
    --bind 0.0.0.0:0 --remote "$OUTER_A:$PORT" --tun-cidr "$VIP_B/24" &
PID_B=$!
sleep 1

for pid in $PID_A $PID_B; do
    kill -0 "$pid" 2>/dev/null || {
        echo "netns-ping.sh: an engine exited during startup" >&2
        exit 1
    }
done

# The client must ping first: the server learns its endpoint from the first
# valid datagram.
echo "== B -> A =="
ip netns exec "$NS_B" ping -c 3 -W 2 "$VIP_A"
echo "== A -> B =="
ip netns exec "$NS_A" ping -c 3 -W 2 "$VIP_B"

kill "$PID_A" "$PID_B" 2>/dev/null || true
wait "$PID_A" 2>/dev/null || true
wait "$PID_B" 2>/dev/null || true
PID_A=""
PID_B=""

# The transient TUNs must be gone with their engines.
if ip netns exec "$NS_A" ip link show tun0 >/dev/null 2>&1; then
    echo "netns-ping.sh: tun0 survived engine shutdown in $NS_A" >&2
    exit 1
fi

echo "netns-ping.sh: PASS"
