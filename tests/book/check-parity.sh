#!/bin/sh
# check-parity.sh — verify the three ebook editions carry the same chapters.
#
# For every chapter file in book/en, the same filename must exist in book/es
# and book/pt-BR, and the <!-- chapter-id: ... --> marker must match across
# all three. Run from the repository root: sh tests/book/check-parity.sh
set -eu

cd "$(dirname "$0")/../.."
fail=0

for en_file in book/en/ch*.md; do
    name=$(basename "$en_file")
    en_id=$(sed -n 's/.*chapter-id: \([^ ]*\).*/\1/p' "$en_file" | head -1)
    if [ -z "$en_id" ]; then
        echo "FAIL: $en_file has no chapter-id marker" >&2
        fail=1
    fi
    for lang in es pt-BR; do
        other="book/$lang/$name"
        if [ ! -f "$other" ]; then
            echo "FAIL: $other missing (edition of $en_file)" >&2
            fail=1
            continue
        fi
        other_id=$(sed -n 's/.*chapter-id: \([^ ]*\).*/\1/p' "$other" | head -1)
        if [ "$other_id" != "$en_id" ]; then
            echo "FAIL: chapter-id mismatch: $en_file='$en_id' vs $other='$other_id'" >&2
            fail=1
        fi
    done
done

for lang in es pt-BR; do
    for f in book/$lang/ch*.md; do
        [ -e "$f" ] || continue
        name=$(basename "$f")
        [ -f "book/en/$name" ] || {
            echo "FAIL: $f has no English edition" >&2
            fail=1
        }
    done
done

if [ "$fail" -eq 0 ]; then
    echo "OK: chapter parity across en/es/pt-BR"
fi
exit "$fail"
