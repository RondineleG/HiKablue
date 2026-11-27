#!/usr/bin/env bash
set -euo pipefail

root="/"
role=""
source_dir=""
rollback=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --root)
            [ "$#" -ge 2 ] || { echo "--root requires a directory" >&2; exit 2; }
            root=$2
            shift
            ;;
        --role)
            [ "$#" -ge 2 ] || { echo "--role requires control or node" >&2; exit 2; }
            role=$2
            shift
            ;;
        --source-dir)
            [ "$#" -ge 2 ] || { echo "--source-dir requires a directory" >&2; exit 2; }
            source_dir=$2
            shift
            ;;
        --rollback) rollback=1 ;;
        *) echo "usage: $0 --role control|node [--source-dir DIR|--rollback] [--root DIR]" >&2; exit 2 ;;
    esac
    shift
done

case "$role" in
    control) credential_names=(connection-string heartbeat-token admin-token session-token-secret server.crt server.key lab-ca.crt) ;;
    node) credential_names=(lab-ca.crt node.crt node.key) ;;
    *) echo "--role must be control or node" >&2; exit 2 ;;
esac
if [ "$rollback" -eq 1 ]; then
    [ -z "$source_dir" ] || { echo "--rollback cannot be combined with --source-dir" >&2; exit 2; }
else
    [ -n "$source_dir" ] || { echo "--source-dir is required unless --rollback is used" >&2; exit 2; }
fi

if [ "$root" = "/" ]; then
    credential_root="/etc/hikablue/credentials"
    if [ "$(id -u)" -ne 0 ]; then
        echo "run as root to provision system credentials, or use --root for isolated validation" >&2
        exit 1
    fi
else
    root=$(cd -- "$root" && pwd)
    credential_root="$root/etc/hikablue/credentials"
fi
role_root="$credential_root/$role"
versions="$role_root/versions"
current="$role_root/current"
previous="$role_root/previous"
install -d -m 0700 "$role_root" "$versions"
chmod 0700 "$role_root" "$versions"

validate_version_link() {
    local link=$1 target
    [ -L "$link" ] || { echo "credential version link is missing: $link" >&2; exit 1; }
    target=$(readlink "$link")
    [[ "$target" =~ ^versions/[A-Za-z0-9._-]+$ ]] &&
        [ -d "$role_root/$target" ] && [ ! -L "$role_root/$target" ] || {
        echo "credential version link is invalid: $link" >&2
        exit 1
    }
    printf '%s\n' "$target"
}

replace_link() {
    local link=$1 target=$2 temporary="$role_root/.$(basename -- "$link").$$"
    ln -s -- "$target" "$temporary"
    mv -Tf -- "$temporary" "$link"
}

if [ "$rollback" -eq 1 ]; then
    current_target=$(validate_version_link "$current")
    previous_target=$(validate_version_link "$previous")
    replace_link "$current" "$previous_target"
    replace_link "$previous" "$current_target"
    echo "credential version rolled back for role: $role"
    echo "restart the corresponding systemd service to load it"
    exit 0
fi

[[ -d "$source_dir" && ! -L "$source_dir" ]] || {
    echo "source directory must be an existing, non-symlink directory" >&2
    exit 1
}
source_dir=$(cd -- "$source_dir" && pwd)
mode=$(stat -c '%a' "$source_dir")
if (( (8#$mode & 077) != 0 )); then
    echo "source directory must not be accessible by group or other" >&2
    exit 1
fi
for name in "${credential_names[@]}"; do
    source="$source_dir/$name"
    [[ -f "$source" && ! -L "$source" ]] || {
        echo "required source credential is missing or not a regular file: $name" >&2
        exit 1
    }
    mode=$(stat -c '%a' "$source")
    if (( (8#$mode & 077) != 0 )); then
        echo "source credential must not be accessible by group or other: $name" >&2
        exit 1
    fi
    size=$(wc -c <"$source")
    if [ "$size" -eq 0 ] || [ "$size" -gt 1048576 ]; then
        echo "source credential is empty or too large: $name" >&2
        exit 1
    fi
done

if [ "$role" = control ]; then
    token_size=$(tr -d '\r\n' <"$source_dir/session-token-secret" | wc -c)
    if [ "$token_size" -lt 32 ]; then
        echo "session-token-secret must contain at least 32 bytes" >&2
        exit 1
    fi
fi
command -v systemd-creds >/dev/null || {
    echo "systemd-creds is required (Debian 12/systemd 252 or newer)" >&2
    exit 1
}

old_target=""
if [ -L "$current" ]; then
    old_target=$(validate_version_link "$current")
elif [ -e "$current" ]; then
    echo "refusing to replace non-symlink credential path: $current" >&2
    exit 1
fi

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
staging=$(mktemp -d "$versions/.stage-$timestamp.XXXXXX")
cleanup() {
    if [ -n "${staging:-}" ] && [ -d "$staging" ]; then
        rm -rf -- "$staging"
    fi
}
trap cleanup EXIT
chmod 0700 "$staging"
for name in "${credential_names[@]}"; do
    systemd-creds --name="$name" --with-key=host encrypt \
        "$source_dir/$name" "$staging/$name"
    chmod 0600 "$staging/$name"
done

version=${staging##*/.stage-}
final="$versions/$version"
mv -T -- "$staging" "$final"
staging=""

if [ -n "$old_target" ]; then
    replace_link "$previous" "$old_target"
fi
replace_link "$current" "versions/$version"
echo "encrypted credentials prepared for role: $role"
echo "restart the corresponding systemd service to load the new version"
