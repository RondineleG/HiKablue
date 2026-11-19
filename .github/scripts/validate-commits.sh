#!/usr/bin/env bash
set -euo pipefail

if [[ ! "${BRANCH_NAME:-}" =~ ^(feat|fix)/[^/]+$ ]]; then
  echo "Invalid branch name: ${BRANCH_NAME:-<empty>}"
  echo "Use feat/<slug> for additive work or fix/<slug> for bug fixes."
  exit 1
fi

if [[ -z "${BASE_SHA:-}" || -z "${HEAD_SHA:-}" ]]; then
  echo "BASE_SHA and HEAD_SHA are required."
  exit 1
fi

git cat-file -e "${BASE_SHA}^{commit}"
git cat-file -e "${HEAD_SHA}^{commit}"

pattern='^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^()]+\))?(!)?: .+$'
mapfile -t commits < <(git rev-list --no-merges "${BASE_SHA}..${HEAD_SHA}")

if ((${#commits[@]} == 0)); then
  echo "No non-merge commits found in the pull request range."
  exit 0
fi

invalid=0
for commit in "${commits[@]}"; do
  subject=$(git show -s --format=%s "$commit")
  if [[ ! "$subject" =~ $pattern ]]; then
    echo "Invalid commit subject (${commit:0:8}): $subject"
    invalid=1
  fi
done

if ((invalid)); then
  echo "Use Conventional Commits: <type>[optional scope][!]: <description>."
  exit 1
fi

echo "Branch name and all non-merge commit subjects are valid."
