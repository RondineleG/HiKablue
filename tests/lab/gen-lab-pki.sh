#!/usr/bin/env bash
# gen-lab-pki.sh — lab-only CA and per-node certificate issuance for the
# gRPC NodeChannel (mTLS), vpn-debian-specification.md §3.
#
# LAB ONLY: CA and node keys are stored UNENCRYPTED under the output dir.
# This PKI exists so agents can exercise real mutual-TLS in the isolated
# lab; it must never sign anything meant for real traffic.
#
# Usage:
#   tests/lab/gen-lab-pki.sh init  <outdir>            create the lab CA
#   tests/lab/gen-lab-pki.sh issue <outdir> <node-id>  issue a node cert
#
# Node identity lands in the certificate CN and a SAN URI
# (spiffe://lab.hikablue/node/<node-id>) — the control plane maps that
# identity to a registered node. Rotation = re-issue the same node-id
# (new serial, same identity) and restart the agent.
set -euo pipefail
umask 077   # private keys must never be world/group-readable, even transiently

readonly CA_SUBJECT="/CN=HiKablue Lab CA/O=HiKablue lab-only"
readonly SAN_PREFIX="spiffe://lab.hikablue/node"
readonly VALID_DAYS=825

die() { echo "gen-lab-pki: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "missing dependency: $1"; }

init_ca() {
    local dir="$1"
    [[ -d "$dir/ca" ]] && die "CA already exists at $dir/ca — refusing to overwrite (delete it to reset)"
    mkdir -p "$dir/ca"
    chmod 700 "$dir/ca"

    # ECDSA P-256: small keys, supported by both .NET and Go TLS stacks.
    openssl ecparam -name prime256v1 -genkey -noout -out "$dir/ca/ca.key"
    chmod 600 "$dir/ca/ca.key"
    openssl req -x509 -new -nodes -key "$dir/ca/ca.key" -sha256 \
        -days "$VALID_DAYS" -subj "$CA_SUBJECT" \
        -addext "basicConstraints=critical,CA:TRUE" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" \
        -out "$dir/ca/ca.crt"
    echo "lab CA created at $dir/ca"
}

issue_node() {
    local dir="$1" node_id="$2"
    [[ -f "$dir/ca/ca.key" && -f "$dir/ca/ca.crt" ]] \
        || die "no CA at $dir/ca — run 'init' first"
    [[ "$node_id" =~ ^[a-z0-9][a-z0-9-]{0,62}$ ]] \
        || die "invalid node-id '$node_id' (lowercase letters, digits, dashes)"

    local nd="$dir/nodes/$node_id"
    mkdir -p "$nd"
    chmod 700 "$nd"
    # Re-issue for rotation: the private key is replaced, not kept.
    [[ -f "$nd/node.key" ]] && echo "gen-lab-pki: re-issuing $node_id (rotation)"

    openssl ecparam -name prime256v1 -genkey -noout -out "$nd/node.key"
    chmod 600 "$nd/node.key"
    openssl req -new -key "$nd/node.key" \
        -subj "/CN=$node_id/O=HiKablue lab-only" -out "$nd/node.csr"

    # SAN URI carries the node identity the control plane maps to a
    # registered node; CN mirrors it for clients that only check the subject.
    openssl x509 -req -in "$nd/node.csr" \
        -CA "$dir/ca/ca.crt" -CAkey "$dir/ca/ca.key" -CAcreateserial \
        -days "$VALID_DAYS" -sha256 \
        -extfile <(printf 'subjectAltName=URI:%s/%s\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=clientAuth\nbasicConstraints=critical,CA:FALSE\n' \
            "$SAN_PREFIX" "$node_id") \
        -out "$nd/node.crt"
    rm -f "$nd/node.csr"
    echo "issued $nd (identity: $SAN_PREFIX/$node_id)"
}


issue_server() {
    local dir="$1"
    [[ -f "$dir/ca/ca.key" && -f "$dir/ca/ca.crt" ]] \
        || die "no CA at $dir/ca — run 'init' first"
    local sd="$dir/server"
    mkdir -p "$sd"
    chmod 700 "$sd"
    openssl ecparam -name prime256v1 -genkey -noout -out "$sd/server.key"
    chmod 600 "$sd/server.key"
    openssl req -new -key "$sd/server.key" \
        -subj "/CN=localhost/O=HiKablue lab-only" -out "$sd/server.csr"
    openssl x509 -req -in "$sd/server.csr" \
        -CA "$dir/ca/ca.crt" -CAkey "$dir/ca/ca.key" -CAcreateserial \
        -days "$VALID_DAYS" -sha256 \
        -extfile <(printf 'subjectAltName=DNS:localhost,IP:127.0.0.1
keyUsage=critical,digitalSignature
extendedKeyUsage=serverAuth
basicConstraints=critical,CA:FALSE
') \
        -out "$sd/server.crt"
    rm -f "$sd/server.csr"
    chmod 600 "$sd/server.crt"
    echo "issued $sd (server identity: localhost/127.0.0.1)"
}

main() {
    need openssl
    case "${1:-}" in
        init)  [[ $# -eq 2 ]] || die "usage: $0 init <outdir>";   init_ca "$2" ;;
        issue) [[ $# -eq 3 ]] || die "usage: $0 issue <outdir> <node-id>"; issue_node "$2" "$3" ;;
        issue-server) [[ $# -eq 2 ]] || die "usage: $0 issue-server <outdir>"; issue_server "$2" ;;
        *) die "usage: $0 init <outdir> | issue <outdir> <node-id> | issue-server <outdir>" ;;
    esac
}
main "$@"
