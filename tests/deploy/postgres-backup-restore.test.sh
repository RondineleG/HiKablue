#!/usr/bin/env bash
# Offline backup/restore safety test. Stub clients prevent any database access.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp=$(mktemp -d /tmp/hk-m5-postgres-test.XXXXXX)
trap 'rm -rf -- "$tmp"' EXIT

for helper in provision-credentials.sh backup-postgres.sh restore-postgres.sh; do
    [[ -x "$repo_root/deploy/debian/$helper" ]] || {
        echo "FAIL: deployment helper is not executable: $helper" >&2
        exit 1
    }
done

mkdir -p "$tmp/bin" "$tmp/backups"
cat >"$tmp/bin/pg_dump" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [ "${1:-}" = "--version" ]; then
    echo "pg_dump stub 1"
    exit 0
fi
printf '%s\n' "$*" >>"$PG_DUMP_ARGS"
file=""
for arg in "$@"; do case "$arg" in --file=*) file=${arg#--file=} ;; esac; done
[ -n "$file" ] || exit 2
[ "${FAIL_DUMP:-}" != 1 ] || exit 1
printf 'isolated test archive\n' >"$file"
STUB
cat >"$tmp/bin/pg_restore" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [[ " $* " == *" --list "* ]]; then
    printf '%s\n' "$*" >>"$PG_RESTORE_LIST_ARGS"
    [ -f "${*: -1}" ]
    exit
fi
printf '%s\n' "$*" >>"$PG_RESTORE_ARGS"
[ "${FAIL_RESTORE:-}" != 1 ] || exit 1
STUB
cat >"$tmp/bin/psql" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >>"$PSQL_ARGS"
printf '%s\n' "${TARGET_HAS_OBJECTS:-0}"
STUB
chmod 0755 "$tmp/bin/pg_dump" "$tmp/bin/pg_restore" "$tmp/bin/psql"
export PATH="$tmp/bin:$PATH"
export PG_DUMP_ARGS="$tmp/pg-dump-args"
export PG_RESTORE_ARGS="$tmp/pg-restore-args"
export PG_RESTORE_LIST_ARGS="$tmp/pg-restore-list-args"
export PSQL_ARGS="$tmp/psql-args"
export PGPASSWORD="not-a-command-line-argument"

bash "$repo_root/deploy/debian/backup-postgres.sh" \
    --database isolated_vpn --output-dir "$tmp/backups/before-upgrade" >/dev/null
backup="$tmp/backups/before-upgrade"
[ "$(stat -c %a "$backup")" = 700 ] || { echo "FAIL: backup directory is not private" >&2; exit 1; }
[ "$(stat -c %a "$backup/backup.dump")" = 600 ] || { echo "FAIL: backup archive is not private" >&2; exit 1; }
[ "$(stat -c %a "$backup/METADATA")" = 600 ] || { echo "FAIL: backup metadata is not private" >&2; exit 1; }
[ "$(stat -c %a "$backup/SHA256SUMS")" = 600 ] || { echo "FAIL: checksum manifest is not private" >&2; exit 1; }
(cd "$backup" && sha256sum --check --status SHA256SUMS) || {
    echo "FAIL: generated checksum is invalid" >&2
    exit 1
}
grep -q -- '--no-password' "$PG_DUMP_ARGS" || {
    echo "FAIL: pg_dump could prompt for a password" >&2
    exit 1
}
if grep -Fq "$PGPASSWORD" "$PG_DUMP_ARGS"; then
    echo "FAIL: password was passed on the pg_dump command line" >&2
    exit 1
fi

if bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database isolated_vpn --backup-dir "$backup" --confirm-database wrong \
    >/dev/null 2>&1; then
    echo "FAIL: mismatched restore confirmation was accepted" >&2
    exit 1
fi
[ ! -e "$PG_RESTORE_ARGS" ] && [ ! -e "$PG_RESTORE_LIST_ARGS" ] || {
    echo "FAIL: pg_restore ran before explicit confirmation" >&2
    exit 1
}

bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database restored_vpn --backup-dir "$backup" \
    --confirm-database restored_vpn >/dev/null
grep -q -- '--single-transaction' "$PG_RESTORE_ARGS" || {
    echo "FAIL: restore is not transactional" >&2
    exit 1
}
if grep -q -- '--clean' "$PG_RESTORE_ARGS"; then
    echo "FAIL: restore must not merge into a non-empty target with --clean" >&2
    exit 1
fi
grep -q -- '--dbname=restored_vpn' "$PG_RESTORE_ARGS" || {
    echo "FAIL: restore did not use the explicitly confirmed target database" >&2
    exit 1
}
grep -q -- '--dbname=restored_vpn' "$PSQL_ARGS" || {
    echo "FAIL: target emptiness was not checked" >&2
    exit 1
}
grep -Fq 'THEN 1 ELSE 0 END' "$PSQL_ARGS" || {
    echo "FAIL: target emptiness query has an incomplete CASE expression" >&2
    exit 1
}
[ -s "$PG_RESTORE_LIST_ARGS" ] || {
    echo "FAIL: pg_restore did not validate the archive" >&2
    exit 1
}

: >"$PG_RESTORE_ARGS"
if TARGET_HAS_OBJECTS=1 bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database restored_vpn --backup-dir "$backup" \
    --confirm-database restored_vpn >/dev/null 2>&1; then
    echo "FAIL: restore accepted a non-empty target database" >&2
    exit 1
fi
[ ! -s "$PG_RESTORE_ARGS" ] || {
    echo "FAIL: pg_restore ran against a non-empty target" >&2
    exit 1
}

cp -a "$backup" "$tmp/backups/corrupt"
printf 'tampered\n' >>"$tmp/backups/corrupt/backup.dump"
if bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database restored_vpn --backup-dir "$tmp/backups/corrupt" \
    --confirm-database restored_vpn >/dev/null 2>&1; then
    echo "FAIL: corrupted backup was restored" >&2
    exit 1
fi

cp -a "$backup" "$tmp/backups/manifest-tampered"
printf '%s\n' "$(cat "$tmp/backups/manifest-tampered/SHA256SUMS")" \
    >>"$tmp/backups/manifest-tampered/SHA256SUMS"
if bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database restored_vpn --backup-dir "$tmp/backups/manifest-tampered" \
    --confirm-database restored_vpn >/dev/null 2>&1; then
    echo "FAIL: multi-entry checksum manifest was accepted" >&2
    exit 1
fi

cp -a "$backup" "$tmp/backups/manifest-symlink"
rm "$tmp/backups/manifest-symlink/SHA256SUMS"
ln -s METADATA "$tmp/backups/manifest-symlink/SHA256SUMS"
if bash "$repo_root/deploy/debian/restore-postgres.sh" \
    --database restored_vpn --backup-dir "$tmp/backups/manifest-symlink" \
    --confirm-database restored_vpn >/dev/null 2>&1; then
    echo "FAIL: symlinked checksum manifest was accepted" >&2
    exit 1
fi

if bash "$repo_root/deploy/debian/backup-postgres.sh" \
    --database 'isolated;drop' --output-dir "$tmp/backups/unsafe" >/dev/null 2>&1; then
    echo "FAIL: unsafe database name accepted" >&2
    exit 1
fi

echo "PASS: PostgreSQL backup is private/checksummed; restore is guarded and transactional"
