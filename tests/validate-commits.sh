#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
validator="$repo_root/.github/scripts/validate-commits.sh"
mock_dir="$(mktemp -d)"
trap 'rm -rf "$mock_dir"' EXIT

cat > "$mock_dir/git" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail

case "$1" in
  cat-file)
    exit 0
    ;;
  rev-list)
    if [[ -n "${MOCK_COMMITS:-}" ]]; then
      printf '%s\n' "${MOCK_COMMITS//,/$'\n'}"
    fi
    ;;
  show)
    commit="${@: -1}"
    case "$commit" in
      11111111) printf '%s\n' "${MOCK_SUBJECT_1:-}" ;;
      22222222) printf '%s\n' "${MOCK_SUBJECT_2:-}" ;;
      *) exit 2 ;;
    esac
    ;;
  *)
    exit 2
    ;;
esac
MOCK
chmod +x "$mock_dir/git"

run_validator() {
  local branch="$1"
  local commits="$2"
  local subject1="$3"
  local subject2="${4:-}"

  PATH="$mock_dir:$PATH" \
    MOCK_COMMITS="$commits" \
    MOCK_SUBJECT_1="$subject1" \
    MOCK_SUBJECT_2="$subject2" \
    BASE_SHA=base HEAD_SHA=head BRANCH_NAME="$branch" \
    bash "$validator"
}

expect_pass() {
  if ! run_validator "$@" >/dev/null; then
    echo "Expected validation to pass: $*" >&2
    exit 1
  fi
}

expect_fail() {
  if run_validator "$@" >/dev/null 2>&1; then
    echo "Expected validation to fail: $*" >&2
    exit 1
  fi
}

expect_pass feat/commit-validation 11111111,22222222 \
  'docs(README): clarify setup' 'feat(api/v2)!: update response contract'
expect_pass fix/packet-boundary 11111111 'fix(net): reject oversized packets'
expect_fail feat/commit-validation 11111111 'invalid subject'
expect_fail feature/commit-validation 11111111 'feat: valid subject'
expect_fail feat/commit-validation 11111111,22222222 \
  'feat: valid subject' 'invalid subject'

echo "Commit validator tests passed."
