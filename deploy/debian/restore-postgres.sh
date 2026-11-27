#!/usr/bin/env bash
set -euo pipefail

database=""
backup_dir=""
confirmation=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --database)
            [ "$#" -ge 2 ] || { echo "--database requires a name" >&2; exit 2; }
            database=$2
            shift
            ;;
        --backup-dir)
            [ "$#" -ge 2 ] || { echo "--backup-dir requires a directory" >&2; exit 2; }
            backup_dir=$2
            shift
            ;;
        --confirm-database)
            [ "$#" -ge 2 ] || { echo "--confirm-database requires the exact target name" >&2; exit 2; }
            confirmation=$2
            shift
            ;;
        *) echo "usage: $0 --database NAME --backup-dir DIR --confirm-database NAME" >&2; exit 2 ;;
    esac
    shift
done

[[ "$database" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,62}$ ]] || {
    echo "--database must be a PostgreSQL identifier of at most 63 safe characters" >&2
    exit 2
}
[ "$confirmation" = "$database" ] || {
    echo "restore requires --confirm-database to exactly match the target database" >&2
    exit 2
}
[ -n "$backup_dir" ] || { echo "--backup-dir is required" >&2; exit 2; }
[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || {
    echo "backup directory must be an existing, non-symlink directory" >&2
    exit 1
}
backup_dir=$(cd -- "$backup_dir" && pwd)
archive="$backup_dir/backup.dump"
metadata="$backup_dir/METADATA"
checksum_manifest="$backup_dir/SHA256SUMS"
[[ -f "$archive" && ! -L "$archive" ]] || {
    echo "backup archive is missing or is not a regular file" >&2
    exit 1
}
[[ -f "$metadata" && ! -L "$metadata" &&
    -f "$checksum_manifest" && ! -L "$checksum_manifest" ]] || {
    echo "backup metadata or checksum manifest is missing" >&2
    exit 1
}
grep -Fqx "database=$database" "$metadata" || {
    echo "backup database does not match the confirmed target" >&2
    exit 1
}
command -v sha256sum >/dev/null || { echo "sha256sum is required" >&2; exit 1; }
command -v pg_restore >/dev/null || { echo "pg_restore is required" >&2; exit 1; }

mode=$(stat -c '%a' "$backup_dir")
if (( (8#$mode & 077) != 0 )); then
    echo "backup directory must not be accessible by group or other" >&2
    exit 1
fi
mode=$(stat -c '%a' "$archive")
if (( (8#$mode & 077) != 0 )); then
    echo "backup archive must not be accessible by group or other" >&2
    exit 1
fi
for protected_file in "$metadata" "$checksum_manifest"; do
    mode=$(stat -c '%a' "$protected_file")
    if (( (8#$mode & 077) != 0 )); then
        echo "backup metadata and checksum manifest must not be accessible by group or other" >&2
        exit 1
    fi
done

checksum_line=$(cat "$checksum_manifest")
[[ "$checksum_line" =~ ^[0-9a-f]{64}[[:space:]][[:space:]]backup\.dump$ ]] || {
    echo "checksum manifest must contain exactly one backup.dump entry" >&2
    exit 1
}

(
    cd -- "$backup_dir"
    sha256sum --check --status "$checksum_manifest"
) || { echo "backup checksum verification failed" >&2; exit 1; }
pg_restore --no-password --list "$archive" >/dev/null

echo "restoring into database '$database'; stop hikablue-control.service first"
pg_restore --no-password --exit-on-error --single-transaction \
    --clean --if-exists --dbname="$database" "$archive"
echo "database restore completed: $database"
