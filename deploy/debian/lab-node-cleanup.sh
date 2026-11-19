#!/bin/sh
# lab-node-cleanup.sh — remove Phase 1 lab state from a Debian node.
#
# The interface the engine creates is transient: it disappears when the
# vpn-engine process exits and its descriptor closes. So by default this
# script only verifies that tun0 is gone. A tun0 that survives cannot be
# proven lab-owned by its name and subnet alone, so it is removed only when
# the caller explicitly asserts ownership with --lab-owned (LAB-02: cleanup
# removes only project-owned objects).
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

if [ "${1:-}" != "--lab-owned" ]; then
    echo "tun0 still exists. If vpn-engine is running, stop it first: the" >&2
    echo "interface is transient and dies with the process. A surviving tun0" >&2
    echo "cannot be proven ours by name and subnet alone, so it is left" >&2
    echo "untouched (LAB-02). If you are certain it belongs to this lab," >&2
    echo "run: sudo $0 --lab-owned" >&2
    exit 1
fi

if ip -4 addr show dev tun0 | grep -q 'inet 10\.80\.'; then
    ip link del dev tun0
    echo "removed lab tun0"
else
    echo "tun0 carries no 10.80.0.0/16 lab address; refusing to remove it" >&2
    exit 1
fi

echo "cleanup done"
