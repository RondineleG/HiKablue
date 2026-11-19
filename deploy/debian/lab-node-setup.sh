#!/bin/sh
# lab-node-setup.sh — prepare a Debian 12/13 node for the Phase 1 TUN/UDP lab.
#
# Idempotent. Scope: load the tun module if needed and verify the tools the
# engine needs. It changes no routes, firewall rules, or forwarding state —
# those are deferred per the Phase 1 scope (see .agc/RULES.md LAB-02).
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "run as root: sudo $0" >&2
    exit 1
fi

command -v ip >/dev/null 2>&1 || {
    echo "iproute2 is missing: apt-get install -y iproute2" >&2
    exit 1
}

# Loading the module only when /dev/net/tun is absent keeps this idempotent.
if [ ! -c /dev/net/tun ]; then
    modprobe tun
fi
[ -c /dev/net/tun ] || {
    echo "/dev/net/tun still unavailable after modprobe tun" >&2
    exit 1
}

echo "node ready: run vpngine with sudo (CAP_NET_ADMIN) per engine-rust/README.md"
