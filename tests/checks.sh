#!/usr/bin/env bash
# tests/checks.sh — local quality gate for HiKablue.
#
# Runs every non-privileged check the CI guardrails enforce, in one command,
# so "validate locally before proceeding" has a single entry point. Each step
# is reported as PASS, FAIL or SKIPPED and a failure never masks the others —
# the final verdict reflects the whole run:
#
#   checks.sh: ALL PASSED             every check ran and passed (exit 0)
#   checks.sh: PASSED (partial)       all executed checks passed but some were
#                                     skipped — NOT a full validation (exit 0)
#   checks.sh: FAILED                 at least one check failed (exit 1)
#
# The privileged netns lab is never run by this script itself: requiring root
# for the whole pipeline just to ping would mix privileges. Use --lab to have
# this script invoke `sudo tests/lab/netns-ping.sh` for that step only, or run
# that command separately in the authorized lab.
#
# Usage:
#   tests/checks.sh              unprivileged gate (lab step reported SKIPPED)
#   tests/checks.sh --lab        also run the netns ping via sudo (last step)
#   tests/checks.sh --skip-lab   explicit form of the default; lab = SKIPPED
#   tests/checks.sh --pre-push   alias for the default run (git hook entry)
#
# Environment overrides:
#   BASE_SHA    base commit for diff-scoped checks (default: merge-base of
#               HEAD and origin/main)
#   HEAD_SHA    head commit (default: HEAD)
#   BRANCH_NAME branch name to validate (default: current branch)
set -uo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root" || exit 1

run_lab=0
for arg in "$@"; do
    case "$arg" in
        --lab) run_lab=1 ;;
        --skip-lab|--pre-push) ;;
        *) echo "tests/checks.sh: unknown argument $arg" >&2; exit 2 ;;
    esac
done

pass=0
failed=()
skipped=()

record_pass() { pass=$((pass + 1)); echo "PASS     $1"; }
record_fail() { failed+=("$1"); echo "FAIL     $1"; }
record_skip() { skipped+=("$1"); echo "SKIPPED  $1 — $2"; }

# run_step NAME COMMAND... — a step that must pass for the gate to pass.
run_step() {
    local name="$1"
    shift
    echo
    echo "== $name"
    if "$@"; then
        record_pass "$name"
    else
        record_fail "$name"
    fi
}

BASE_SHA="${BASE_SHA:-$(git merge-base HEAD origin/main)}"
HEAD_SHA="${HEAD_SHA:-$(git rev-parse HEAD)}"
BRANCH_NAME="${BRANCH_NAME:-$(git branch --show-current)}"
export BASE_SHA HEAD_SHA BRANCH_NAME

run_step "commit subjects + branch name" bash tests/validate-commits.sh
run_step "commit validator" bash .github/scripts/validate-commits.sh
run_step "diff safety (forbidden patterns)" bash tests/lab/check-diff-safety.sh "$BASE_SHA" "$HEAD_SHA"
run_step "diff safety self-tests" bash tests/lab/check-diff-safety.test.sh

step_shell() {
    mapfile -t scripts < <(git ls-files '*.sh')
    for f in "${scripts[@]}"; do
        case "$(head -1 "$f")" in
            *bash*) bash -n "$f" ;;
            *) sh -n "$f" ;;
        esac
    done
    # Lint is a required check: when shellcheck is absent the check never ran,
    # which must not count as an approval.
    command -v shellcheck >/dev/null 2>&1 || {
        echo "shellcheck not installed — lint cannot run" >&2
        return 1
    }
    shellcheck -S warning "${scripts[@]}"
}
run_step "shell syntax + shellcheck" step_shell

step_rust() {
    (
        cd engine-rust || exit 1
        cargo fmt --all -- --check &&
            cargo clippy --all-targets -- -D warnings &&
            cargo test &&
            cargo build --release
    )
}
run_step "rust: fmt, clippy, test, release build" step_rust

run_step "book parity (en / es / pt-BR)" bash tests/book/check-parity.sh

if [ "$run_lab" -eq 1 ]; then
    run_step "lab integration: ip netns ping (sudo)" \
        sudo tests/lab/netns-ping.sh engine-rust/target/release/vpn-engine
else
    record_skip "lab integration: ip netns ping" \
        "privileged — run 'tests/checks.sh --lab' or 'sudo tests/lab/netns-ping.sh engine-rust/target/release/vpn-engine' in the authorized lab"
fi

echo
echo "== summary"
echo "passed: $pass  failed: ${#failed[@]}  skipped: ${#skipped[@]}"
for f in "${failed[@]}"; do echo "  FAILED: $f"; done
for s in "${skipped[@]}"; do echo "  SKIPPED: $s"; done

if [ "${#failed[@]}" -gt 0 ]; then
    echo "checks.sh: FAILED"
    exit 1
fi
if [ "${#skipped[@]}" -gt 0 ]; then
    echo "checks.sh: PASSED (partial — ${#skipped[@]} check(s) skipped; not a full validation)"
    exit 0
fi
echo "checks.sh: ALL PASSED"
