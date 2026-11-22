#!/usr/bin/env bash
# coverage.test.sh — regression tests for the coverage collector itself
# (TEST-01): threshold math, report parsers, and failure modes. Fixtures
# and stubs only — the real measurement stays in coverage.sh.
set -u
# The gate and component overrides must not leak in from the caller (e.g.
# COVERAGE_GATE=1 bash tests/checks.sh exports them to us): every scenario
# below sets the variables it needs explicitly, so clear them first.
unset COVERAGE_GATE COVERAGE_COMPONENTS COVERAGE_MIN
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
. "$ROOT/tests/coverage-lib.sh"
FIX="$ROOT/tests/fixtures/coverage"
fails=0
t() { # t NAME EXPECT(0|1) CMD...
  local name="$1" want="$2"; shift 2
  "$@" >/dev/null 2>&1
  local got=$?
  if [ "$want" -eq 0 ]; then
    [ "$got" -eq 0 ] && { echo "  ok   $name"; } || { echo "  FAIL $name (expected pass)"; fails=1; }
  else
    [ "$got" -ne 0 ] && { echo "  ok   $name"; } || { echo "  FAIL $name (expected fail)"; fails=1; }
  fi
}

echo "-- threshold (pct_ge_min, raw float compare) --"
t "95% passes 90 bar"   0 pct_ge_min 95 90
t "90% passes 90 bar"   0 pct_ge_min 90 90
t "89.99% fails 90"     1 pct_ge_min 89.99 90
t "0% fails"            1 pct_ge_min 0 90
t "empty pct fails"     1 pct_ge_min "" 90
t "non-numeric fails"   1 pct_ge_min "abc" 90
t "trailing junk fails" 1 pct_ge_min "95abc" 90
t "NaN fails"           1 pct_ge_min "NaN" 90
t "Infinity fails"      1 pct_ge_min "Infinity" 90
t "negative fails"      1 pct_ge_min "-1" 90
t "over 100 fails"      1 pct_ge_min "101" 90
t "bad min fails"       1 pct_ge_min 95 "abc"
t "min over 100 fails"  1 pct_ge_min 95 101

echo "-- lcov-percent.py --"
# fake_engine.rs: DA 1-4 counted (2 covered → 50.00); DA:6 sits in a
# lab-only span and DA:9 inside #[cfg(test)] — both must be dropped.
LCOV=$(printf 'TN:\nSF:%s/fake_engine.rs\nDA:1,1\nDA:2,1\nDA:3,0\nDA:4,0\nDA:6,1\nDA:9,1\nend_of_record\n' "$FIX")
t "valid lcov → 50.00" 0 test "$(printf '%s' "$LCOV" | python3 "$ROOT/tests/lcov-percent.py")" = "50.00"
t "empty report fails"   1 bash -c "printf '' | python3 $ROOT/tests/lcov-percent.py"
t "garbage fails"        1 bash -c "printf 'not lcov at all\n' | python3 $ROOT/tests/lcov-percent.py"

echo "-- cobertura-percent.py --"
t "valid xml parses"     0 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/sample-cobertura.xml"
t "missing file fails"   1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/nope.xml"
t "invalid xml fails"    1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/not-xml.xml"
t "non-cobertura fails"  1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/wrong-root.xml"

echo "-- coverage.sh failure modes (one injected fault per scenario) --"
# Stub scenarios run coverage.sh in a throwaway tree: it rm -rf's its own
# artifact dirs before measuring, so pointing it at the real checkout would
# delete the official run's reports. $STUB mirrors just the paths it needs.
STUB="$(mktemp -d)"
trap 'rm -rf "$STUB"' EXIT
mkdir -p "$STUB/engine-rust" "$STUB/agent-go" "$STUB/control-dotnet"
cp -R "$ROOT/tests" "$STUB/tests"
STUB_COV="$STUB/tests/coverage.sh"

ts() { # ts NAME EXPECTED_DIAG CMD... — non-zero exit AND the stage's own
  # diagnostic, so a masked failure can't pass as the named one.
  local name="$1" diag="$2"; shift 2
  local out rc
  out=$("$@" 2>&1); rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q "$diag"; then
    echo "  ok   $name"
  else
    echo "  FAIL $name (rc=$rc; wanted diagnostic: $diag)"; fails=1
  fi
}
ts "missing llvm fails"    "llvm-tools-preview missing" \
  env COVERAGE_COMPONENTS=rust LLVM_PROFDATA=/nonexistent LLVM_COV=/nonexistent bash "$STUB_COV"
ts "broken cargo fails"    "instrumented cargo test failed" \
  env COVERAGE_COMPONENTS=rust CARGO_BIN=false LLVM_PROFDATA=/bin/true LLVM_COV=/bin/true bash "$STUB_COV"
ts "empty lcov report fails" "llvm-cov produced no report" \
  env COVERAGE_COMPONENTS=rust CARGO_BIN=/bin/true LLVM_PROFDATA=/bin/true LLVM_COV=/bin/false bash "$STUB_COV"
ts "broken go fails"       "go test -coverprofile failed" \
  env COVERAGE_COMPONENTS=go GO_BIN=false bash "$STUB_COV"
ts "broken dotnet fails"   "dotnet test --collect coverage failed" \
  env COVERAGE_COMPONENTS=csharp DOTNET_BIN=false bash "$STUB_COV"
ts "typo component fails"  "unknown component 'cshapr'" \
  env COVERAGE_COMPONENTS=cshapr bash "$STUB_COV"

# Gate mode (COVERAGE_GATE=1, set by checks.sh) must ignore env vars that
# would shrink the official run; selective runs stay available only for
# these self-tests and local debugging.
printf '%s\n' "$LCOV" > "$STUB/fake-50.lcov"
printf '#!/bin/sh\ncat %q\n' "$STUB/fake-50.lcov" > "$STUB/llvm-cov-fake"
chmod +x "$STUB/llvm-cov-fake"
ts "gate ignores COVERAGE_COMPONENTS" "go test -coverprofile failed" \
  env COVERAGE_GATE=1 COVERAGE_COMPONENTS=rust \
      CARGO_BIN=/bin/true LLVM_PROFDATA=/bin/true LLVM_COV=/bin/true \
      GO_BIN=false DOTNET_BIN=/bin/true bash "$STUB_COV"
ts "gate ignores COVERAGE_MIN" "coverage: rust 50.00% < 90%" \
  env COVERAGE_GATE=1 COVERAGE_MIN=50 COVERAGE_COMPONENTS=rust \
      CARGO_BIN=/bin/true LLVM_PROFDATA=/bin/true LLVM_COV="$STUB/llvm-cov-fake" \
      GO_BIN=false DOTNET_BIN=/bin/true bash "$STUB_COV"
t "selective run still works outside the gate" 0 \
  env COVERAGE_COMPONENTS=rust COVERAGE_MIN=50 \
      CARGO_BIN=/bin/true LLVM_PROFDATA=/bin/true LLVM_COV="$STUB/llvm-cov-fake" \
      bash "$STUB_COV"

# Artifact preservation: sentinels sit inside the two dirs coverage.sh
# rm -rf's before measuring. If a stub run ever touched the real tree the
# sentinels would be gone — surviving files prove isolation held.
SENT_RUST="$ROOT/engine-rust/target/profraw/selftest-keep.profraw"
SENT_COV="$ROOT/control-dotnet/VpnControl.Tests/TestResults/coverage/selftest-keep.xml"
mkdir -p "$(dirname "$SENT_RUST")" "$(dirname "$SENT_COV")"
echo sentinel > "$SENT_RUST"
echo sentinel > "$SENT_COV"
env COVERAGE_COMPONENTS=rust CARGO_BIN=false LLVM_PROFDATA=/bin/true \
    LLVM_COV=/bin/true bash "$STUB_COV" >/dev/null 2>&1 || true
t "official artifacts preserved" 0 test -f "$SENT_RUST" -a -f "$SENT_COV"
rm -f "$SENT_RUST" "$SENT_COV"  # only files this test created

[ "$fails" -eq 0 ] && echo "coverage.test.sh: ALL PASSED" || { echo "coverage.test.sh: FAILED"; exit 1; }
