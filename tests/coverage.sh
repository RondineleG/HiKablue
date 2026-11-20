#!/usr/bin/env bash
# coverage.sh — measure line coverage for the three components and print a
# summary table. Fails when a tool breaks or a component is below the
# threshold (COVERAGE_MIN, default 90 — see .agc/RULES.md TEST-02).
#
#   rust  — rustc -C instrument-coverage + llvm-profdata/llvm-cov, exported as
#           lcov; lcov-percent.py drops #[cfg(test)] modules and
#           "// coverage: lab-only" spans so they don't count.
#   go    — go test -coverpkg=./... so packages without tests (cmd/vpn-agent)
#           still count as uncovered; go tool cover -func for the total.
#   c#    — coverlet (XPlat Code Coverage collector, cobertura xml); results
#           land in VpnControl.Tests/TestResults/coverage only.
#
# Threshold logic lives in tests/coverage-lib.sh and the report parsers in
# tests/*-percent.py — all exercised by tests/coverage.test.sh.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
. "$ROOT/tests/coverage-lib.sh"
COVERAGE_MIN="${COVERAGE_MIN:-90}"
fail=0

# COVERAGE_GATE=1 (set by checks.sh) is the official TEST-02 gate: all
# three components must be measured at the 90% floor no matter what the
# environment says — otherwise COVERAGE_COMPONENTS=rust or COVERAGE_MIN=50
# would produce a formally-green gate that never verified coverage.
# Selective runs stay available for the collector's self-test and local
# debugging only.
if [ "${COVERAGE_GATE:-0}" = "1" ]; then
  COVERAGE_COMPONENTS="rust go csharp"
  COVERAGE_MIN=90
fi

line() { printf '%-10s %s\n' "$1" "$2"; }

below() { # below NAME PCT — records a TEST-02 threshold violation
  if ! pct_ge_min "$2" "$COVERAGE_MIN"; then
    echo "coverage: $1 ${2}% < ${COVERAGE_MIN}% threshold"
    fail=1
  fi
}

# COVERAGE_COMPONENTS (space list, default all) selects stages — the
# self-test uses it to isolate one failure per scenario. An unknown name
# means a typo silently measured nothing, so fail loud instead of passing.
COMPONENTS=" ${COVERAGE_COMPONENTS:-rust go csharp} "
set -- $COMPONENTS
if [ "$#" -eq 0 ]; then
  echo "coverage: COVERAGE_COMPONENTS selects no component"
  exit 1
fi
KNOWN_COMPONENTS="rust go csharp"
for comp in "$@"; do
  case " $KNOWN_COMPONENTS " in
    *" $comp "*) ;;
    *) echo "coverage: unknown component '$comp' in COVERAGE_COMPONENTS"; exit 1;;
  esac
done
run_for() { case "$COMPONENTS" in *" $1 "*) return 0;; *) return 1;; esac }

echo "== rust (engine-rust) =="
if run_for rust; then
RUST_DIR="$ROOT/engine-rust"
PROFRAW_DIR="$RUST_DIR/target/profraw"
rm -rf "$PROFRAW_DIR" "$RUST_DIR/target/coverage.profdata"
mkdir -p "$PROFRAW_DIR"
# LLVM tools must come from the toolchain that compiled the tests — the
# profile format can differ across Rust versions.
SYSROOT="$(cd "$RUST_DIR" && rustc --print sysroot 2>/dev/null)"
RUST_HOST="$(cd "$RUST_DIR" && rustc -vV 2>/dev/null | awk '/^host:/{print $2}')"
LLVM_BIN="$SYSROOT/lib/rustlib/$RUST_HOST/bin"
LLVM_PROFDATA="${LLVM_PROFDATA:-$LLVM_BIN/llvm-profdata}"
LLVM_COV="${LLVM_COV:-$LLVM_BIN/llvm-cov}"
CARGO_BIN="${CARGO_BIN:-cargo}"
if [ ! -x "$LLVM_PROFDATA" ] || [ ! -x "$LLVM_COV" ]; then
  line rust "FAIL: llvm-tools-preview missing in $SYSROOT (rustup component add llvm-tools-preview)"
  fail=1
elif ! (cd "$RUST_DIR" && RUSTFLAGS="-C instrument-coverage" \
      LLVM_PROFILE_FILE="$PROFRAW_DIR/cov-%p-%m.profraw" \
      "$CARGO_BIN" test --quiet >/dev/null 2>&1); then
  line rust "FAIL: instrumented cargo test failed"
  fail=1
else
  "$LLVM_PROFDATA" merge -sparse "$PROFRAW_DIR"/*.profraw \
    -o "$RUST_DIR/target/coverage.profdata" 2>/dev/null
  objs=()
  for f in "$RUST_DIR"/target/debug/deps/vpn_engine-*; do
    [ -f "$f" ] && [ -x "$f" ] && objs+=(--object "$f")
  done
  lcov="$("$LLVM_COV" export "${objs[@]}" \
    --instr-profile="$RUST_DIR/target/coverage.profdata" \
    --sources "$RUST_DIR/src" --format=lcov 2>/dev/null)"
  if [ -z "$lcov" ]; then
    line rust "FAIL: llvm-cov produced no report"
    fail=1
  elif ! rust_pct=$(printf '%s' "$lcov" | python3 "$ROOT/tests/lcov-percent.py" 2>/dev/null); then
    line rust "FAIL: lcov report could not be parsed"
    fail=1
  else
    line rust "lines (excl. test code + lab-only spans): ${rust_pct}%"
    below rust "$rust_pct"
  fi
fi
fi

echo "== go (agent-go) =="
if run_for go; then
GO_BIN="${GO_BIN:-go}"
if ! (cd "$ROOT/agent-go" && "$GO_BIN" test -coverpkg=./... \
      -coverprofile=coverage.out ./... >/dev/null 2>&1); then
  line go "FAIL: go test -coverprofile failed"
  fail=1
else
  go_out=$(cd "$ROOT/agent-go" && "$GO_BIN" tool cover -func=coverage.out 2>/dev/null | tail -1)
  go_pct=$(echo "$go_out" | grep -oP '[0-9]+\.[0-9]+(?=%)' || true)
  if [ -z "$go_pct" ]; then
    line go "FAIL: go tool cover produced no total"
    fail=1
  else
    line go "statements: ${go_pct}%"
    below go "$go_pct"
  fi
fi
fi

echo "== c# (control-dotnet) =="
if run_for csharp; then
CD_DIR="$ROOT/control-dotnet"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
COV_DIR="$CD_DIR/VpnControl.Tests/TestResults/coverage"
rm -rf "$COV_DIR"   # only our own results dir — never the whole TestResults tree
if ! command -v python3 >/dev/null; then
  line csharp "FAIL: python3 required to parse cobertura xml"
  fail=1
elif ! (cd "$CD_DIR" && "$DOTNET_BIN" test VpnControl.Tests/VpnControl.Tests.csproj \
      --collect:"XPlat Code Coverage" --settings coverlet.runsettings \
      --results-directory "$COV_DIR" -v q >/dev/null 2>&1); then
  line csharp "FAIL: dotnet test --collect coverage failed"
  fail=1
else
  covxml=$(find "$COV_DIR" -name "coverage.cobertura.xml" | head -1)
  if [ -z "$covxml" ]; then
    line csharp "FAIL: no cobertura xml"
    fail=1
  elif ! cs_pct=$(python3 "$ROOT/tests/cobertura-percent.py" "$covxml" 2>/dev/null); then
    line csharp "FAIL: cobertura xml could not be parsed"
    fail=1
  else
    cs_line=${cs_pct%% *}
    cs_branch=${cs_pct##* }
    line csharp "lines: ${cs_line}% branches: ${cs_branch}%"
    below csharp "$cs_line"
  fi
fi
fi

[ "$fail" -eq 0 ] && echo "coverage: measured (all >= ${COVERAGE_MIN}%)" || { echo "coverage: FAIL"; exit 1; }
