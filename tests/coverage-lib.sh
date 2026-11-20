#!/usr/bin/env bash
# coverage-lib.sh — pure threshold logic shared by coverage.sh and its
# self-test (coverage.test.sh). No tool execution here: this file only
# decides whether a measured percentage satisfies TEST-02.

# pct_ge_min PCT MIN → 0 when the raw PCT >= MIN, else 1.
# Comparisons are floats on the *unrounded* value so 89.99 fails a 90 bar.
# Missing or non-numeric input counts as "no valid measurement" → 1.
pct_ge_min() {
  [ -n "$1" ] && [ -n "$2" ] || return 1
  awk -v p="$1" -v m="$2" 'BEGIN{exit !((p+0) >= (m+0))}'
}
