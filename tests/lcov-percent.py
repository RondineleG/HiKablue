#!/usr/bin/env python3
"""lcov-percent.py — percent of covered DA: lines from lcov on stdin.

Drops, per source file, everything from its `#[cfg(test)]` module line
onward and every `// coverage: lab-only begin` .. `end` span, so test
code and lab-only paths never inflate the engine's coverage.

Exit 0 + "NN.NN" on stdout when at least one lcov record was parsed;
nonzero on empty or record-less input (a report that says nothing is
a broken report, not 0% coverage).
"""
import re
import sys
import pathlib

total = covered = 0
sf = test_start = None
lab_spans = []
saw_record = False

for ln in sys.stdin.read().splitlines():
    if ln.startswith("SF:"):
        saw_record = True
        sf = ln[3:]
        test_start = None
        lab_spans = []
        src = pathlib.Path(sf).read_text().splitlines()
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

if not saw_record:
    sys.exit("lcov-percent: no SF records — empty or invalid report")
print(f"{covered / total * 100:.2f}" if total else "0.0")
