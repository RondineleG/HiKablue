#!/usr/bin/env bash
# coverage-lib.sh — pure threshold logic shared by coverage.sh and its
# self-test (coverage.test.sh). No tool execution here: this file only
# decides whether a measured percentage satisfies TEST-02.

# pct_ge_min PCT MIN → 0 when the raw PCT >= MIN, else 1.
# Both values must be complete decimals in [0,100] — awk's +0 coercion
# would silently accept "95abc", "NaN" or "Infinity" as numbers, so the
# format is checked first and the range second. Comparisons are floats on
# the *unrounded* value so 89.99 fails a 90 bar. Missing, malformed or
# out-of-range input counts as "no valid measurement" → 1.
pct_ge_min() {
  [[ "${1:-}" =~ ^[0-9]+([.][0-9]+)?$ ]] || return 1
  [[ "${2:-}" =~ ^[0-9]+([.][0-9]+)?$ ]] || return 1
  awk -v p="$1" -v m="$2" 'BEGIN{exit !((p+0) >= (m+0) && (p+0) <= 100 && (m+0) <= 100)}'
}
