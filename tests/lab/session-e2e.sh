#!/usr/bin/env bash
# shellcheck disable=SC2024
# tests/lab/session-e2e.sh — milestone 4/5 end-to-end session validation.
#
# Isolated topology (all inside run-owned network namespaces):
#   client A/B --veth/bridge--> exit node --veth--> HTTP service namespace
#
# The exit engine is provisioned through the real chain:
# REST session -> PostgreSQL outbox -> mTLS gRPC NodeChannel -> vpn-agent ->
# Rust control socket. Client engines are bootstrapped through protected key
# files inside the run directory — never through visible process arguments.
#
# Usage:
#   tests/lab/session-e2e.sh                    normal revocation acceptance
#   tests/lab/session-e2e.sh --drop             gRPC interruption/reconciliation
#   tests/lab/session-e2e.sh --expire-restart   local expiry across agent restart
#   tests/lab/session-e2e.sh --engine-restart   same-session client rekey
set -euo pipefail
umask 077

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
# shellcheck source=tests/lab/lab-resources.sh
. tests/lab/lab-resources.sh

MODE=normal
case "${1:-normal}" in
    normal|--normal) MODE=normal ;;
    --drop) MODE=drop ;;
    --expire-restart) MODE=expire-restart ;;
    --engine-restart) MODE=engine-restart ;;
    *) echo "usage: $0 [--drop|--expire-restart|--engine-restart]" >&2; exit 2 ;;
esac

RUN_DIR=$(mktemp -d /tmp/hk-m4-session.XXXXXX)
chmod 700 "$RUN_DIR"
RUN_TAG=$(openssl rand -hex 3)
AGENT="$RUN_DIR/vpn-agent"
PGBIN=""
PGDATA="$RUN_DIR/pgdata"
CTRL_LOG="$RUN_DIR/control.log"
AGENT_LOG="$RUN_DIR/agent.log"
SERVER_LOG="$RUN_DIR/server-engine.log"
PROXY_PID=""
CTRL_PID=""
AGENT_PID=""
BLOOP_PID=""
HTTP_PID=""
SERVER_ENGINE_PID=""
lab_scope_init

read -r CTRL_PORT PROXY_PORT PGPORT < <(python3 - <<'PY'
import socket
socks = []
ports = []
for _ in range(3):
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    ports.append(s.getsockname()[1])
    socks.append(s)
print(*ports)
for s in socks:
    s.close()
PY
)
CTRL_URL="https://127.0.0.1:$CTRL_PORT"
# Lab-only bearer values are random per run and never enter the database.
HB_TOKEN=$(openssl rand -hex 16)
ADMIN_TOKEN=$(openssl rand -hex 16)
SESSION_TOKEN_SECRET=$(openssl rand -hex 32)
PGPASSWORD=$(openssl rand -hex 24)
export PGPASSWORD

NS_EXIT="hkm4e$RUN_TAG"
NS_A="hkm4a$RUN_TAG"
NS_B="hkm4b$RUN_TAG"
NS_SVC="hkm4s$RUN_TAG"
BR_IF="hkm4br$RUN_TAG"
A_HOST_IF="hkm4va$RUN_TAG"
A_PEER_IF="hkm4a0$RUN_TAG"
B_HOST_IF="hkm4vb$RUN_TAG"
B_PEER_IF="hkm4b0$RUN_TAG"
SVC_HOST_IF="hkm4vs$RUN_TAG"
SVC_PEER_IF="hkm4s0$RUN_TAG"
ENGINE="$repo_root/engine-rust/target/release/vpn-engine"
VPN_CLI="$repo_root/engine-rust/target/release/vpn-cli"

log() { echo "== $*"; }

kill_user_pid() {
    local pid=${1:-}
    [ -n "$pid" ] || return 0
    # Only children of this run are eligible; a reused PID is left alone.
    if [ "$(ps -o ppid= -p "$pid" 2>/dev/null | tr -d ' ')" = "$$" ]; then
        kill "$pid" 2>/dev/null || true
    fi
}

stop_root_owned_pid() {
    local pid=$1 signal=$2 cmdline
    [ -n "$pid" ] || return 0
    cmdline=$(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null || true)
    case "$cmdline" in
        *"$RUN_DIR"*) ;;
        *) return 0 ;;
    esac
    lab_as_root kill -"$signal" "$pid" >/dev/null 2>&1 || true
    for _ in $(seq 100); do
        [ ! -r "/proc/$pid/cmdline" ] && return 0
        sleep 0.05
    done
    return 1
}

fail() {
    echo "session-e2e: FAIL: $*" >&2
    local keep="/tmp/hk-m4-session-failed-${RUN_DIR##*/}"
    if [ ! -e "$keep" ]; then
        cp -a "$RUN_DIR" "$keep" 2>/dev/null || true
    fi
    echo "  debug artifacts preserved at $keep" >&2
    exit 1
}

cleanup() {
    kill_user_pid "$PROXY_PID"
    kill_user_pid "$BLOOP_PID"
    kill_user_pid "$AGENT_PID"
    kill_user_pid "$CTRL_PID"
    if [ -n "$PGBIN" ] && [ -d "$PGDATA" ]; then
        "$PGBIN/pg_ctl" -D "$PGDATA" -m fast stop >/dev/null 2>&1 || true
    fi
    lab_cleanup_scope
    case "$RUN_DIR" in
        /tmp/hk-m4-session.*) rm -rf "$RUN_DIR" ;;
    esac
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

wait_for_tenths() {
    local tenths=$1; shift
    for _ in $(seq "$tenths"); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 0.1
    done
    return 1
}

api() {
    curl --cacert "$RUN_DIR/pki/ca/ca.crt" -sf "$@"
}

psql_lab() {
    "$PGBIN/psql" -h 127.0.0.1 -p "$PGPORT" -U "$(id -un)" -d postgres -Atc "$1"
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
printf '%s\n' "$PGPASSWORD" >"$RUN_DIR/postgres-password"
"$PGBIN/initdb" -D "$PGDATA" --auth=scram-sha-256 \
    --pwfile="$RUN_DIR/postgres-password" --no-sync >/dev/null
rm -f "$RUN_DIR/postgres-password"
"$PGBIN/pg_ctl" -D "$PGDATA" -l "$RUN_DIR/postgres.log" \
    -o "-p $PGPORT -k $RUN_DIR" start >/dev/null
wait_for 10 psql_lab "select 1" || fail "temporary PostgreSQL did not start"

log "lab PKI"
tests/lab/gen-lab-pki.sh init "$RUN_DIR/pki" >/dev/null
tests/lab/gen-lab-pki.sh issue "$RUN_DIR/pki" exit-node >/dev/null
tests/lab/gen-lab-pki.sh issue-server "$RUN_DIR/pki" >/dev/null
chmod 644 "$RUN_DIR/pki/server/server.crt"
chmod 600 "$RUN_DIR/pki/server/server.key"
mkdir -p "$RUN_DIR/agent-state"
chmod 700 "$RUN_DIR/agent-state"

log "isolated network namespaces"
lab_netns_add "$NS_EXIT"
lab_netns_add "$NS_A"
lab_netns_add "$NS_B"
lab_netns_add "$NS_SVC"
lab_as_root ip netns exec "$NS_EXIT" ip link add "$BR_IF" type bridge
lab_as_root ip netns exec "$NS_EXIT" ip link set "$BR_IF" up
lab_as_root ip netns exec "$NS_EXIT" ip addr add 192.168.50.1/24 dev "$BR_IF"
for ns in "$NS_EXIT" "$NS_A" "$NS_B" "$NS_SVC"; do
    lab_as_root ip netns exec "$ns" ip link set lo up
done

make_client_link() {
    local ns=$1 host_if=$2 peer_if=$3 ip=$4
    lab_root_veth_add "$host_if" "$peer_if"
    lab_as_root ip link set "$host_if" netns "$NS_EXIT"
    lab_as_root ip link set "$peer_if" netns "$ns"
    lab_as_root ip netns exec "$NS_EXIT" ip link set "$host_if" master "$BR_IF"
    lab_as_root ip netns exec "$NS_EXIT" ip link set "$host_if" up
    lab_as_root ip netns exec "$ns" ip link set "$peer_if" up
    lab_as_root ip netns exec "$ns" ip addr add "$ip/24" dev "$peer_if"
}
make_client_link "$NS_A" "$A_HOST_IF" "$A_PEER_IF" 192.168.50.2
make_client_link "$NS_B" "$B_HOST_IF" "$B_PEER_IF" 192.168.50.3

lab_root_veth_add "$SVC_HOST_IF" "$SVC_PEER_IF"
lab_as_root ip link set "$SVC_HOST_IF" netns "$NS_EXIT"
lab_as_root ip link set "$SVC_PEER_IF" netns "$NS_SVC"
lab_as_root ip netns exec "$NS_EXIT" ip link set "$SVC_HOST_IF" up
lab_as_root ip netns exec "$NS_EXIT" ip addr add 192.168.100.1/24 dev "$SVC_HOST_IF"
lab_as_root ip netns exec "$NS_EXIT" sysctl -q net.ipv4.ip_forward=1
lab_as_root ip netns exec "$NS_EXIT" ip route add 10.80.1.100/32 via 192.168.100.2
lab_as_root ip netns exec "$NS_SVC" ip link set "$SVC_PEER_IF" up
lab_as_root ip netns exec "$NS_SVC" ip addr add 192.168.100.2/24 dev "$SVC_PEER_IF"
lab_as_root ip netns exec "$NS_SVC" ip addr add 10.80.1.100/32 dev lo
lab_as_root ip netns exec "$NS_SVC" ip route add default via 192.168.100.1

mkdir "$RUN_DIR/www"
printf 'hikablue-m4-ok\n' >"$RUN_DIR/www/index.html"
lab_as_root ip netns exec "$NS_SVC" sh -c "echo \$\$ >'$RUN_DIR/http.pid'; exec python3 -m http.server 8080 --bind 10.80.1.100 --directory '$RUN_DIR/www'" \
    >"$RUN_DIR/http.log" 2>&1 &
wait_for 5 test -s "$RUN_DIR/http.pid" || fail "HTTP service did not write its pid"
HTTP_PID=$(lab_as_root cat "$RUN_DIR/http.pid")
lab_register_root_pid "$HTTP_PID" "$RUN_DIR"

log "control plane"
ASPNETCORE_URLS="$CTRL_URL" \
Kestrel__Certificates__Default__Path="$RUN_DIR/pki/server/server.crt" \
Kestrel__Certificates__Default__KeyPath="$RUN_DIR/pki/server/server.key" \
VpnControl__LabCaPath="$RUN_DIR/pki/ca/ca.crt" \
VpnControl__ConnectionString="Host=127.0.0.1;Port=$PGPORT;Database=postgres;Username=$(id -un);Password=$PGPASSWORD" \
VpnControl__HeartbeatToken="$HB_TOKEN" \
VpnControl__AdminToken="$ADMIN_TOKEN" \
VpnControl__SessionTokenSecret="$SESSION_TOKEN_SECRET" \
VpnControl__IpPoolCidr="10.80.1.128/25" \
VpnControl__OutboxDispatchMs=100 \
VpnControl__ReaperIntervalMs=200 \
VpnControl__SessionTtlSeconds=180 \
dotnet run --project control-dotnet/VpnControl --no-build >"$CTRL_LOG" 2>&1 &
CTRL_PID=$!
wait_for 20 curl --cacert "$RUN_DIR/pki/ca/ca.crt" -sf "$CTRL_URL/v1/nodes" \
    || { tail -60 "$CTRL_LOG" >&2; fail "control plane did not start"; }

start_exit_engine() {
    local log_file=$1
    rm -f "$RUN_DIR/server-metrics.sock" "$RUN_DIR/server-control.sock" "$RUN_DIR/server-engine.pid"
    lab_as_root ip netns exec "$NS_EXIT" sh -c "echo \$\$ >'$RUN_DIR/server-engine.pid'; exec '$ENGINE' server \
        --bind 192.168.50.1:51820 --tun-cidr 10.80.1.1/24 --tun-name tun0 \
        --metrics-sock '$RUN_DIR/server-metrics.sock' --metrics-group $(id -gn) \
        --control-sock '$RUN_DIR/server-control.sock' --control-uid $(id -u)" \
        >"$log_file" 2>&1 &
    wait_for 10 test -S "$RUN_DIR/server-control.sock" || { cat "$log_file" >&2; fail "exit engine control socket missing"; }
    SERVER_ENGINE_PID=$(lab_as_root cat "$RUN_DIR/server-engine.pid")
    lab_register_root_pid "$SERVER_ENGINE_PID" "$RUN_DIR"
}

stop_exit_engine() {
    stop_root_owned_pid "$SERVER_ENGINE_PID" TERM || fail "exit engine did not stop"
    SERVER_ENGINE_PID=""
}

log "exit engine"
start_exit_engine "$SERVER_LOG"

start_proxy() {
    local delay_ms=$1 log_file=$2
    python3 tests/lab/grpc-delay-proxy.py "$PROXY_PORT" "$CTRL_PORT" "$delay_ms" \
        >"$log_file" 2>&1 &
    PROXY_PID=$!
}

stop_proxy() {
    kill_user_pid "$PROXY_PID"
    PROXY_PID=""
}

start_agent() {
    local target_port=$1 log_file=$2
    "$AGENT" -serve -orchestrator "127.0.0.1:$target_port" \
        -ca "$RUN_DIR/pki/ca/ca.crt" \
        -cert "$RUN_DIR/pki/nodes/exit-node/node.crt" \
        -key "$RUN_DIR/pki/nodes/exit-node/node.key" \
        -node-id exit-node \
        -metrics-sock "$RUN_DIR/server-metrics.sock" \
        -control-sock "$RUN_DIR/server-control.sock" \
        -state-dir "$RUN_DIR/agent-state" \
        >"$log_file" 2>&1 &
    AGENT_PID=$!
}

stop_agent() {
    kill_user_pid "$AGENT_PID"
    wait "$AGENT_PID" 2>/dev/null || true
    AGENT_PID=""
}

TARGET_PORT=$CTRL_PORT
if [ "$MODE" != normal ]; then
    start_proxy 900 "$RUN_DIR/proxy.log"
    TARGET_PORT=$PROXY_PORT
fi
start_agent "$TARGET_PORT" "$AGENT_LOG"
wait_for 15 node_registered \
    || { tail -80 "$AGENT_LOG" >&2; tail -40 "$CTRL_LOG" >&2; fail "agent never registered"; }

create_session() {
    local subject=$1 out=$2 ttl=$3
    api -X POST "$CTRL_URL/v1/sessions" \
        -H 'Content-Type: application/json' -H "X-Subject: $subject" \
        -d "{\"nodeId\":\"exit-node\",\"ttlSeconds\":$ttl}" >"$out" \
        || { show_session_summary "$out"; fail "create $subject failed"; }
    cp "$out" "$out.bootstrap"
    local id token
    id=$(jq -r '.data.sessionId' "$out.bootstrap")
    token=$(jq -r '.data.sessionToken' "$out.bootstrap")
    [ "$(jq -r '.data.status' "$out.bootstrap")" = pending ] || fail "session $subject did not start pending"
    for _ in $(seq 150); do
        api "$CTRL_URL/v1/sessions/$id" -H "X-Subject: $subject" \
            -H "X-Session-Token: $token" >"$out" || true
        [ "$(jq -r '.data.status' "$out" 2>/dev/null)" = active ] && return 0
        sleep 0.1
    done
    show_session_summary "$out"
    tail -50 "$AGENT_LOG" >&2
    fail "session $subject never became active"
}

start_client() {
    local ns=$1 json=$2 tun=$3 tag=$4
    local vip egress ingress sender peer tx rx tx_file rx_file
    vip=$(jq -r '.data.virtualIp' "$json")
    egress=$(jq -r '.data.egressKey' "$json" | base64 -d | xxd -p -c 256)
    ingress=$(jq -r '.data.ingressKey' "$json" | base64 -d | xxd -p -c 256)
    sender=$(jq -r '.data.senderIndex' "$json")
    peer=$(jq -r '.data.peerIndex' "$json")
    tx=$ingress
    rx=$egress
    tx_file="$RUN_DIR/$tag-tx.key"
    rx_file="$RUN_DIR/$tag-rx.key"
    printf '%s' "$tx" >"$tx_file"
    printf '%s' "$rx" >"$rx_file"
    chmod 600 "$tx_file" "$rx_file"
    lab_as_root ip netns exec "$ns" sh -c "echo \$\$ >'$RUN_DIR/$tag-engine.pid'; exec '$ENGINE' client \
        --remote 192.168.50.1:51820 --tun-cidr '$vip/24' --peer 10.80.1.1 \
        --tun-name '$tun' --metrics-sock '$RUN_DIR/$tag-metrics.sock' \
        --tx-key-file '$tx_file' --rx-key-file '$rx_file' --sender-index '$peer' --peer-index '$sender'" \
        >"$RUN_DIR/$tag-engine.log" 2>&1 &
    wait_for 10 test -S "$RUN_DIR/$tag-metrics.sock" || { cat "$RUN_DIR/$tag-engine.log" >&2; fail "$tag engine did not start"; }
    local client_pid cmdline
    client_pid=$(lab_as_root cat "$RUN_DIR/$tag-engine.pid")
    cmdline=$(tr '\0' ' ' <"/proc/$client_pid/cmdline" 2>/dev/null || true)
    case "$cmdline" in
        *"$tx"*|*"$rx"*) fail "$tag engine exposed key material in its command line" ;;
    esac
    lab_register_root_pid "$client_pid" "$RUN_DIR"
}

assert_client_engine_key_hygiene() {
    local state_dir=$1 session_id=$2
    local pid_file="$state_dir/$session_id/engine.pid"
    local pid cmdline key_file key key_count=0 key_files
    lab_as_root test -s "$pid_file" || fail "client engine PID file missing: $pid_file"
    pid=$(lab_as_root cat "$pid_file")
    cmdline=$(lab_as_root cat "/proc/$pid/cmdline" | tr '\0' ' ' 2>/dev/null || true)
    [ -n "$cmdline" ] || fail "cannot inspect client engine command line"
    key_files=$(lab_as_root find "$state_dir/$session_id" -maxdepth 2 -type f \
        \( -name tx.key -o -name rx.key \) -print)
    while IFS= read -r key_file; do
        [ -n "$key_file" ] || continue
        key_count=$((key_count + 1))
        [ "$(lab_as_root stat -c %a "$key_file")" = 600 ] ||
            fail "client key file is not mode 0600: $key_file"
        key=$(lab_as_root cat "$key_file")
        case "$cmdline" in
            *"$key"*) fail "client engine exposed key material in its command line" ;;
        esac
    done <<<"$key_files"
    [ "$key_count" -eq 2 ] || fail "expected two protected key files for client engine"
    lab_register_root_pid "$pid" "$RUN_DIR"
}

start_client_supervisor() {
    local ns=$1 subject=$2 tag=$3
    local state_dir="$RUN_DIR/$tag-state"
    local info_file="$RUN_DIR/$tag-session-info.json"
    local log_file="$RUN_DIR/$tag-cli.log"
    local session_id token manager_pid_file manager_pid metrics_sock engine_pid_file

    lab_as_root "$VPN_CLI" \
        --control-url "$CTRL_URL" \
        --node-id exit-node \
        --subject "$subject" \
        --ca-cert "$RUN_DIR/pki/ca/ca.crt" \
        --remote 192.168.50.1:51820 \
        --peer 10.80.1.1 \
        --tun-prefix 24 \
        --tun-name tun0 \
        --netns "$ns" \
        --engine "$ENGINE" \
        --state-dir "$state_dir" \
        --session-info "$info_file" \
        >"$log_file" 2>&1 &

    wait_for_tenths 100 test -s "$info_file" || {
        cat "$log_file" >&2
        fail "$tag vpn-cli did not create its session"
    }
    session_id=$(client_info_field "$info_file" '.sessionId')
    token=$(client_info_field "$info_file" '.sessionToken')
    [ -n "$session_id" ] && [ "$session_id" != null ] || fail "$tag session ID missing"
    [ -n "$token" ] && [ "$token" != null ] || fail "$tag session token missing"
    wait_for_tenths 150 session_is_active "$session_id" "$subject" "$token" || {
        cat "$log_file" >&2
        fail "$tag session never became active"
    }

    manager_pid_file="$state_dir/$session_id/manager.pid"
    wait_for_tenths 100 lab_as_root test -s "$manager_pid_file" || {
        cat "$log_file" >&2
        fail "$tag vpn-cli manager PID file missing"
    }
    manager_pid=$(lab_as_root cat "$manager_pid_file")
    lab_register_root_pid "$manager_pid" "$RUN_DIR"

    engine_pid_file="$state_dir/$session_id/engine.pid"
    wait_for_tenths 150 lab_as_root test -s "$engine_pid_file" || {
        cat "$log_file" >&2
        fail "$tag vpn-engine did not start"
    }
    metrics_sock="$state_dir/$session_id/engine.sock"
    wait_for_tenths 100 lab_as_root test -S "$metrics_sock" || {
        cat "$log_file" >&2
        fail "$tag vpn-engine metrics socket missing"
    }
    assert_client_engine_key_hygiene "$state_dir" "$session_id"
}

show_session_summary() {
    jq -c '{
        data: {
            sessionId: .data.sessionId,
            status: .data.status,
            virtualIp: .data.virtualIp,
            expiresAt: .data.expiresAt,
            keyEpoch: .data.keyEpoch
        }
    }' "$1" >&2 2>/dev/null || echo "session response omitted (invalid or unavailable JSON)" >&2
}

stop_client() {
    local tag=$1 signal=${2:-TERM} pid pid_file manager_pid="" manager_pid_file=""
    if [ "$MODE" = engine-restart ] && [ "$tag" = client-a ]; then
        pid_file="$RUN_DIR/client-a-state/$A_ID/engine.pid"
        manager_pid_file="$RUN_DIR/client-a-state/$A_ID/manager.pid"
        lab_as_root test -s "$manager_pid_file" &&
            manager_pid=$(lab_as_root cat "$manager_pid_file")
    else
        pid_file="$RUN_DIR/$tag-engine.pid"
    fi
    lab_as_root test -s "$pid_file" || return 0
    pid=$(lab_as_root cat "$pid_file")
    stop_root_owned_pid "$pid" "$signal" || fail "$tag engine did not stop after $signal"
    if [ -n "$manager_pid" ]; then
        for _ in $(seq 100); do
            [ ! -r "/proc/$manager_pid/cmdline" ] && return 0
            sleep 0.05
        done
        stop_root_owned_pid "$manager_pid" TERM ||
            fail "$tag vpn-cli did not stop after its engine exited"
    fi
}

curl_client() {
    lab_as_root ip netns exec "$1" curl --connect-timeout 2 --max-time 3 -sf http://10.80.1.100:8080/ 2>/dev/null
}

curl_client_fast() {
    lab_as_root ip netns exec "$1" curl --connect-timeout 1 --max-time 1 -sf http://10.80.1.100:8080/ 2>/dev/null
}

session_status() {
    api "$CTRL_URL/v1/sessions/$1" -H "X-Subject: $2" -H "X-Session-Token: $3" | jq -r '.data.status'
}

session_is_active() {
    local status
    status=$(session_status "$@" 2>/dev/null) || return 1
    [ "$status" = active ]
}

session_epoch() {
    api "$CTRL_URL/v1/sessions/$1" -H "X-Subject: $2" -H "X-Session-Token: $3" |
        jq -r '.data.keyEpoch'
}

client_info_field() {
    lab_as_root cat "$1" | jq -r "$2"
}

lease_count() {
    psql_lab "select count(*) from ip_leases where session_id='$1'"
}

start_b_monitor() {
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
}

b_monitor_counts() {
    OK_COUNT=$(grep -c '^ok$' "$RUN_DIR/b-continuous.log" || true)
    FAIL_COUNT=$(grep -c '^fail$' "$RUN_DIR/b-continuous.log" || true)
}

log "direct-path probe before tunnel"
if curl_client "$NS_A" >/dev/null; then
    fail "test service is reachable without the tunnel"
fi
echo "  OBSERVED: direct path is inaccessible"

log "provision and supervise sessions A and B"
if [ "$MODE" = engine-restart ]; then
    start_client_supervisor "$NS_A" alice client-a
    A_ID=$(client_info_field "$RUN_DIR/client-a-session-info.json" '.sessionId')
    A_TOKEN=$(client_info_field "$RUN_DIR/client-a-session-info.json" '.sessionToken')
    A_IP=$(client_info_field "$RUN_DIR/client-a-session-info.json" '.virtualIp')
    A_EXPIRES_AT=$(client_info_field "$RUN_DIR/client-a-session-info.json" '.expiresAt')
    A_INITIAL_EPOCH=$(client_info_field "$RUN_DIR/client-a-session-info.json" '.keyEpoch')
    [ "$(session_epoch "$A_ID" alice "$A_TOKEN")" = "$A_INITIAL_EPOCH" ] ||
        fail "vpn-cli session epoch changed before backend restart"
else
    if [ "$MODE" = expire-restart ]; then
        create_session alice "$RUN_DIR/session-a.json" 20
    else
        create_session alice "$RUN_DIR/session-a.json" 180
    fi
    A_ID=$(jq -r '.data.sessionId' "$RUN_DIR/session-a.json")
    A_TOKEN=$(jq -r '.data.sessionToken' "$RUN_DIR/session-a.json.bootstrap")
    A_IP=$(jq -r '.data.virtualIp' "$RUN_DIR/session-a.json")
    A_EXPIRES_AT=$(jq -r '.data.expiresAt' "$RUN_DIR/session-a.json")
    A_INITIAL_EPOCH=$(jq -r '.data.keyEpoch' "$RUN_DIR/session-a.json")
fi
create_session bob "$RUN_DIR/session-b.json" 180
B_ID=$(jq -r '.data.sessionId' "$RUN_DIR/session-b.json")
B_TOKEN=$(jq -r '.data.sessionToken' "$RUN_DIR/session-b.json.bootstrap")
B_IP=$(jq -r '.data.virtualIp' "$RUN_DIR/session-b.json")
B_INITIAL_EPOCH=$(jq -r '.data.keyEpoch' "$RUN_DIR/session-b.json")
[ -n "$A_ID" ] && [ -n "$B_ID" ] || fail "session IDs missing"
[ "$A_ID" != "$B_ID" ] || fail "clients received the same session ID"
[ -n "$B_ID" ] || fail "session B id missing"
[ "$A_IP" != "$B_IP" ] || fail "clients received the same IP"
[[ "$A_INITIAL_EPOCH" =~ ^[0-9]+$ ]] || fail "session A key epoch is invalid"
[[ "$B_INITIAL_EPOCH" =~ ^[0-9]+$ ]] || fail "session B key epoch is invalid"
A_INITIAL_LAB_ID=$(psql_lab "select lab_session_id from sessions where session_id='$A_ID'")
B_INITIAL_LAB_ID=$(psql_lab "select lab_session_id from sessions where session_id='$B_ID'")
echo "  OBSERVED: A=$A_IP B=$B_IP; both pending until ACK, then active"

if [ "$MODE" != engine-restart ]; then
    start_client "$NS_A" "$RUN_DIR/session-a.json.bootstrap" tun0 client-a
fi
start_client "$NS_B" "$RUN_DIR/session-b.json.bootstrap" tun0 client-b

log "HTTP connectivity over both tunnels"
[ "$(curl_client "$NS_A")" = "hikablue-m4-ok" ] || fail "client A did not receive the expected HTTP body"
[ "$(curl_client "$NS_B")" = "hikablue-m4-ok" ] || fail "client B did not receive the expected HTTP body"
echo "  OBSERVED: clients A and B received hikablue-m4-ok through the tunnel"

if [ "$MODE" = engine-restart ]; then
    log "restart the exit engine and rekey the same client session"
    OLD_BACKEND=$(psql_lab "select backend_instance_id from nodes where node_id='exit-node'")
    [ -n "$OLD_BACKEND" ] || fail "initial backend instance id missing"
    OLD_A_EPOCH=$A_INITIAL_EPOCH
    OLD_B_EPOCH=$B_INITIAL_EPOCH
    OLD_A_WIRE_ID=$A_INITIAL_LAB_ID
    OLD_B_WIRE_ID=$B_INITIAL_LAB_ID
    [ "$(session_status "$A_ID" alice "$A_TOKEN")" = active ] ||
        fail "client A session was not active before restart"
    [ "$(session_status "$B_ID" bob "$B_TOKEN")" = active ] ||
        fail "client B session was not active before restart"
    [ "$(lease_count "$A_ID")" = 1 ] || fail "A lease missing before restart"
    [ "$(lease_count "$B_ID")" = 1 ] || fail "B lease missing before restart"
    ROUTE_BEFORE=$(lab_as_root ip netns exec "$NS_A" ip -4 route show 10.80.1.0/24 2>/dev/null || true)
    case "$ROUTE_BEFORE" in
        *"10.80.1.0/24"*) ;;
        *) fail "client A tunnel route missing before restart" ;;
    esac

    stop_exit_engine
    echo "  OBSERVED: exit engine stopped; no data-plane availability is inferred from process state"
    start_exit_engine "$RUN_DIR/server-engine-restart.log"

    AGENT_EXITED=0
    for _ in $(seq 80); do
        STATE=$(ps -o stat= -p "$AGENT_PID" 2>/dev/null | tr -d ' ' || true)
        if [ -z "$STATE" ] || [ "${STATE#Z}" != "$STATE" ]; then
            AGENT_EXITED=1
            break
        fi
        sleep 0.1
    done
    [ "$AGENT_EXITED" = 1 ] || { tail -80 "$AGENT_LOG" >&2; fail "agent did not exit after backend instance change"; }
    if wait "$AGENT_PID" 2>/dev/null; then AGENT_STATUS=0; else AGENT_STATUS=$?; fi
    AGENT_PID=""
    [ "$AGENT_STATUS" -eq 0 ] || { tail -80 "$AGENT_LOG" >&2; fail "agent crashed instead of supervised backend-change exit (status $AGENT_STATUS)"; }
    grep -q "engine backend instance changed" "$AGENT_LOG" || { tail -80 "$AGENT_LOG" >&2; fail "agent exited without backend-change evidence"; }
    echo "  OBSERVED: agent detected the new backend instance and exited for supervised restart"

    start_agent "$CTRL_PORT" "$RUN_DIR/agent-backend-restart.log"
    NEW_BACKEND=""
    for _ in $(seq 150); do
        NEW_BACKEND=$(psql_lab "select backend_instance_id from nodes where node_id='exit-node'" 2>/dev/null || true)
        [ -n "$NEW_BACKEND" ] && [ "$NEW_BACKEND" != "$OLD_BACKEND" ] && break
        sleep 0.1
    done
    [ -n "$NEW_BACKEND" ] && [ "$NEW_BACKEND" != "$OLD_BACKEND" ] || {
        tail -80 "$RUN_DIR/agent-backend-restart.log" >&2
        fail "restarted agent did not register the fresh backend instance"
    }

    A_DB="" B_DB=""
    for _ in $(seq 200); do
        A_DB=$(psql_lab "select status from sessions where session_id='$A_ID'" 2>/dev/null || true)
        B_DB=$(psql_lab "select status from sessions where session_id='$B_ID'" 2>/dev/null || true)
        [ "$A_DB" = Active ] && [ "$B_DB" = Active ] && break
        sleep 0.1
    done
    [ "$A_DB" = Active ] && [ "$B_DB" = Active ] || {
        tail -80 "$RUN_DIR/agent-backend-restart.log" >&2
        fail "sessions did not return to active after confirmed rekey (A=$A_DB B=$B_DB)"
    }

    NEW_A_EPOCH=$(session_epoch "$A_ID" alice "$A_TOKEN")
    NEW_B_EPOCH=$(session_epoch "$B_ID" bob "$B_TOKEN")
    NEW_A_WIRE_ID=$(psql_lab "select lab_session_id from sessions where session_id='$A_ID'")
    NEW_B_WIRE_ID=$(psql_lab "select lab_session_id from sessions where session_id='$B_ID'")
    [ "$NEW_A_EPOCH" -gt "$OLD_A_EPOCH" ] || fail "A key epoch did not increase"
    [ "$NEW_B_EPOCH" -gt "$OLD_B_EPOCH" ] || fail "B key epoch did not increase"
    [ "$NEW_A_WIRE_ID" != "$OLD_A_WIRE_ID" ] || fail "A wire session ID was reused"
    [ "$NEW_B_WIRE_ID" != "$OLD_B_WIRE_ID" ] || fail "B wire session ID was reused"
    [ "$NEW_A_WIRE_ID" != "$NEW_B_WIRE_ID" ] || fail "rekeyed sessions share a wire session ID"
    [ "$(psql_lab "select host(virtual_ip) from sessions where session_id='$A_ID'")" = "$A_IP" ] ||
        fail "A virtual IP changed during rekey"
    [ "$(psql_lab "select count(*) from sessions where owner_subject='alice'")" = 1 ] ||
        fail "vpn-cli created a replacement API session"
    [ "$(lease_count "$A_ID")" = 1 ] || fail "A lease was not retained across rekey"
    [ "$(lease_count "$B_ID")" = 1 ] || fail "B lease was not retained across rekey"
    echo "  OBSERVED: same API session IDs and IP leases survived reset; both key epochs and wire session IDs changed"

    if curl_client_fast "$NS_B" >/dev/null; then
        fail "client B kept connectivity with pre-restart keys"
    fi
    echo "  OBSERVED: client B's old keys and wire session ID cannot carry traffic on the new backend instance"

    wait_for_tenths 200 grep -Fq \
        "vpn-cli: session $A_ID active at epoch $NEW_A_EPOCH " "$RUN_DIR/client-a-cli.log" || {
        cat "$RUN_DIR/client-a-cli.log" >&2
        fail "vpn-cli did not restart the engine with A's new key epoch"
    }
    assert_client_engine_key_hygiene "$RUN_DIR/client-a-state" "$A_ID"
    A_HTTP=0
    for _ in $(seq 100); do
        if [ "$(curl_client_fast "$NS_A" 2>/dev/null || true)" = "hikablue-m4-ok" ]; then
            A_HTTP=1
            break
        fi
        sleep 0.1
    done
    [ "$A_HTTP" = 1 ] || {
        tail -80 "$RUN_DIR/client-a-cli.log" >&2
        fail "same-session rekey did not restore client A's HTTP connectivity"
    }
    ROUTE_AFTER_REKEY=$(lab_as_root ip netns exec "$NS_A" ip -4 route show 10.80.1.0/24 2>/dev/null || true)
    case "$ROUTE_AFTER_REKEY" in
        *"10.80.1.0/24"*) ;;
        *) fail "client A tunnel route missing after rekey" ;;
    esac
    echo "  OBSERVED: vpn-cli installed epoch $NEW_A_EPOCH and client A again received hikablue-m4-ok"

    stop_client client-a KILL
    LINK_ERR=$(mktemp "$RUN_DIR/link-check.XXXXXX")
    if lab_as_root ip netns exec "$NS_A" ip link show dev tun0 >/dev/null 2>"$LINK_ERR"; then
        fail "client A tun0 survived abrupt engine termination"
    fi
    grep -Eq 'does not exist|Cannot find device' "$LINK_ERR" || { cat "$LINK_ERR" >&2; fail "client A tun0 inspection failed"; }
    ROUTE_ERR=$(mktemp "$RUN_DIR/route-check.XXXXXX")
    ROUTE_AFTER=$(lab_as_root ip netns exec "$NS_A" ip -4 route show 10.80.1.0/24 2>"$ROUTE_ERR") || { cat "$ROUTE_ERR" >&2; fail "client A route inspection failed"; }
    [ -z "$ROUTE_AFTER" ] || fail "client A tunnel route survived abrupt termination: $ROUTE_AFTER"
    echo "  OBSERVED: abrupt client termination removed tun0 and rolled back its connected tunnel route"
    CLIENT_EXIT_STATUS=""
    for _ in $(seq 200); do
        CLIENT_EXIT_STATUS=$(session_status "$A_ID" alice "$A_TOKEN" 2>/dev/null || true)
        [ "$CLIENT_EXIT_STATUS" = revoked ] && [ "$(lease_count "$A_ID")" = 0 ] && break
        sleep 0.1
    done
    [ "$CLIENT_EXIT_STATUS" = revoked ] || fail "vpn-cli did not revoke A after its engine exited"
    [ "$(lease_count "$A_ID")" = 0 ] || fail "A lease was not released after the revoke ACK"
    echo "  OBSERVED: vpn-cli requested session revocation; the node ACK released A's lease"
elif [ "$MODE" = expire-restart ]; then
    start_b_monitor
    sleep 0.5
    b_monitor_counts
    BASE_OK=$OK_COUNT
    A_DEADLINE=$(date -d "$A_EXPIRES_AT" +%s)

    log "interrupt gRPC and restart the agent while the engine stays up"
    stop_proxy
    stop_agent
    start_agent "$PROXY_PORT" "$RUN_DIR/agent-restart.log"
    [ "$(date +%s)" -lt "$A_DEADLINE" ] || fail "agent restart missed A's expiration deadline"
    echo "  OBSERVED: gRPC channel is down; no data-plane assertion is inferred from that"

    while [ "$(date +%s)" -le "$A_DEADLINE" ]; do sleep 0.05; done
    EXPIRED_LOCAL=0
    for _ in $(seq 50); do
        if ! curl_client_fast "$NS_A" >/dev/null; then
            EXPIRED_LOCAL=1
            break
        fi
        sleep 0.1
    done
    [ "$EXPIRED_LOCAL" = 1 ] || fail "A still reached the service after its local deadline"
    echo "  OBSERVED: persisted deadline expired A without a reachable control plane"
    [ "$(curl_client_fast "$NS_B")" = "hikablue-m4-ok" ] || fail "B lost connectivity while A expired"

    for _ in $(seq 100); do
        STATUS=$(session_status "$A_ID" alice "$A_TOKEN")
        [ "$STATUS" = revoking ] || [ "$STATUS" = revoked ] && break
        sleep 0.1
    done
    [ "$STATUS" = revoking ] || fail "A status is $STATUS while gRPC is down, expected revoking"
    [ "$(lease_count "$A_ID")" = 1 ] || fail "A lease was released before removal acknowledgement"

    log "restore gRPC and reconcile expiry"
    start_proxy 20 "$RUN_DIR/proxy-restore.log"
    for _ in $(seq 200); do
        STATUS=$(session_status "$A_ID" alice "$A_TOKEN")
        [ "$STATUS" = revoked ] && break
        sleep 0.1
    done
    [ "$STATUS" = revoked ] || { tail -80 "$RUN_DIR/agent-restart.log" >&2; fail "A never reached revoked after reconnect"; }
    for _ in $(seq 20); do
        if curl_client_fast "$NS_A" >/dev/null; then
            fail "expired session A was resurrected during reconciliation"
        fi
        sleep 0.1
    done
    [ "$(lease_count "$A_ID")" = 0 ] || fail "A lease count is not zero after fenced removal ACK"
    b_monitor_counts
    [ "$OK_COUNT" -gt "$BASE_OK" ] || fail "B did not continue receiving responses"
    [ "$FAIL_COUNT" -eq 0 ] || fail "B observed $FAIL_COUNT failed responses during agent restart"
    echo "  OBSERVED: removal ACK released A's lease; B survived with zero failed responses"
else
    start_b_monitor
    sleep 0.5
    b_monitor_counts
    BEFORE=$OK_COUNT

    log "revoke session A"
    api -X DELETE "$CTRL_URL/v1/sessions/$A_ID" -H 'X-Subject: alice' \
        -H "X-Session-Token: $A_TOKEN" \
        >"$RUN_DIR/revoke-a.json" || { cat "$RUN_DIR/revoke-a.json" >&2; fail "revoke request failed"; }
    [ "$(jq -r '.data.status' "$RUN_DIR/revoke-a.json")" = revoking ] || fail "A did not enter revoking"
    echo "  OBSERVED: revoke request accepted; command application is not yet assumed"

    if [ "$MODE" = drop ]; then
        DISPATCHED=0
        for _ in $(seq 40); do
            OUTBOX_STATUS=$(psql_lab "select status from outbox where command_type='RevokePeer' and delivered_at is not null order by created_at desc limit 1" 2>/dev/null || true)
            [ "$OUTBOX_STATUS" = sent ] && { DISPATCHED=1; break; }
            sleep 0.05
        done
        [ "$DISPATCHED" = 1 ] || fail "drop scenario: RevokePeer was not sent before interruption"
        sleep 0.15
        stop_proxy
        echo "  OBSERVED: RevokePeer was sent; gRPC was interrupted before application/ACK was proven"
    fi
    sleep 0.5
    STATUS=$(session_status "$A_ID" alice "$A_TOKEN")
    LEASES=$(lease_count "$A_ID")
    if [ "$MODE" = drop ]; then
        [ "$STATUS" = revoking ] || fail "drop scenario: A is $STATUS before reconciliation, expected revoking"
        [ "$LEASES" = 1 ] || fail "drop scenario: A lease count is $LEASES before ACK, expected 1"
        echo "  OBSERVED: command was sent, lease remains reserved until node ACK"
    else
        [ "$STATUS" = revoking ] || [ "$STATUS" = revoked ] || fail "unexpected A status $STATUS during revocation"
        [ "$LEASES" = 1 ] || [ "$STATUS" = revoked ] || fail "A lease released before node confirmation"
    fi

    if [ "$MODE" != drop ] && curl_client "$NS_A" >/dev/null; then
        fail "client A still reaches the service after revocation"
    fi
    b_monitor_counts
    [ "$OK_COUNT" -gt "$BEFORE" ] || fail "client B did not continue receiving responses"
    [ "$FAIL_COUNT" -eq 0 ] || fail "client B observed $FAIL_COUNT failed responses during A revocation"
    if [ "$MODE" != drop ]; then
        echo "  OBSERVED: A lost access; B continued without a failed response"
    else
        echo "  OBSERVED: B continued; A's data-plane loss is asserted only after reconciliation"
    fi

    if [ "$MODE" = drop ]; then
        log "restore gRPC and reconcile"
        start_proxy 20 "$RUN_DIR/proxy2.log"
    fi

    for _ in $(seq 240); do
        STATUS=$(session_status "$A_ID" alice "$A_TOKEN")
        [ "$STATUS" = revoked ] && break
        sleep 0.25
    done
    [ "$STATUS" = revoked ] || { tail -80 "$AGENT_LOG" >&2; fail "A never reached revoked (last $STATUS)"; }
    [ "$(lease_count "$A_ID")" = 0 ] || fail "A lease count is nonzero after revoke ACK"
    if curl_client "$NS_A" >/dev/null; then
        fail "client A still reaches the service after reconciliation"
    fi
    echo "  OBSERVED: node ACK completed revocation; only then was A's IP released"

    b_monitor_counts
    [ "$OK_COUNT" -gt "$BEFORE" ] || fail "client B did not continue receiving responses through reconciliation"
    [ "$FAIL_COUNT" -eq 0 ] || fail "client B observed $FAIL_COUNT failed responses through reconciliation"
    echo "  OBSERVED: B survived through revocation and reconciliation without a failed response"
fi

log "forwarding metrics"
"$AGENT" --metrics-sock "$RUN_DIR/server-metrics.sock" | tee "$RUN_DIR/server-metrics.json"
echo "session-e2e ($MODE): PASS"
