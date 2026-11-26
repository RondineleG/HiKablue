#!/usr/bin/env bash
# install-rollback.test.sh — offline validation of repeatable installation,
# config preservation, upgrade symlinks, and rollback. Uses a temporary root;
# no users, services, routes, or host files are touched.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp=$(mktemp -d /tmp/hk-m5-install-test.XXXXXX)
trap 'rm -rf "$tmp"' EXIT

make_release() {
    local dir=$1 version=$2 marker=$3
    mkdir -p "$dir/bin" "$dir/control" "$dir/systemd" "$dir/config"
    printf '%s\n' "$version" >"$dir/VERSION"
    printf 'marker=%s\n' "$marker" >"$dir/BUILD_INFO"
    for f in vpn-engine vpn-agent hikablue-engine-service hikablue-agent-service hikablue-control-service; do
        printf '#!/bin/sh\n# %s %s\n' "$f" "$marker" >"$dir/bin/$f"
        chmod 0755 "$dir/bin/$f"
    done
    printf 'control %s\n' "$marker" >"$dir/control/VpnControl.dll"
    for unit in hikablue-engine.service hikablue-agent.service hikablue-control.service; do
        printf '[Unit]\n# %s %s\n' "$unit" "$marker" >"$dir/systemd/$unit"
    done
    for cfg in engine.env agent.env control.env; do
        printf '# sample %s %s\n' "$cfg" "$marker" >"$dir/config/$cfg.sample"
    done
}

assert_link() {
    local path=$1 want=$2 got
    [ -L "$path" ] || { echo "FAIL: $path is not a symlink" >&2; exit 1; }
    got=$(readlink "$path")
    [ "$got" = "$want" ] || { echo "FAIL: $path -> $got, want $want" >&2; exit 1; }
}

r1="$tmp/release-v1"
r2="$tmp/release-v2"
root="$tmp/root"
make_release "$r1" v1 first
make_release "$r2" v2 second

(umask 077; bash "$repo_root/deploy/debian/install.sh" --release "$r1" --root "$root" --no-systemctl >/dev/null)
assert_link "$root/opt/hikablue/current" releases/v1
for dir in "$root/opt/hikablue" "$root/opt/hikablue/releases" "$root/etc/hikablue"; do
    [ "$(stat -c %a "$dir")" = 755 ] || {
        echo "FAIL: $dir is not service-readable under a restrictive install umask" >&2
        exit 1
    }
done
[ -f "$root/etc/systemd/system/hikablue-engine.service" ] || { echo "FAIL: unit not installed" >&2; exit 1; }
printf '# operator edit\n' >"$root/etc/hikablue/engine.env"

bash "$repo_root/deploy/debian/install.sh" --release "$r1" --root "$root" --no-systemctl >/dev/null
assert_link "$root/opt/hikablue/current" releases/v1
[ "$(cat "$root/etc/hikablue/engine.env")" = '# operator edit' ] || {
    echo "FAIL: reinstall overwrote operator config" >&2
    exit 1
}

bash "$repo_root/deploy/debian/install.sh" --release "$r2" --root "$root" --no-systemctl >/dev/null
assert_link "$root/opt/hikablue/current" releases/v2
assert_link "$root/opt/hikablue/previous" releases/v1

bash "$repo_root/deploy/debian/rollback.sh" --root "$root" --no-systemctl >/dev/null
assert_link "$root/opt/hikablue/current" releases/v1
assert_link "$root/opt/hikablue/previous" releases/v2

if bash "$repo_root/deploy/debian/install.sh" --release "$tmp/missing" --root "$root" --no-systemctl >/dev/null 2>&1; then
    echo "FAIL: missing release accepted" >&2
    exit 1
fi

echo "PASS: installation is repeatable, config is preserved, upgrade and rollback switch releases"
