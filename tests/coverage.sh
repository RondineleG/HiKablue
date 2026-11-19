#!/usr/bin/env bash
# coverage.sh — measure line coverage for the three components and print a
# summary table. Fails when a tool breaks or a component is below the
# threshold (COVERAGE_MIN, default 90 — see .agc/RULES.md TEST-02).
#
#   rust  — rustc -C instrument-coverage + llvm-profdata/llvm-cov, exported as
#           lcov; lines inside #[cfg(test)] modules are dropped before the
#           total so test code does not count as engine coverage.
#   go    — go test -coverpkg=./... so packages without tests (cmd/vpn-agent)
#           still count as uncovered; go tool cover -func for the total.
#   c#    — coverlet (XPlat Code Coverage collector, cobertura xml); results
#           land in VpnControl.Tests/TestResults/coverage only.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
COVERAGE_MIN="${COVERAGE_MIN:-90}"
fail=0

line() { printf '%-10s %s\n' "$1" "$2"; }

below() { # below NAME PCT — records a threshold violation (float compare:
  # the displayed percentage is rounded, the check must use the raw value)
  if [ "$(awk -v p="$2" -v m="$COVERAGE_MIN" 'BEGIN{print (p+0 < m+0) ? 1 : 0}')" = "1" ]; then
    echo "coverage: $1 ${2}% < ${COVERAGE_MIN}% threshold"
    fail=1
  fi
}

echo "== rust (engine-rust) =="
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
  else
    rust_pct=$(python3 - "$lcov" <<'PY'
import sys
# lcov records: SF:<file> / DA:<line>,<count> / end_of_record
total = covered = 0
sf = test_start = None
lab_spans = []
import re, pathlib
for ln in sys.argv[1].splitlines():
    if ln.startswith("SF:"):
        sf = ln[3:]
        test_start = None
        lab_spans = []
        src = pathlib.Path(sf).read_text().splitlines()
        # Drop lines inside #[cfg(test)] modules: each file has at most one,
        # opened by the attribute line; ignore everything from there on.
        # Also drop lines inside "// coverage: lab-only begin" .. "end"
        # spans — code that only runs privileged and is exercised by the
        # lab steps instead of unit tests.
        begin = None
        for i, l in enumerate(src, 1):
            if re.match(r"\s*#\[cfg\(test\)\]", l):
                test_start = i
                break
            if "coverage: lab-only begin" in l:
                begin = i
            elif "coverage: lab-only end" in l and begin is not None:
                lab_spans.append((begin, i))
                begin = None
    elif ln.startswith("DA:"):
        num = int(ln[3:].split(",")[0])
        cnt = int(ln[3:].split(",")[1])
        if test_start is not None and num >= test_start:
            continue
        if any(a <= num <= b for a, b in lab_spans):
            continue
        total += 1
        if cnt > 0:
            covered += 1
if total:
    print(f"{covered/total*100:.2f}")
else:
    print("0.0")
PY
)
    line rust "lines (excl. test code + lab-only spans): ${rust_pct}%"
    below rust "$rust_pct"
  fi
fi

echo "== go (agent-go) =="
GO_BIN="${GO_BIN:-go}"
if ! (cd "$ROOT/agent-go" && "$GO_BIN" test -coverpkg=./... \
      -coverprofile=coverage.out ./... >/dev/null 2>&1); then
  line go "FAIL: go test -coverprofile failed"
  fail=1
else
  go_out=$(cd "$ROOT/agent-go" && "$GO_BIN" tool cover -func=coverage.out 2>/dev/null | tail -1)
  go_pct=$(echo "$go_out" | grep -oP '[0-9]+\.[0-9]+(?=%)')
  if [ -z "$go_pct" ]; then
    line go "FAIL: go tool cover produced no total"
    fail=1
  else
    line go "statements: ${go_pct}%"
    below go "$go_pct"
  fi
fi

echo "== c# (control-dotnet) =="
CD_DIR="$ROOT/control-dotnet"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
COV_DIR="$CD_DIR/VpnControl.Tests/TestResults/coverage"
rm -rf "$COV_DIR"   # only our own results dir — never the whole TestResults tree
if ! command -v python3 >/dev/null; then
  line csharp "FAIL: python3 required to parse cobertura xml"
  fail=1
elif ! (cd "$CD_DIR" && "$DOTNET_BIN" test VpnControl.Tests/VpnControl.Tests.csproj \
      --collect:"XPlat Code Coverage" --results-directory "$COV_DIR" -v q >/dev/null 2>&1); then
  line csharp "FAIL: dotnet test --collect coverage failed"
  fail=1
else
  covxml=$(find "$COV_DIR" -name "coverage.cobertura.xml" | head -1)
  if [ -z "$covxml" ]; then
    line csharp "FAIL: no cobertura xml"
    fail=1
  else
    cs_pct=$(python3 - "$covxml" <<'PY'
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(f"{float(r.get('line-rate', 0))*100:.1f} {float(r.get('branch-rate', 0))*100:.1f}")
PY
)
    if [ -z "$cs_pct" ]; then
      line csharp "FAIL: cobertura xml had no rates"
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
