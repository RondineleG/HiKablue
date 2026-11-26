#!/usr/bin/env bash
# Ephemeral Debian validation: real systemd units plus REST->outbox->gRPC->agent->engine provisioning.
set -euo pipefail
umask 077

log() { printf '\n== %s\n' "$*"; }
fail() { echo "DEBIAN-E2E-FAIL: $*" >&2; exit 1; }

log "install validation prerequisites"
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -qq
sudo apt-get install -y -qq postgresql postgresql-client curl jq openssl python3 ca-certificates libicu72 2>&1 | tee /tmp/hk-apt.log >/dev/null

log "install dotnet runtime"
sudo rm -rf /usr/share/dotnet /tmp/hk-dotnet-runtime
mkdir -p /tmp/hk-dotnet-runtime
tar xzf /tmp/hk-dotnet-runtime.tar.gz -C /tmp
sudo mkdir -p /usr/share/dotnet
sudo cp -a /tmp/hk-dotnet-runtime/. /usr/share/dotnet/
sudo chmod -R a+rX /usr/share/dotnet
sudo chmod 0755 /usr/share/dotnet/dotnet
/usr/share/dotnet/dotnet --list-runtimes | tee /tmp/hk-dotnet-runtimes.txt

log "install hikablue release"
cd /tmp
rm -rf hk-debian-e2e-release hk-pki
mkdir hk-debian-e2e-release hk-pki
tar xzf /tmp/hk-release.tar.gz -C hk-debian-e2e-release --strip-components=1
tar xzf /tmp/hk-pki.tar.gz -C hk-pki
sudo bash /tmp/hk-debian-install.sh --release /tmp/hk-debian-e2e-release

log "configure postgres"
sudo systemctl enable --now postgresql >/dev/null
for _ in $(seq 30); do
    sudo -u postgres psql -Atc 'select 1' >/dev/null 2>&1 && break
    sleep 1
done
pgpass=$(openssl rand -hex 24)
hb_token=$(openssl rand -hex 16)
admin_token=$(openssl rand -hex 16)
session_secret=$(openssl rand -hex 32)
sudo -u postgres psql -v ON_ERROR_STOP=1 >/dev/null <<SQL
DROP DATABASE IF EXISTS hikablue;
DROP USER IF EXISTS hikablue;
CREATE USER hikablue WITH PASSWORD '$pgpass';
CREATE DATABASE hikablue OWNER hikablue;
SQL

log "configure services"
sudo cp /tmp/hk-pki/ca.crt /etc/hikablue/pki/ca.crt
sudo cp /tmp/hk-pki/node.crt /etc/hikablue/pki/node.crt
sudo cp /tmp/hk-pki/node.key /etc/hikablue/pki/node.key
sudo cp /tmp/hk-pki/server.crt /etc/hikablue/pki/server.crt
sudo cp /tmp/hk-pki/server.key /etc/hikablue/pki/server.key
sudo chown root:root /etc/hikablue/pki/ca.crt /etc/hikablue/pki/node.crt /etc/hikablue/pki/server.crt
sudo chown hikablue-agent:hikablue-agent /etc/hikablue/pki/node.key
sudo chown hikablue-control:hikablue-control /etc/hikablue/pki/server.key
sudo chmod 0644 /etc/hikablue/pki/ca.crt /etc/hikablue/pki/node.crt /etc/hikablue/pki/server.crt
sudo chmod 0600 /etc/hikablue/pki/node.key /etc/hikablue/pki/server.key

sudo tee /etc/hikablue/engine.env >/dev/null <<EOF
ENGINE_BIND=127.0.0.1:51820
ENGINE_TUN_CIDR=10.232.0.1/24
ENGINE_TUN_NAME=hksys0
ENGINE_MTU=1400
AGENT_USER=hikablue-agent
EOF
sudo tee /etc/hikablue/agent.env >/dev/null <<EOF
CONTROL_ORCHESTRATOR=127.0.0.1:7443
NODE_ID=node-a
AGENT_VERSION=debian-validation
CA_PATH=/etc/hikablue/pki/ca.crt
CERT_PATH=/etc/hikablue/pki/node.crt
KEY_PATH=/etc/hikablue/pki/node.key
EOF
sudo tee /etc/hikablue/control.env >/dev/null <<EOF
ASPNETCORE_URLS=https://127.0.0.1:7443
VpnControl__ConnectionString=Host=127.0.0.1;Port=5432;Database=hikablue;Username=hikablue;Password=$pgpass
VpnControl__HeartbeatToken=$hb_token
VpnControl__AdminToken=$admin_token
VpnControl__SessionTokenSecret=$session_secret
VpnControl__IpPoolCidr=10.80.1.128/25
VpnControl__SessionTtlSeconds=180
VpnControl__OutboxDispatchMs=100
VpnControl__ReaperIntervalMs=200
Kestrel__Certificates__Default__Path=/etc/hikablue/pki/server.crt
Kestrel__Certificates__Default__KeyPath=/etc/hikablue/pki/server.key
VpnControl__LabCaPath=/etc/hikablue/pki/ca.crt
EOF
sudo sh -c 'chmod 0600 /etc/hikablue/*.env'

log "start control plane"
sudo systemctl daemon-reload
sudo systemctl restart hikablue-control.service
for _ in $(seq 30); do
    if curl --cacert /etc/hikablue/pki/ca.crt -sf https://127.0.0.1:7443/v1/nodes >/tmp/hk-nodes-initial.json 2>/dev/null; then break; fi
    sleep 1
done
curl --cacert /etc/hikablue/pki/ca.crt -sf https://127.0.0.1:7443/v1/nodes >/dev/null || {
    sudo journalctl -u hikablue-control.service --no-pager -n 100 >&2
    fail "control plane did not answer"
}
systemctl is-active hikablue-control.service | sed 's/^/control=/'

log "start engine and agent"
sudo systemctl restart hikablue-engine.service
for _ in $(seq 20); do
    [ -S /run/hikablue-engine/control.sock ] && break
    sleep 1
done
[ -S /run/hikablue-engine/control.sock ] || {
    sudo journalctl -u hikablue-engine.service --no-pager -n 100 >&2
    fail "engine control socket missing"
}
sudo systemctl restart hikablue-agent.service
registered=0
for _ in $(seq 30); do
    if curl --cacert /etc/hikablue/pki/ca.crt -sf https://127.0.0.1:7443/v1/nodes 2>/dev/null | grep -q node-a; then registered=1; break; fi
    sleep 1
done
[ "$registered" = 1 ] || {
    sudo journalctl -u hikablue-agent.service --no-pager -n 100 >&2
    sudo journalctl -u hikablue-control.service --no-pager -n 100 >&2
    fail "agent did not register"
}
printf 'engine=%s\n' "$(systemctl is-active hikablue-engine.service)"
printf 'agent=%s\n' "$(systemctl is-active hikablue-agent.service)"
stat -c '%a %U %G %n' /run/hikablue-engine/control.sock /run/hikablue-engine/engine.sock

log "provision session end to end"
create_json=/tmp/hk-session-create.json
curl --cacert /etc/hikablue/pki/ca.crt -sf -X POST https://127.0.0.1:7443/v1/sessions \
    -H 'Content-Type: application/json' -H 'X-Subject: debian-validation' \
    -H 'Idempotency-Key: debian-validation-1' \
    -d '{"nodeId":"node-a","ttlSeconds":180}' >"$create_json" || {
    fail "session creation request failed"
}
jq '.data.sessionToken="<redacted>" | .data.egressKey="<redacted>" | .data.ingressKey="<redacted>"' "$create_json"
session_id=$(jq -r '.data.sessionId' "$create_json")
session_token=$(jq -r '.data.sessionToken' "$create_json")
if [ "$session_id" = null ] || [ -z "$session_id" ]; then fail "session id missing"; fi
if [ "$session_token" = null ] || [ -z "$session_token" ]; then fail "session token missing"; fi
status=unknown
status_json=/tmp/hk-session-status.json
for _ in $(seq 100); do
    if curl --cacert /etc/hikablue/pki/ca.crt -sf "https://127.0.0.1:7443/v1/sessions/$session_id" \
        -H 'X-Subject: debian-validation' -H "X-Session-Token: $session_token" >"$status_json" 2>/dev/null; then
        status=$(jq -r '.data.status' "$status_json")
        [ "$status" = active ] && break
    fi
    sleep 0.2
done
[ "$status" = active ] || {
    cat "$status_json" >&2 2>/dev/null || true
    sudo journalctl -u hikablue-agent.service --no-pager -n 100 >&2
    sudo journalctl -u hikablue-control.service --no-pager -n 100 >&2
    fail "session did not become active (status=$status)"
}
printf 'session_status=%s\n' "$status"
printf 'session_virtual_ip=%s\n' "$(jq -r '.data.virtualIp' "$status_json")"
printf 'outbox_states=%s\n' "$(sudo -u postgres psql -d hikablue -Atc "select command_type || ':' || status || ':attempts=' || attempts from outbox order by created_at" | paste -sd, -)"

log "query engine control socket as agent"
cat >/tmp/hk-control-query.py <<'PY'
import json, socket, struct

def request(path, op):
    body = json.dumps({"request_id":"debian-validation","version":1,"op":op,"payload":{}}).encode()
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(path)
    s.sendall(struct.pack(">I", len(body)) + body)
    def read_exact(count):
        data = b""
        while len(data) < count:
            chunk = s.recv(count - len(data))
            if not chunk:
                s.close()
                raise EOFError("socket closed before complete reply")
            data += chunk
        return data
    hdr = read_exact(4)
    n = struct.unpack(">I", hdr)[0]
    data = read_exact(n)
    s.close()
    return json.loads(data)

resp = request("/run/hikablue-engine/control.sock", "GetSessions")
print("GetSessions:" + json.dumps(resp, sort_keys=True))
assert resp["status"] == "ok"
assert len(resp["result"]["peers"]) == 1
assert resp["result"]["peers"][0]["virtual_ip"].startswith("10.80.1.")
PY
chmod 0644 /tmp/hk-control-query.py
sudo -u hikablue-agent python3 /tmp/hk-control-query.py
sudo -u hikablue-agent /opt/hikablue/current/bin/vpn-agent --metrics-sock /run/hikablue-engine/engine.sock | sed 's/^/GetMetrics:/'

log "revoke session and confirm removal"
curl --cacert /etc/hikablue/pki/ca.crt -sf -X DELETE "https://127.0.0.1:7443/v1/sessions/$session_id" \
    -H 'X-Subject: debian-validation' -H "X-Session-Token: $session_token" >/tmp/hk-session-revoke.json
for _ in $(seq 100); do
    curl --cacert /etc/hikablue/pki/ca.crt -sf "https://127.0.0.1:7443/v1/sessions/$session_id" \
        -H 'X-Subject: debian-validation' -H "X-Session-Token: $session_token" >"$status_json" 2>/dev/null || true
    status=$(jq -r '.data.status' "$status_json" 2>/dev/null || echo unknown)
    [ "$status" = revoked ] && break
    sleep 0.2
done
[ "$status" = revoked ] || fail "session did not become revoked (status=$status)"
printf 'session_final_status=%s\n' "$status"
cat >/tmp/hk-control-query-empty.py <<'PY'
import json, socket, struct

def request(path, op):
    body = json.dumps({"request_id":"debian-validation","version":1,"op":op,"payload":{}}).encode()
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(path)
    s.sendall(struct.pack(">I", len(body)) + body)
    def read_exact(count):
        data = b""
        while len(data) < count:
            chunk = s.recv(count - len(data))
            if not chunk:
                s.close()
                raise EOFError("socket closed before complete reply")
            data += chunk
        return data
    hdr = read_exact(4)
    n = struct.unpack(">I", hdr)[0]
    data = read_exact(n)
    s.close()
    return json.loads(data)

resp = request("/run/hikablue-engine/control.sock", "GetSessions")
print("GetSessionsAfterRevoke:" + json.dumps(resp, sort_keys=True))
assert resp["status"] == "ok"
assert len(resp["result"]["peers"]) == 0
PY
chmod 0644 /tmp/hk-control-query-empty.py
sudo -u hikablue-agent python3 /tmp/hk-control-query-empty.py
leases_remaining=$(sudo -u postgres psql -d hikablue -Atc "select count(*) from ip_leases where session_id='$session_id'") || fail "lease count query failed"
[ "$leases_remaining" = 0 ] || fail "revocation left $leases_remaining lease(s) behind"
printf 'leases_remaining=%s\n' "$leases_remaining"

log "system evidence"
# shellcheck disable=SC1091
. /etc/os-release
printf 'debian=%s\n' "$PRETTY_NAME"
printf 'kernel=%s\n' "$(uname -r)"
printf 'release=%s\n' "$(cat /opt/hikablue/current/VERSION)"
printf 'control=%s engine=%s agent=%s\n' \
    "$(systemctl is-active hikablue-control.service)" \
    "$(systemctl is-active hikablue-engine.service)" \
    "$(systemctl is-active hikablue-agent.service)"
printf '\nJOURNAL_CONTROL\n'
sudo journalctl -u hikablue-control.service --no-pager -n 30 || true
printf '\nJOURNAL_ENGINE\n'
sudo journalctl -u hikablue-engine.service --no-pager -n 20 || true
printf '\nJOURNAL_AGENT\n'
sudo journalctl -u hikablue-agent.service --no-pager -n 40 || true
echo 'DEBIAN-E2E-PASS'
