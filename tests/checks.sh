#!/usr/bin/env bash
# tests/checks.sh — local quality gate for HiKablue.
#
# Runs every non-privileged check the CI guardrails enforce, in one command,
# so "validate locally before proceeding" has a single entry point. With root
# it also runs the netns integration test (the real TUN↔UDP ping).
#
# Usage:
#   tests/checks.sh              run everything (lab ping needs root)
#   tests/checks.sh --skip-lab   skip the privileged netns test
#   tests/checks.sh --pre-push   intended for a git pre-push hook
#
# Environment overrides:
#   BASE_SHA    base commit for the commit validator (default: merge-base of
#               HEAD and origin/main)
#   HEAD_SHA    head commit (default: HEAD)
#   BRANCH_NAME branch name to validate (default: current branch)
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

skip_lab=0
for arg in "$@"; do
    case "$arg" in
        --skip-lab) skip_lab=1 ;;
        --pre-push) ;; # same checks; the hook just calls this script
        *) echo "tests/checks.sh: unknown argument $arg" >&2; exit 2 ;;
    esac
done

step() { printf '\n== %s\n' "$*"; }

step "commit subjects + branch name"
BASE_SHA="${BASE_SHA:-$(git merge-base HEAD origin/main)}"
HEAD_SHA="${HEAD_SHA:-$(git rev-parse HEAD)}"
BRANCH_NAME="${BRANCH_NAME:-$(git branch --show-current)}"
export BASE_SHA HEAD_SHA BRANCH_NAME
bash tests/validate-commits.sh
bash .github/scripts/validate-commits.sh

step "shell syntax + shellcheck"
mapfile -t scripts < <(git ls-files '*.sh')
for f in "${scripts[@]}"; do
    case "$(head -1 "$f")" in
        *bash*) bash -n "$f" ;;
        *) sh -n "$f" ;;
    esac
done
if command -v shellcheck >/dev/null 2>&1; then
    shellcheck -S warning "${scripts[@]}"
else
    echo "shellcheck not installed — syntax check only" >&2
fi

step "rust: fmt, clippy, test, release build"
(
    cd engine-rust
    cargo fmt --all -- --check
    cargo clippy --all-targets -- -D warnings
    cargo test
    cargo build --release
)

step "book parity (en / es / pt-BR)"
bash tests/book/check-parity.sh

if [ "$skip_lab" -eq 0 ]; then
    if [ "$(id -u)" -eq 0 ]; then
        step "lab integration: ip netns ping"
        tests/lab/netns-ping.sh engine-rust/target/release/vpn-engine
    else
        step "lab integration skipped (needs root; run: sudo tests/checks.sh)"
    fi
fi

printf '\nchecks.sh: ALL PASSED\n'
