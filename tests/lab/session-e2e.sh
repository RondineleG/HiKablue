#!/usr/bin/env bash
# shellcheck disable=SC2024
# tests/lab/session-e2e.sh — milestone 4 end-to-end session validation.
#
# Isolated topology (all inside network namespaces created by this script):
#   client A/B --veth/bridge--> exit node --veth--> HTTP service namespace
#
# The exit engine is provisioned through the real chain:
# REST session -> PostgreSQL outbox -> mTLS gRPC NodeChannel -> vpn-agent ->
# Rust control socket. Client engines are bootstrapped from the session
# response (lab-only exact keys) and carry only curl-generated test traffic.
#
# Usage:
#   tests/lab/session-e2e.sh          normal revocation acceptance
#   tests/lab/session-e2e.sh --drop   gRPC interruption/reconciliation acceptance
set -euo pipefail
# Logs stay in the caller-owned run directory; the sudo process only writes stdout.

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
MODE=normal
[ "${1:-}" = "--drop" ] && MODE=drop
[ "${1:-}" = "" ] || [ "${1:-}" = "--drop" ] || { echo "usage: $0 [--drop]" >&2; exit 2; }

CTRL_PORT=7443
PROXY_PORT=7444
PGPORT=55432
CTRL_URL="https://127.0.0.1:$CTRL_PORT"
HB_TOKEN=hikablue-lab-heartbeat-token
ADMIN_TOKEN=hikablue-lab-admin-token
NS_EXIT=hkm4exit
NS_A=hkm4ca
NS_B=hkm4cb
NS_SVC=hkm4svc
ENGINE="$repo_root/engine-rust/target/release/vpn-engine"

for port in "$CTRL_PORT" "$PROXY_PORT" "$PGPORT"; do
    if ss -ltn | grep -q ":${port}\b"; then
        echo "TCP :$port already in use; refusing to proceed" >&2
        exit 1
    fi
done
for ns in "$NS_EXIT" "$NS_A" "$NS_B" "$NS_SVC"; do
    if sudo ip netns exec "$ns" true 2>/dev/null; then
        echo "namespace $ns already exists; refusing to proceed" >&2
        exit 1
    fi
done

RUN_DIR=$(mktemp -d /tmp/hk-m4-session.XXXXXX)
chmod 755 "$RUN_DIR"
AGENT="$RUN_DIR/vpn-agent"
PGBIN=""
PGDATA="$RUN_DIR/pgdata"
CTRL_LOG="$RUN_DIR/control.log"
AGENT_LOG="$RUN_DIR/agent.log"
SERVER_LOG="$RUN_DIR/server-engine.log"
PIDS=()
CREATED_NS=0
PROXY_PID=""

log() { echo "== $*"; }
fail() {
    echo "session-e2e: FAIL: $*" >&2
    local keep=/tmp/hk-m4-session-failed
    rm -rf "$keep"
    cp -a "$RUN_DIR" "$keep" 2>/dev/null || true
    echo "  debug artifacts preserved at $keep" >&2
    exit 1
}

cleanup() {
    if [ -n "$PROXY_PID" ]; then kill "$PROXY_PID" 2>/dev/null || true; fi
    for pid in "${PIDS[@]:-}"; do
        if [ -n "$pid" ]; then sudo kill "$pid" 2>/dev/null || true; fi
    done
    if [ -n "${CTRL_PID:-}" ]; then kill "$CTRL_PID" 2>/dev/null || true; fi
    if [ -n "${AGENT_PID:-}" ]; then kill "$AGENT_PID" 2>/dev/null || true; fi
    if [ -n "${BLOOP_PID:-}" ]; then kill "$BLOOP_PID" 2>/dev/null || true; fi
    if [ -n "$PGBIN" ] && [ -d "$PGDATA" ]; then
        "$PGBIN/pg_ctl" -D "$PGDATA" -m fast stop >/dev/null 2>&1 || true
    fi
    if [ "$CREATED_NS" -eq 1 ]; then
        for ns in "$NS_A" "$NS_B" "$NS_SVC" "$NS_EXIT"; do
            sudo ip netns del "$ns" 2>/dev/null || true
        done
    fi
    rm -rf "$RUN_DIR"
}
trap cleanup EXIT

wait_for() {
    local secs=$1; shift
    for _ in $(seq "$secs"); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    return 1
}

api() {
    curl --cacert "$RUN_DIR/pki/ca/ca.crt" -sf "$@"
}

node_registered() {
    api "$CTRL_URL/v1/nodes" | grep -q exit-node
}

log "build"
(cd engine-rust && cargo build --release -q)
(cd agent-go && go build -o "$AGENT" ./cmd/vpn-agent)
export PATH="$HOME/.dotnet:$PATH"
dotnet build control-dotnet/VpnControl/VpnControl.csproj -v q --nologo >/dev/null

log "temporary PostgreSQL"
PGBIN=$(dirname "$(find /usr/lib/postgresql -path '*/bin/initdb' -print 2>/dev/null | sort -V | tail -1)")
[ -x "$PGBIN/initdb" ] || fail "postgresql binaries not installed"
"$PGBIN/initdb" -D "$PGDATA" --auth=trust --no-sync >/dev/null
"$PGBIN/pg_ctl" -D "$PGDATA" -l "$RUN_DIR/postgres.log" \
    -o "-p $PGPORT -k $RUN_DIR" start >/dev/null
wait_for 10 "$PGBIN/psql" -h 127.0.0.1 -p "$PGPORT" -U "$(id -un)" -d postgres -Atc "select 1" \
    || fail "temporary PostgreSQL did not start"

log "lab PKI"
tests/lab/gen-lab-pki.sh init "$RUN_DIR/pki" >/dev/null
tests/lab/gen-lab-pki.sh issue "$RUN_DIR/pki" exit-node >/dev/null
tests/lab/gen-lab-pki.sh issue-server "$RUN_DIR/pki" >/dev/null
chmod 644 "$RUN_DIR/pki/server/server.crt"
chmod 600 "$RUN_DIR/pki/server/server.key"

log "isolated network namespaces"
sudo ip netns add "$NS_EXIT"
sudo ip netns add "$NS_A"
sudo ip netns add "$NS_B"
sudo ip netns add "$NS_SVC"
CREATED_NS=1
# One bridge inside the exit namespace; client outer traffic cannot leave
# these namespaces. The service is separately connected behind the exit node.
sudo ip netns exec "$NS_EXIT" ip link add hkm4br0 type bridge
sudo ip netns exec "$NS_EXIT" ip link set hkm4br0 up
sudo ip netns exec "$NS_EXIT" ip addr add 192.168.50.1/24 dev hkm4br0
sudo ip netns exec "$NS_EXIT" ip link set lo up
sudo ip netns exec "$NS_A" ip link set lo up
sudo ip netns exec "$NS_B" ip link set lo up
sudo ip netns exec "$NS_SVC" ip link set lo up

make_client_link() {
    local ns=$1 host_if=$2 peer_if=$3 ip=$4
    sudo ip link add "$host_if" type veth peer name "$peer_if"
    sudo ip link set "$host_if" netns "$NS_EXIT"
    sudo ip link set "$peer_if" netns "$ns"
    sudo ip netns exec "$NS_EXIT" ip link set "$host_if" master hkm4br0
    sudo ip netns exec "$NS_EXIT" ip link set "$host_if" up
    sudo ip netns exec "$ns" ip link set "$peer_if" up
    sudo ip netns exec "$ns" ip addr add "$ip/24" dev "$peer_if"
}
make_client_link "$NS_A" hkm4vca hkm4ca0 192.168.50.2
make_client_link "$NS_B" hkm4vcb hkm4cb0 192.168.50.3

sudo ip link add hkm4vsvc type veth peer name hkm4svc0
sudo ip link set hkm4vsvc netns "$NS_EXIT"
sudo ip link set hkm4svc0 netns "$NS_SVC"
sudo ip netns exec "$NS_EXIT" ip link set hkm4vsvc up
sudo ip netns exec "$NS_EXIT" ip addr add 192.168.100.1/24 dev hkm4vsvc
sudo ip netns exec "$NS_EXIT" sysctl -q net.ipv4.ip_forward=1
sudo ip netns exec "$NS_EXIT" ip route add 10.80.1.100/32 via 192.168.100.2
sudo ip netns exec "$NS_SVC" ip link set hkm4svc0 up
sudo ip netns exec "$NS_SVC" ip addr add 192.168.100.2/24 dev hkm4svc0
sudo ip netns exec "$NS_SVC" ip addr add 10.80.1.100/32 dev lo
sudo ip netns exec "$NS_SVC" ip route add default via 192.168.100.1

mkdir "$RUN_DIR/www"
printf 'hikablue-m4-ok\n' >"$RUN_DIR/www/index.html"
sudo ip netns exec "$NS_SVC" python3 -m http.server 8080 --bind 10.80.1.100 \
    --directory "$RUN_DIR/www" >"$RUN_DIR/http.log" 2>&1 &
HTTP_PID=$!
PIDS+=("$HTTP_PID")

log "control plane"
ASPNETCORE_URLS="$CTRL_URL" \
Kestrel__Certificates__Default__Path="$RUN_DIR/pki/server/server.crt" \
Kestrel__Certificates__Default__KeyPath="$RUN_DIR/pki/server/server.key" \
VpnControl__LabCaPath="$RUN_DIR/pki/ca/ca.crt" \
VpnControl__ConnectionString="Host=127.0.0.1;Port=$PGPORT;Database=postgres;Username=$(id -un)" \
VpnControl__HeartbeatToken="$HB_TOKEN" \
VpnControl__AdminToken="$ADMIN_TOKEN" \
VpnControl__IpPoolCidr="10.80.1.0/24" \
VpnControl__OutboxDispatchMs=100 \
VpnControl__SessionTtlSeconds=180 \
dotnet run --project control-dotnet/VpnControl --no-build >"$CTRL_LOG" 2>&1 &
CTRL_PID=$!
wait_for 20 curl --cacert "$RUN_DIR/pki/ca/ca.crt" -sf "$CTRL_URL/v1/nodes" \
    || { tail -60 "$CTRL_LOG" >&2; fail "control plane did not start"; }

log "exit engine and orchestrating agent"
sudo ip netns exec "$NS_EXIT" sh -c "echo \$\$ >'$RUN_DIR/server-engine.pid'; exec '$ENGINE' server \
    --bind 192.168.50.1:51820 --tun-cidr 10.80.1.1/24 --tun-name tun0 \
    --metrics-sock '$RUN_DIR/server-metrics.sock' --metrics-group $(id -gn) \
    --control-sock '$RUN_DIR/server-control.sock' --control-uid $(id -u)" \
    >"$SERVER_LOG" 2>&1 &
wait_for 10 test -S "$RUN_DIR/server-control.sock" || { cat "$SERVER_LOG" >&2; fail "exit engine control socket missing"; }
SERVER_ENGINE_PID=$(sudo cat "$RUN_DIR/server-engine.pid")
PIDS+=("$SERVER_ENGINE_PID")

TARGET_PORT=$CTRL_PORT
if [ "$MODE" = drop ]; then
    python3 tests/lab/grpc-delay-proxy.py "$PROXY_PORT" "$CTRL_PORT" 900 \
        >"$RUN_DIR/proxy.log" 2>&1 &
    PROXY_PID=$!
    TARGET_PORT=$PROXY_PORT
fi
"$AGENT" -serve -orchestrator "127.0.0.1:$TARGET_PORT" \
    -ca "$RUN_DIR/pki/ca/ca.crt" \
    -cert "$RUN_DIR/pki/nodes/exit-node/node.crt" \
    -key "$RUN_DIR/pki/nodes/exit-node/node.key" \
    -node-id exit-node \
    -metrics-sock "$RUN_DIR/server-metrics.sock" \
    -control-sock "$RUN_DIR/server-control.sock" \
    >"$AGENT_LOG" 2>&1 &
AGENT_PID=$!
wait_for 15 node_registered \
    || { tail -80 "$AGENT_LOG" >&2; tail -40 "$CTRL_LOG" >&2; fail "agent never registered"; }

create_session() {
    local subject=$1 out=$2
    api -X POST "$CTRL_URL/v1/sessions" \
        -H 'Content-Type: application/json' -H "X-Subject: $subject" \
        -d '{"nodeId":"exit-node","ttlSeconds":180}' >"$out" \
        || { cat "$out" >&2; fail "create $subject failed"; }
    local id
    id=$(jq -r '.data.sessionId' "$out")
    for _ in $(seq 30); do
        api "$CTRL_URL/v1/sessions/$id" -H "X-Subject: $subject" >"$out" || true
        [ "$(jq -r '.data.status' "$out" 2>/dev/null)" = active ] && return 0
        sleep 0.2
    done
    cat "$out" >&2
    tail -50 "$AGENT_LOG" >&2
    fail "session $subject never became active"
}

start_client() {
    local ns=$1 json=$2 tun=$3 tag=$4
    local vip egress ingress sender peer tx rx
    vip=$(jq -r '.data.virtualIp' "$json")
    egress=$(jq -r '.data.egressKey' "$json" | base64 -d | xxd -p -c 256)
    ingress=$(jq -r '.data.ingressKey' "$json" | base64 -d | xxd -p -c 256)
    sender=$(jq -r '.data.senderIndex' "$json")
    peer=$(jq -r '.data.peerIndex' "$json")
    # Client direction is the inverse of the exit engine: it transmits with
    # the engine's ingress key and stamps the index the engine expects.
    tx=$ingress
    rx=$egress
    sudo ip netns exec "$ns" sh -c "echo \$\$ >'$RUN_DIR/$tag-engine.pid'; exec '$ENGINE' client \
        --remote 192.168.50.1:51820 --tun-cidr '$vip/24' --peer 10.80.1.1 \
        --tun-name '$tun' --metrics-sock '$RUN_DIR/$tag-metrics.sock' \
        --tx-key '$tx' --rx-key '$rx' --sender-index '$peer' --peer-index '$sender'" \
        >"$RUN_DIR/$tag-engine.log" 2>&1 &
    wait_for 10 test -S "$RUN_DIR/$tag-metrics.sock" || { cat "$RUN_DIR/$tag-engine.log" >&2; fail "$tag engine did not start"; }
    PIDS+=("$(sudo cat "$RUN_DIR/$tag-engine.pid")")
}

curl_client() {
    sudo ip netns exec "$1" curl --connect-timeout 2 --max-time 3 -sf http://10.80.1.100:8080/ 2>/dev/null
}

log "direct-path probe before tunnel"
if curl_client "$NS_A" >/dev/null; then
    fail "test service is reachable without the tunnel"
fi
echo "  OBSERVED: direct path is inaccessible"

log "provision sessions A and B"
create_session alice "$RUN_DIR/session-a.json"
create_session bob "$RUN_DIR/session-b.json"
A_ID=$(jq -r '.data.sessionId' "$RUN_DIR/session-a.json")
B_ID=$(jq -r '.data.sessionId' "$RUN_DIR/session-b.json")
[ -n "$B_ID" ] || fail "session B id missing"
A_IP=$(jq -r '.data.virtualIp' "$RUN_DIR/session-a.json")
B_IP=$(jq -r '.data.virtualIp' "$RUN_DIR/session-b.json")
[ "$A_IP" != "$B_IP" ] || fail "clients received the same IP"
echo "  OBSERVED: A=$A_IP B=$B_IP; both pending until ACK, then active"

start_client "$NS_A" "$RUN_DIR/session-a.json" tun0 client-a
start_client "$NS_B" "$RUN_DIR/session-b.json" tun0 client-b

log "HTTP connectivity over both tunnels"
[ "$(curl_client "$NS_A")" = "hikablue-m4-ok" ] || fail "client A did not receive the expected HTTP body"
[ "$(curl_client "$NS_B")" = "hikablue-m4-ok" ] || fail "client B did not receive the expected HTTP body"
echo "  OBSERVED: clients A and B received hikablue-m4-ok through the tunnel"

: >"$RUN_DIR/b-continuous.log"
(
    while true; do
        if [ "$(curl_client "$NS_B")" = "hikablue-m4-ok" ]; then
            echo ok >>"$RUN_DIR/b-continuous.log"
        else
            echo fail >>"$RUN_DIR/b-continuous.log"
        fi
        sleep 0.15
    done
) &
BLOOP_PID=$!
sleep 0.5
BEFORE=$(grep -c '^ok$' "$RUN_DIR/b-continuous.log" || true)

log "revoke session A"
api -X DELETE "$CTRL_URL/v1/sessions/$A_ID" -H 'X-Subject: alice' \
    >"$RUN_DIR/revoke-a.json" || { cat "$RUN_DIR/revoke-a.json" >&2; fail "revoke request failed"; }
[ "$(jq -r '.data.status' "$RUN_DIR/revoke-a.json")" = revoking ] || fail "A did not enter revoking"
if [ "$MODE" = drop ]; then
    # Wait until the control plane has dispatched RevokePeer, then close
    # the delayed proxy while that command/ACK is still in flight. The
    # 900 ms one-way delay makes the interruption deterministic without
    # inspecting the TLS stream.
    DISPATCHED=0
    for _ in $(seq 40); do
        OUTBOX_STATUS=$("$PGBIN/psql" -h 127.0.0.1 -p "$PGPORT" -U "$(id -un)" -d postgres -Atc \
            "select status from outbox where command_type='RevokePeer' order by created_at desc limit 1" 2>/dev/null || true)
        [ "$OUTBOX_STATUS" = sent ] && { DISPATCHED=1; break; }
        sleep 0.05
    done
    [ "$DISPATCHED" = 1 ] || fail "drop scenario: RevokePeer was not dispatched before interruption"
    kill "$PROXY_PID" 2>/dev/null || true
    PROXY_PID=""
fi
sleep 0.5
STATUS=$(
    api "$CTRL_URL/v1/sessions/$A_ID" -H 'X-Subject: alice' | jq -r '.data.status'
)
LEASES=$("$PGBIN/psql" -h 127.0.0.1 -p "$PGPORT" -U "$(id -un)" -d postgres -Atc \
    "select count(*) from ip_leases where session_id='$A_ID'")
if [ "$MODE" = drop ]; then
    [ "$STATUS" = revoking ] || fail "drop scenario: A is $STATUS before reconciliation, expected revoking"
    [ "$LEASES" = 1 ] || fail "drop scenario: A lease count is $LEASES before ACK, expected 1"
    echo "  OBSERVED: gRPC interruption leaves A revoking with its IP reserved"
else
    [ "$STATUS" = revoking ] || [ "$STATUS" = revoked ] || fail "unexpected A status $STATUS during revocation"
    [ "$LEASES" = 1 ] || [ "$STATUS" = revoked ] || fail "A lease released before node confirmation"
fi

if [ "$MODE" != drop ] && curl_client "$NS_A" >/dev/null; then
    fail "client A still reaches the service after revocation"
fi
# In the drop scenario the interrupted command may not have reached the
# node yet; loss of access is asserted after reconciliation below.
AFTER=$(grep -c '^ok$' "$RUN_DIR/b-continuous.log" || true)
FAILS=$(grep -c '^fail$' "$RUN_DIR/b-continuous.log" || true)
[ "$AFTER" -gt "$BEFORE" ] || fail "client B did not continue receiving responses"
[ "$FAILS" -eq 0 ] || fail "client B observed $FAILS failed responses during A revocation"
echo "  OBSERVED: A lost access; B continued without a failed response"

if [ "$MODE" = drop ]; then
    log "restore gRPC and reconcile"
    python3 tests/lab/grpc-delay-proxy.py "$PROXY_PORT" "$CTRL_PORT" 20 \
        >"$RUN_DIR/proxy2.log" 2>&1 &
    PROXY_PID=$!
fi

for _ in $(seq 60); do
    STATUS=$(api "$CTRL_URL/v1/sessions/$A_ID" -H 'X-Subject: alice' | jq -r '.data.status')
    [ "$STATUS" = revoked ] && break
    sleep 0.25
done
[ "$STATUS" = revoked ] || { tail -80 "$AGENT_LOG" >&2; fail "A never reached revoked (last $STATUS)"; }
LEASES=$("$PGBIN/psql" -h 127.0.0.1 -p "$PGPORT" -U "$(id -un)" -d postgres -Atc \
    "select count(*) from ip_leases where session_id='$A_ID'")
[ "$LEASES" = 0 ] || fail "A lease count is $LEASES after revoke ACK"
if curl_client "$NS_A" >/dev/null; then
    fail "client A still reaches the service after reconciliation"
fi
echo "  OBSERVED: node ACK completed revocation; only then was A's IP released"

log "forwarding metrics"
"$AGENT" --metrics-sock "$RUN_DIR/server-metrics.sock" | tee "$RUN_DIR/server-metrics.json"
echo "session-e2e ($MODE): PASS"
