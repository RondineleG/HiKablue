#!/bin/sh
# lab-node-cleanup.sh — remove Phase 1 lab state from a Debian node.
#
# Removes tun0 only when it carries an address inside the project's lab
# tunnel pool (10.80.0.0/16, per vpn-debian-specification.md section 3). A
# tun0 owned by another VPN is left untouched (LAB-02: cleanup removes only
# project-owned objects). Normally the engine's closed descriptor already
# removes the transient interface; this handles a lingering persistent one.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "run as root: sudo $0" >&2
    exit 1
fi

if ! ip link show dev tun0 >/dev/null 2>&1; then
    echo "tun0 already absent"
    echo "cleanup done"
    exit 0
fi

if ip -4 addr show dev tun0 | grep -q 'inet 10\.80\.'; then
    ip link del dev tun0
    echo "removed lab tun0"
else
    echo "tun0 exists but is not the lab interface (no 10.80.0.0/16 address); left alone" >&2
    exit 1
fi

echo "cleanup done"
