#!/usr/bin/env bash
# coverage.test.sh — regression tests for the coverage collector itself
# (TEST-01): threshold math, report parsers, and failure modes. Fixtures
# and stubs only — the real measurement stays in coverage.sh.
set -u
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

echo "-- lcov-percent.py --"
# fake_engine.rs: DA 1-4 counted (2 covered → 50.00); DA:6 sits in a
# lab-only span and DA:9 inside #[cfg(test)] — both must be dropped.
LCOV=$(printf 'TN:\nSF:%s/fake_engine.rs\nDA:1,1\nDA:2,1\nDA:3,0\nDA:4,0\nDA:6,1\nDA:9,1\nend_of_record\n' "$FIX")
t "valid lcov → 50.00" 0 test "$(printf '%s' "$LCOV" | python3 "$ROOT/tests/lcov-percent.py")" = "50.00"
t "empty report fails"   1 bash -c "printf '' | python3 $ROOT/tests/lcov-percent.py"
t "garbage fails"        1 bash -c "printf 'not lcov at all\n' | python3 $ROOT/tests/lcov-percent.py"

echo "-- cobertura-percent.py --"
t "valid xml parses"     0 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/coverage.cobertura.xml"
t "missing file fails"   1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/nope.xml"
t "invalid xml fails"    1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/not-xml.xml"
t "non-cobertura fails"  1 python3 "$ROOT/tests/cobertura-percent.py" "$FIX/wrong-root.xml"

echo "-- coverage.sh failure modes (stubbed tools) --"
# Stub every stage to fail fast: each run must still exit non-zero.
t "missing llvm fails"   1 env LLVM_PROFDATA=/nonexistent LLVM_COV=/nonexistent GO_BIN=false DOTNET_BIN=false bash "$ROOT/tests/coverage.sh"
t "broken cargo fails"   1 env CARGO_BIN=false GO_BIN=false DOTNET_BIN=false bash "$ROOT/tests/coverage.sh"
t "broken go fails"      1 env LLVM_PROFDATA=/nonexistent LLVM_COV=/nonexistent GO_BIN=false DOTNET_BIN=false bash "$ROOT/tests/coverage.sh"

[ "$fails" -eq 0 ] && echo "coverage.test.sh: ALL PASSED" || { echo "coverage.test.sh: FAILED"; exit 1; }
