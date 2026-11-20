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
# Lab-only symmetric key (Phase 2 data plane): proves the AEAD wire path
# end to end. Never a real credential — real keys arrive via the control
# plane's ProvisionPeer in a later milestone. Plaintext mode still works
# by omitting --key.
LAB_KEY=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff
PID_A=""
PID_B=""
CREATED=0

# Only deletes objects this script created (CREATED is set after the
# namespaces come up), so refusing to run never removes foreign objects.
cleanup() {
    [ -n "$PID_A" ] && kill "$PID_A" 2>/dev/null || true
    [ -n "$PID_B" ] && kill "$PID_B" 2>/dev/null || true
    if [ "$CREATED" -eq 1 ]; then
        ip netns del "$NS_A" 2>/dev/null || true
        ip netns del "$NS_B" 2>/dev/null || true
    fi
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

# LAB-02: only remove objects this script created. Refuse to run — rather
# than delete — when the lab names are already taken by something else.
for obj in "netns:$NS_A" "netns:$NS_B" "link:hkl-a" "link:hkl-b"; do
    kind="${obj%%:*}"; name="${obj#*:}"
    case "$kind" in
        netns) ip netns list | awk '{print $1}' | grep -qx "$name" 2>/dev/null && taken=1 || taken=0 ;;
        link)  ip link show "$name" >/dev/null 2>&1 && taken=1 || taken=0 ;;
    esac
    if [ "$taken" -eq 1 ]; then
        echo "netns-ping.sh: $kind $name already exists; refusing to touch a foreign object" >&2
        exit 1
    fi
done

ip netns add "$NS_A"
CREATED=1 # from here on hik-lab-a is ours; cleanup must remove it on any failure
ip netns add "$NS_B"
ip link add hkl-a type veth peer name hkl-b
ip link set hkl-a netns "$NS_A"
ip link set hkl-b netns "$NS_B"
ip netns exec "$NS_A" ip addr add "$OUTER_A/24" dev hkl-a
ip netns exec "$NS_A" ip link set hkl-a up
ip netns exec "$NS_B" ip addr add "$OUTER_B/24" dev hkl-b
ip netns exec "$NS_B" ip link set hkl-b up

# Namespaces share the host filesystem, so each engine gets its own metrics
# socket path (default /run/vpn/engine.sock would collide).
ip netns exec "$NS_A" "$ENGINE" server \
    --bind "0.0.0.0:$PORT" --tun-cidr "$VIP_A/24" --peer "$VIP_B" \
    --key "$LAB_KEY" --metrics-sock /tmp/hik-lab-a.sock &
PID_A=$!
ip netns exec "$NS_B" "$ENGINE" client \
    --bind 0.0.0.0:0 --remote "$OUTER_A:$PORT" --tun-cidr "$VIP_B/24" \
    --key "$LAB_KEY" --metrics-sock /tmp/hik-lab-b.sock &
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

# SIGTERM both engines: they must exit 0 (orderly shutdown).
kill "$PID_A" "$PID_B" 2>/dev/null || true
FAILED=0
if ! wait "$PID_A" 2>/dev/null; then
    echo "netns-ping.sh: server exited abnormally" >&2
    FAILED=1
fi
if ! wait "$PID_B" 2>/dev/null; then
    echo "netns-ping.sh: client exited abnormally" >&2
    FAILED=1
fi
PID_A=""
PID_B=""

# The transient TUNs must be gone with their engines.
for ns in "$NS_A" "$NS_B"; do
    if ip netns exec "$ns" ip link show tun0 >/dev/null 2>&1; then
        echo "netns-ping.sh: tun0 survived engine shutdown in $ns" >&2
        FAILED=1
    fi
done
[ "$FAILED" -eq 0 ] || exit 1

echo "netns-ping.sh: PASS"
