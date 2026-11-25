#!/usr/bin/env bash
# install.sh — idempotently install one versioned HiKablue release tree.
# It copies files and systemd units only; it does not start services, change
# routes, change firewall state, or provision secrets.
set -euo pipefail

release=""
root="/"
skip_users=0
no_systemctl=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --release)
            [ "$#" -ge 2 ] || { echo "--release requires a directory" >&2; exit 2; }
            release=$2
            shift
            ;;
        --root)
            [ "$#" -ge 2 ] || { echo "--root requires a directory" >&2; exit 2; }
            root=$2
            shift
            ;;
        --skip-users) skip_users=1 ;;
        --no-systemctl) no_systemctl=1 ;;
        *) echo "usage: $0 --release DIR [--root DIR] [--skip-users] [--no-systemctl]" >&2; exit 2 ;;
    esac
    shift
done

[ -n "$release" ] || { echo "--release is required" >&2; exit 2; }
[ -d "$release" ] || { echo "release directory not found: $release" >&2; exit 1; }
[ -f "$release/VERSION" ] || { echo "release has no VERSION file: $release" >&2; exit 1; }
version=$(tr -cd 'A-Za-z0-9._-' <"$release/VERSION" | head -c 80)
[ -n "$version" ] || { echo "release VERSION is empty or invalid" >&2; exit 1; }
for required in \
    bin/vpn-engine bin/vpn-agent bin/hikablue-engine-service \
    bin/hikablue-agent-service bin/hikablue-control-service \
    systemd/hikablue-engine.service systemd/hikablue-agent.service \
    systemd/hikablue-control.service control/VpnControl.dll config/engine.env.sample \
    config/agent.env.sample config/control.env.sample; do
    [ -e "$release/$required" ] || { echo "release is missing $required" >&2; exit 1; }
done

if [ "$root" = "/" ]; then
    root=""
    real_system=1
else
    real_system=0
fi
if [ "$real_system" -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
    echo "run as root for a real install, or use --root for staged/offline validation" >&2
    exit 1
fi

path_in_root() {
    printf '%s%s\n' "$root" "$1"
}

base=$(path_in_root /opt/hikablue)
releases="$base/releases"
target="$releases/$version"
current="$base/current"
previous="$base/previous"
systemd_dir=$(path_in_root /etc/systemd/system)
config_dir=$(path_in_root /etc/hikablue)

mkdir -p "$releases" "$systemd_dir" "$config_dir"

if [ -d "$target" ]; then
    if diff -qr "$release" "$target" >/dev/null 2>&1; then
        echo "release $version already installed unchanged"
    else
        echo "release directory $target exists with different content; build a new version" >&2
        exit 1
    fi
else
    tmp="$releases/.new-$version.$$"
    rm -rf -- "$tmp"
    mkdir -p "$tmp"
    cp -a "$release/." "$tmp/"
    chmod 0755 "$tmp/bin/"*
    mv -- "$tmp" "$target"
    if [ "$real_system" -eq 1 ]; then chown -R root:root "$target"; fi
    echo "installed release $version"
fi

cp "$release/systemd/"*.service "$systemd_dir/"
for sample in "$release/config/"*.sample; do
    name=$(basename "$sample" .sample)
    if [ ! -e "$config_dir/$name" ]; then
        cp "$sample" "$config_dir/$name"
        chmod 0600 "$config_dir/$name"
    fi
done

if [ "$real_system" -eq 1 ] && [ "$skip_users" -eq 0 ]; then
    for user in hikablue-engine hikablue-agent hikablue-control; do
        if ! id -u "$user" >/dev/null 2>&1; then
            useradd --system --user-group --home-dir /nonexistent --shell /usr/sbin/nologin "$user"
        fi
    done
    install -d -m 0755 /var/lib/hikablue
    install -d -m 0700 -o hikablue-agent -g hikablue-agent /var/lib/hikablue/agent-state
    install -d -m 0755 /etc/hikablue/pki
fi

old_target=""
if [ -L "$current" ]; then
    old_target=$(readlink "$current")
fi
new_link="$base/.current.$$"
ln -sfn "releases/$version" "$new_link"
mv -T -- "$new_link" "$current"
if [ -n "$old_target" ] && [ "$old_target" != "releases/$version" ]; then
    ln -sfn "$old_target" "$previous"
fi

if [ "$real_system" -eq 1 ] && [ "$no_systemctl" -eq 0 ] && command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload
fi

echo "current release: $version"
echo "services installed but not started: hikablue-engine.service hikablue-agent.service hikablue-control.service"
