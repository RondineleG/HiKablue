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
source_database=$(sed -n 's/^database=//p' "$metadata")
[[ "$source_database" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,62}$ ]] || {
    echo "backup metadata must contain exactly one safe source database name" >&2
    exit 1
}
command -v sha256sum >/dev/null || { echo "sha256sum is required" >&2; exit 1; }
command -v pg_restore >/dev/null || { echo "pg_restore is required" >&2; exit 1; }
command -v psql >/dev/null || { echo "psql is required" >&2; exit 1; }

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

if ! target_has_objects=$(psql --no-password --set=ON_ERROR_STOP=1 \
    --tuples-only --no-align --dbname="$database" --command="
SELECT CASE WHEN
    EXISTS (
        SELECT 1
        FROM pg_catalog.pg_namespace AS n
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'public')
          AND n.nspname !~ '^pg_'
    )
    OR EXISTS (
        SELECT 1
        FROM pg_catalog.pg_depend AS d
        WHERE d.refclassid = 'pg_catalog.pg_namespace'::regclass
          AND d.classid <> 'pg_catalog.pg_namespace'::regclass
          AND d.refobjid = (
              SELECT n.oid
              FROM pg_catalog.pg_namespace AS n
              WHERE n.nspname = 'public'
          )
    )
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_largeobject_metadata)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_publication)
    OR EXISTS (
        SELECT 1
        FROM pg_catalog.pg_subscription AS s
        WHERE s.subdbid = (
            SELECT d.oid
            FROM pg_catalog.pg_database AS d
            WHERE d.datname = pg_catalog.current_database()
        )
    )
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_foreign_data_wrapper)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_foreign_server)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_user_mapping)
    OR EXISTS (
        SELECT 1
        FROM pg_catalog.pg_language AS l
        WHERE l.lanname NOT IN ('internal', 'c', 'sql', 'plpgsql')
    )
    OR EXISTS (
        SELECT 1
        FROM pg_catalog.pg_db_role_setting AS s
        WHERE s.setdatabase = (
            SELECT d.oid
            FROM pg_catalog.pg_database AS d
            WHERE d.datname = pg_catalog.current_database()
        )
    )
THEN 1 ELSE 0 END
"); then
    echo "could not verify that the target database is empty" >&2
    exit 1
fi
[[ "$target_has_objects" == "0" ]] || {
    echo "target database must be empty; restore into a fresh database to avoid retaining objects absent from the archive" >&2
    exit 1
}

echo "restoring backup from '$source_database' into empty database '$database'; stop hikablue-control.service first"
pg_restore --no-password --exit-on-error --single-transaction \
    --dbname="$database" "$archive"
echo "database restore completed: $database"
