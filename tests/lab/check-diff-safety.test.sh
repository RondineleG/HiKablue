#!/usr/bin/env bash
# tests/lab/check-diff-safety.test.sh — unit tests for the lab-safety scanner.
#
# Builds a scratch git repository in mktemp and asserts the scanner's exit
# code for each documented case: network mutations blocked outside lab-owned
# files, secrets blocked everywhere (docs included), and fail-closed behavior
# on unresolvable refs. A passing run prints PASS/FAIL per case and exits 1
# if any case produced the wrong verdict.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
scanner="$script_dir/check-diff-safety.sh"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

git -C "$tmp" init -q
git -C "$tmp" config user.email test@example.com
git -C "$tmp" config user.name test
git -C "$tmp" commit -qm "base" --allow-empty
BASE="$(git -C "$tmp" rev-parse HEAD)"

failures=0

# case NAME EXPECTED_EXIT FILE CONTENT
case_run() {
    local name="$1" expected="$2" file="$3" content="$4"
    git -C "$tmp" checkout -q --orphan "case-$RANDOM" "$BASE" 2>/dev/null || git -C "$tmp" checkout -q "$BASE"
    git -C "$tmp" rm -rq --cached . 2>/dev/null || true
    mkdir -p "$tmp/$(dirname "$file")"
    printf '%s\n' "$content" > "$tmp/$file"
    git -C "$tmp" add "$file"
    git -C "$tmp" commit -qm "test: $name"
    local head
    head="$(git -C "$tmp" rev-parse HEAD)"
    local out rc
    out="$(CHECK_DIFF_SAFETY_ROOT="$tmp" bash "$scanner" "$BASE" "$head" 2>&1)" && rc=0 || rc=$?
    if [ "$rc" -eq "$expected" ]; then
        echo "PASS  $name (exit $rc)"
    else
        echo "FAIL  $name (expected $expected, got $rc)"
        printf '%s\n' "$out" | sed 's/^/      /'
        failures=1
    fi
}

case_run "iptables in shell script is blocked"        1 "net.sh"              "iptables -F"
case_run "default route change is blocked"            1 "route.sh"            "ip route add default via 10.0.0.1"
case_run "forwarding sysctl is blocked"               1 "fwd.sh"              "sysctl -w net.ipv4.ip_forward=1"
# The marker is concatenated so the literal never sits in this source file —
# the scanner scans every file, this one included.
pk_marker='-----BEGIN '
pk_marker+='PRIVATE KEY-----'
case_run "private key anywhere is blocked"            1 "k.txt"               "$pk_marker"
case_run "credential in markdown is blocked"          1 "README.md"           "api_key = \"sk-test\""
case_run "credential in lab file is still blocked"    1 "tests/lab/x.sh"      "password = \"hunter2\""
case_run "iptables in markdown doc is allowed"        0 "doc.md"              "Next phase will use iptables for NAT"
case_run "ip route inside lab dir is allowed"         0 "tests/lab/r.sh"      "ip route add default via 10.80.0.1"
case_run "iptables inside deploy dir is allowed"      0 "deploy/debian/s.sh"  "iptables -A FORWARD -j ACCEPT"
case_run "clean code change passes"                   0 "src.rs"              "fn main() {}"
case_run "unrelated token word in docs passes"        0 "guide.md"            "token semantics are explained here"

# Fail-closed: an unresolvable ref must not pass.
out="$(CHECK_DIFF_SAFETY_ROOT="$tmp" bash "$scanner" deadbeef HEAD 2>&1)" && rc=0 || rc=$?
if [ "$rc" -eq 1 ]; then
    echo "PASS  invalid ref fails closed (exit 1)"
else
    echo "FAIL  invalid ref (expected 1, got $rc)"
    printf '%s\n' "$out" | sed 's/^/      /'
    failures=1
fi

if [ "$failures" -eq 0 ]; then
    echo "check-diff-safety.test: ALL PASSED"
else
    echo "check-diff-safety.test: FAILURES" >&2
    exit 1
fi
