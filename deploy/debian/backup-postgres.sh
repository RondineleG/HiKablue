#!/usr/bin/env bash
set -euo pipefail

database=""
output_dir=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --database)
            [ "$#" -ge 2 ] || { echo "--database requires a name" >&2; exit 2; }
            database=$2
            shift
            ;;
        --output-dir)
            [ "$#" -ge 2 ] || { echo "--output-dir requires a directory" >&2; exit 2; }
            output_dir=$2
            shift
            ;;
        *) echo "usage: $0 --database NAME --output-dir NEW_DIRECTORY" >&2; exit 2 ;;
    esac
    shift
done

[[ "$database" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,62}$ ]] || {
    echo "--database must be a PostgreSQL identifier of at most 63 safe characters" >&2
    exit 2
}
[ -n "$output_dir" ] || { echo "--output-dir is required" >&2; exit 2; }
command -v pg_dump >/dev/null || { echo "pg_dump is required" >&2; exit 1; }
command -v sha256sum >/dev/null || { echo "sha256sum is required" >&2; exit 1; }

parent=$(cd -- "$(dirname -- "$output_dir")" && pwd)
name=$(basename -- "$output_dir")
case "$name" in ""|.|..) echo "invalid output directory name" >&2; exit 2 ;; esac
output_dir="$parent/$name"
[ ! -e "$output_dir" ] && [ ! -L "$output_dir" ] || {
    echo "refusing to overwrite existing backup: $output_dir" >&2
    exit 1
}

umask 077
work=$(mktemp -d "$parent/.${name}.XXXXXX")
cleanup() {
    if [ -n "${work:-}" ] && [ -d "$work" ]; then
        rm -rf -- "$work"
    fi
}
trap cleanup EXIT
chmod 0700 "$work"

pg_dump --format=custom --no-password \
    --dbname="$database" --file="$work/backup.dump"
chmod 0600 "$work/backup.dump"
(
    cd -- "$work"
    sha256sum backup.dump >SHA256SUMS
)
{
    printf 'database=%s\n' "$database"
    printf 'created_utc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'pg_dump_version=%s\n' "$(pg_dump --version)"
} >"$work/METADATA"
chmod 0600 "$work/SHA256SUMS" "$work/METADATA"

mv -T -- "$work" "$output_dir"
work=""
echo "backup created: $output_dir"
