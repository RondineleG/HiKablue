#!/usr/bin/env bash
# coverage.sh — measure line coverage for the three components and print a
# summary table. Reporting only (no threshold yet): fails only when a tool
# breaks, not on a low percentage.
#
#   rust  — rustc -C instrument-coverage + llvm-profdata/llvm-cov
#   go    — go test -coverprofile + go tool cover -func
#   c#    — coverlet (XPlat Code Coverage collector, cobertura xml)
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
fail=0

line() { printf '%-10s %s\n' "$1" "$2"; }

echo "== rust (engine-rust) =="
RUST_DIR="$ROOT/engine-rust"
PROFRAW_DIR="$RUST_DIR/target/profraw"
rm -rf "$PROFRAW_DIR" "$RUST_DIR/target/coverage.profdata"
mkdir -p "$PROFRAW_DIR"
LLVM_BIN="$(find "$HOME/.rustup/toolchains" -name llvm-profdata 2>/dev/null | head -1)"
if [ -z "$LLVM_BIN" ]; then
  line rust "FAIL: llvm-tools-preview not installed (rustup component add llvm-tools-preview)"
  fail=1
else
  LLVM_PROFDATA="$LLVM_BIN"
  LLVM_COV="$(dirname "$LLVM_BIN")/llvm-cov"
  if ! (cd "$RUST_DIR" && RUSTFLAGS="-C instrument-coverage" \
        LLVM_PROFILE_FILE="$PROFRAW_DIR/cov-%p-%m.profraw" \
        cargo test --quiet >/dev/null 2>&1); then
    line rust "FAIL: instrumented cargo test failed"
    fail=1
  else
    "$LLVM_PROFDATA" merge -sparse "$PROFRAW_DIR"/*.profraw \
      -o "$RUST_DIR/target/coverage.profdata" 2>/dev/null
    objs=()
    for f in "$RUST_DIR"/target/debug/deps/vpn_engine-*; do
      [ -f "$f" ] && [ -x "$f" ] && objs+=(--object "$f")
    done
    out=$("$LLVM_COV" report "${objs[@]}" \
      --instr-profile="$RUST_DIR/target/coverage.profdata" \
      --sources "$RUST_DIR/src" \
      --summary-only 2>/dev/null | grep -E "^TOTAL|Filename")
    if [ -z "$out" ]; then
      line rust "FAIL: llvm-cov produced no report"
      fail=1
    else
      echo "$out"
    fi
  fi
fi

echo "== go (agent-go) =="
if ! (cd "$ROOT/agent-go" && go test -coverprofile=coverage.out ./... >/dev/null 2>&1); then
  line go "FAIL: go test -coverprofile failed"
  fail=1
else
  (cd "$ROOT/agent-go" && go tool cover -func=coverage.out | tail -1)
fi

echo "== c# (control-dotnet) =="
CD_DIR="$ROOT/control-dotnet"
DOTNET_BIN="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
rm -rf "$CD_DIR/VpnControl.Tests/TestResults"
if ! (cd "$CD_DIR" && "$DOTNET_BIN" test VpnControl.Tests/VpnControl.Tests.csproj \
      --collect:"XPlat Code Coverage" -v q >/dev/null 2>&1); then
  line csharp "FAIL: dotnet test --collect coverage failed"
  fail=1
else
  covxml=$(find "$CD_DIR/VpnControl.Tests/TestResults" -name "coverage.cobertura.xml" | head -1)
  if [ -z "$covxml" ]; then
    line csharp "FAIL: no cobertura xml"
    fail=1
  else
    python3 - "$covxml" <<'PY'
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
for tag in ("line-rate", "branch-rate"):
    v = float(r.get(tag, 0))
    print(f"csharp {tag}: {v*100:.1f}%")
PY
  fi
fi

[ "$fail" -eq 0 ] && echo "coverage: measured" || { echo "coverage: FAIL"; exit 1; }
