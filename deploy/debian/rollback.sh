#!/usr/bin/env bash
# rollback.sh — switch /opt/hikablue/current back to the release recorded
# as previous by install.sh. Services are not restarted automatically.
set -euo pipefail

root="/"
no_systemctl=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --root)
            [ "$#" -ge 2 ] || { echo "--root requires a directory" >&2; exit 2; }
            root=$2
            shift
            ;;
        --no-systemctl) no_systemctl=1 ;;
        *) echo "usage: $0 [--root DIR] [--no-systemctl]" >&2; exit 2 ;;
    esac
    shift
done

if [ "$root" = "/" ]; then
    root=""
    real_system=1
else
    real_system=0
fi
if [ "$real_system" -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
    echo "run as root for a real rollback, or use --root for staged/offline validation" >&2
    exit 1
fi

base="$root/opt/hikablue"
current="$base/current"
previous="$base/previous"
[ -L "$current" ] || { echo "no current release symlink at $current" >&2; exit 1; }
[ -L "$previous" ] || { echo "no previous release symlink at $previous" >&2; exit 1; }
current_target=$(readlink "$current")
previous_target=$(readlink "$previous")
[ -d "$base/$previous_target" ] || { echo "previous release is missing: $base/$previous_target" >&2; exit 1; }

new_link="$base/.rollback.$$"
ln -sfn "$previous_target" "$new_link"
mv -T -- "$new_link" "$current"
ln -sfn "$current_target" "$previous"

if [ "$real_system" -eq 1 ] && [ "$no_systemctl" -eq 0 ] && command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload
fi

echo "rolled back current -> $previous_target"
echo "restart services explicitly after checking configuration"
