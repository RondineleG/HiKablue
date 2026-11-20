#!/usr/bin/env python3
"""cobertura-percent.py XML — print "line% branch%" from a cobertura file.

Nonzero exit when the file is missing, unreadable, or not cobertura XML —
a broken report must never read as a passing measurement.
"""
import sys
import xml.etree.ElementTree as ET

try:
    root = ET.parse(sys.argv[1]).getroot()
except Exception as exc:
    sys.exit(f"cobertura-percent: {exc}")

if root.tag != "coverage":
    sys.exit("cobertura-percent: not a cobertura report")

print(f"{float(root.get('line-rate', 0)) * 100:.1f} "
      f"{float(root.get('branch-rate', 0)) * 100:.1f}")
