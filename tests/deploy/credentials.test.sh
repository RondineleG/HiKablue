#!/usr/bin/env bash
# Offline credential staging/rotation test. systemd-creds is stubbed so no
# host key, live secret, or service credential is created.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp=$(mktemp -d /tmp/hk-m5-credentials-test.XXXXXX)
trap 'rm -rf -- "$tmp"' EXIT

mkdir -p "$tmp/bin" "$tmp/source" "$tmp/root"
chmod 0700 "$tmp/source"
cat >"$tmp/bin/systemd-creds" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
name=${1#--name=}
[ "$2" = "--with-key=host" ] && [ "$3" = encrypt ] && [ "$#" -eq 5 ]
[ "${FAIL_NAME:-}" != "$name" ] || exit 1
printf 'encrypted:%s\n' "$name" >"$5"
STUB
chmod 0755 "$tmp/bin/systemd-creds"
export PATH="$tmp/bin:$PATH"

write_control_source() {
    local marker=$1
    printf 'Host=db;Password=%s\n' "$marker" >"$tmp/source/connection-string"
    printf 'heartbeat-%s\n' "$marker" >"$tmp/source/heartbeat-token"
    printf 'admin-%s\n' "$marker" >"$tmp/source/admin-token"
    printf '%040d\n' 1 >"$tmp/source/session-token-secret"
    printf 'server-cert-%s\n' "$marker" >"$tmp/source/server.crt"
    printf 'server-key-%s\n' "$marker" >"$tmp/source/server.key"
    printf 'ca-%s\n' "$marker" >"$tmp/source/lab-ca.crt"
    chmod 0600 "$tmp/source/"*
}

write_control_source first
bash "$repo_root/deploy/debian/provision-credentials.sh" \
    --role control --source-dir "$tmp/source" --root "$tmp/root" >/dev/null
role="$tmp/root/etc/hikablue/credentials/control"
[ -L "$role/current" ] || { echo "FAIL: current credential version is not a symlink" >&2; exit 1; }
first=$(readlink "$role/current")
[[ "$first" =~ ^versions/[A-Za-z0-9._-]+$ ]] || {
    echo "FAIL: unexpected current credential target: $first" >&2
    exit 1
}
for name in connection-string heartbeat-token admin-token session-token-secret \
    server.crt server.key lab-ca.crt; do
    [ "$(cat "$role/current/$name")" = "encrypted:$name" ] || {
        echo "FAIL: $name was not encrypted by the stub" >&2
        exit 1
    }
    [ "$(stat -c %a "$role/current/$name")" = 600 ] || {
        echo "FAIL: $name has permissive mode" >&2
        exit 1
    }
done
if grep -R -q 'Password=first\|heartbeat-first\|admin-first' "$role"; then
    echo "FAIL: source secret was copied into the encrypted credential store" >&2
    exit 1
fi

write_control_source second
bash "$repo_root/deploy/debian/provision-credentials.sh" \
    --role control --source-dir "$tmp/source" --root "$tmp/root" >/dev/null
second=$(readlink "$role/current")
[ "$second" != "$first" ] || { echo "FAIL: rotation did not create a new version" >&2; exit 1; }
[ "$(readlink "$role/previous")" = "$first" ] || {
    echo "FAIL: previous version was not retained" >&2
    exit 1
}

bash "$repo_root/deploy/debian/provision-credentials.sh" \
    --role control --rollback --root "$tmp/root" >/dev/null
[ "$(readlink "$role/current")" = "$first" ] || {
    echo "FAIL: rollback did not restore the previous version" >&2
    exit 1
}
[ "$(readlink "$role/previous")" = "$second" ] || {
    echo "FAIL: rollback did not retain the displaced version" >&2
    exit 1
}

write_control_source third
if FAIL_NAME=server.key bash "$repo_root/deploy/debian/provision-credentials.sh" \
    --role control --source-dir "$tmp/source" --root "$tmp/root" >/dev/null 2>&1; then
    echo "FAIL: encryption failure was accepted" >&2
    exit 1
fi
[ "$(readlink "$role/current")" = "$first" ] || {
    echo "FAIL: failed rotation changed current credentials" >&2
    exit 1
}
if find "$role/versions" -maxdepth 1 -type d -name '.stage-*' | grep -q .; then
    echo "FAIL: failed rotation left a staging directory" >&2
    exit 1
fi

chmod 0644 "$tmp/source/connection-string"
if bash "$repo_root/deploy/debian/provision-credentials.sh" \
    --role control --source-dir "$tmp/source" --root "$tmp/root" >/dev/null 2>&1; then
    echo "FAIL: group-readable source secret was accepted" >&2
    exit 1
fi

echo "PASS: credentials are encrypted, rotated atomically, and safely rolled back"
