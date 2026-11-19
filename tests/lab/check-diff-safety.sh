#!/usr/bin/env bash
# tests/lab/check-diff-safety.sh — lab-safety guardrail.
#
# Scans the lines ADDED by this branch's diff for patterns the LAB/SEC rules
# forbid outside project-owned lab objects: firewall tool invocations,
# default-route changes, forwarding sysctls, and embedded secrets. Lab-owned
# files (deploy/debian/**, tests/lab/**) are exempt — scoped rule changes are
# expected to live there, reviewed by humans.
#
# Usage:
#   tests/lab/check-diff-safety.sh [BASE_SHA] [HEAD_SHA]
# Defaults: merge-base of HEAD with origin/main, and HEAD.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

base="${1:-$(git merge-base HEAD origin/main)}"
head="${2:-$(git rev-parse HEAD)}"

# Forbidden patterns (grep -E) scanned in added lines of non-exempt files.
patterns='iptables|(^|[^a-z])nft(ables)?[^a-z]|ip route (add|del|replace|change)[^|]*\bdefault\b|net\.ipv4\.ip_forward|BEGIN [A-Z ]*PRIVATE KEY|(password|passwd|secret|api_?key|token)[[:space:]]*=[[:space:]]*["'"'"'][^"'"'"']+'

hits=0
while IFS= read -r file; do
    case "$file" in
        deploy/debian/*|tests/lab/*) continue ;;
    esac
    found="$(git diff --unified=0 "$base" "$head" -- "$file" \
        | grep -E '^\+' | grep -vE '^\+\+\+' | grep -nE "$patterns" || true)"
    if [ -n "$found" ]; then
        echo "check-diff-safety: $file"
        echo "$found" | sed 's/^/    /'
        hits=1
    fi
done < <(git diff --name-only "$base" "$head")

if [ "$hits" -eq 1 ]; then
    echo "check-diff-safety: FAIL — forbidden patterns outside lab-owned files" >&2
    exit 1
fi
echo "check-diff-safety: PASS"
