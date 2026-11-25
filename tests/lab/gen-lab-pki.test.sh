#!/usr/bin/env bash
# Focused tests for tests/lab/gen-lab-pki.sh (TEST-01): CA creation,
# node issuance, SAN identity, key permissions, rotation, and the
# refusal/error paths — all inside a temp dir, nothing persists.
set -euo pipefail

SCRIPT="$(cd "$(dirname "$0")" && pwd)/gen-lab-pki.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
pass=0

check() {
    local desc="$1"; shift
    if "$@" >/dev/null 2>&1; then
        echo "PASS  $desc"; pass=$((pass+1))
    else
        echo "FAIL  $desc" >&2; exit 1
    fi
}

expect_fail() {
    local desc="$1"; shift
    if "$@" >/dev/null 2>&1; then
        echo "FAIL  $desc (expected non-zero exit)" >&2; exit 1
    fi
    echo "PASS  $desc"; pass=$((pass+1))
}

# init creates a self-signed CA with 0600 key permissions
"$SCRIPT" init "$WORK" >/dev/null
check "init creates CA cert+key" test -f "$WORK/ca/ca.crt"
check "CA key is 0600" test "$(stat -c %a "$WORK/ca/ca.key")" = 600
check "CA cert verifies itself" openssl verify -CAfile "$WORK/ca/ca.crt" "$WORK/ca/ca.crt"

# init refuses to overwrite an existing CA (guardrail)
expect_fail "init refuses existing CA" "$SCRIPT" init "$WORK"

# issue creates a node cert that verifies against the CA, with 0600 key
"$SCRIPT" issue "$WORK" node-a >/dev/null 2>&1
check "node-a cert verifies against CA" openssl verify -CAfile "$WORK/ca/ca.crt" "$WORK/nodes/node-a/node.crt"
check "node key is 0600" test "$(stat -c %a "$WORK/nodes/node-a/node.key")" = 600

# identity lands in CN and the spiffe:// SAN URI
check "CN carries node-id" bash -c "openssl x509 -in '$WORK/nodes/node-a/node.crt' -noout -subject | grep -q 'CN = node-a'"
check "SAN URI carries node-id" bash -c "openssl x509 -in '$WORK/nodes/node-a/node.crt' -noout -ext subjectAltName | grep -q 'URI:spiffe://lab.hikablue/node/node-a'"

# rotation: re-issuing same node-id produces a new serial, still verifiable
s1=$(openssl x509 -in "$WORK/nodes/node-a/node.crt" -noout -serial | cut -d= -f2)
"$SCRIPT" issue "$WORK" node-a >/dev/null 2>&1
s2=$(openssl x509 -in "$WORK/nodes/node-a/node.crt" -noout -serial | cut -d= -f2)
check "rotation produces a new serial" test "$s1" != "$s2"
check "rotated cert still verifies" openssl verify -CAfile "$WORK/ca/ca.crt" "$WORK/nodes/node-a/node.crt"

# server issuance carries the loopback server identity and serverAuth EKU
"$SCRIPT" issue-server "$WORK" >/dev/null 2>&1
check "server cert verifies against CA" openssl verify -CAfile "$WORK/ca/ca.crt" "$WORK/server/server.crt"
check "server key is 0600" test "$(stat -c %a "$WORK/server/server.key")" = 600
check "server SAN covers localhost" bash -c "openssl x509 -in '$WORK/server/server.crt' -noout -ext subjectAltName | grep -q 'DNS:localhost'"
check "server certificate has serverAuth" bash -c "openssl x509 -in '$WORK/server/server.crt' -noout -ext extendedKeyUsage | grep -q 'TLS Web Server Authentication'"

# error paths fail closed
expect_fail "issue without CA fails" "$SCRIPT" issue "$WORK/missing" node-x
expect_fail "issue-server without CA fails" "$SCRIPT" issue-server "$WORK/missing"
expect_fail "invalid node-id rejected" "$SCRIPT" issue "$WORK" "BAD_ID!"
expect_fail "unknown subcommand fails" "$SCRIPT" bogus "$WORK"

echo "gen-lab-pki.test: ${pass} case(s) PASSED"
