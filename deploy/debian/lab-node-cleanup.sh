#!/bin/sh
# lab-node-cleanup.sh — remove Phase 1 lab state from a Debian node.
#
# Removes only project-owned objects: the tun0 interface if it lingered past
# engine shutdown (normally the engine's closed descriptor removes it) and any
# connected route it carried. No other routes or rules are touched.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "run as root: sudo $0" >&2
    exit 1
fi

if ip link show dev tun0 >/dev/null 2>&1; then
    ip link del dev tun0
    echo "removed lingering tun0"
else
    echo "tun0 already absent"
fi

echo "cleanup done"
