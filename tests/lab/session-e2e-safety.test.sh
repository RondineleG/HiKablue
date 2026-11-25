#!/usr/bin/env bash
# Regression tests for run-scoped ownership in tests/lab/lab-resources.sh.
# Requires root (or passwordless sudo) and creates only uniquely named
# namespaces/links inside this test's own scopes.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
# shellcheck source=tests/lab/lab-resources.sh
. tests/lab/lab-resources.sh

TAG=$(openssl rand -hex 4)
TMP=$(mktemp -d /tmp/hk-m4-scope.XXXXXX)
trap 'lab_cleanup_scope; rm -rf "$TMP"' EXIT
pass=0

# 1. Partial creation: only the namespace that was actually created is
# registered, so a later failure still cleans exactly that resource.
lab_scope_init
partial_ns="hkp${TAG}a"
lab_netns_add "$partial_ns"
# Simulate a failure before the second namespace is created: the second name
# is never registered and cleanup must still remove the first.
lab_cleanup_scope
! lab_netns_exists "$partial_ns" || { echo "FAIL: partial namespace leaked" >&2; exit 1; }
echo "PASS: partial creation cleans only created namespaces"
pass=$((pass + 1))

# 2. Preexisting resource: a foreign namespace outside this scope survives
# cleanup untouched.
foreign_ns="hkforeign${TAG}"
lab_as_root ip netns add "$foreign_ns"
lab_scope_init
owned_ns="hkp${TAG}b"
lab_netns_add "$owned_ns"
lab_cleanup_scope
lab_netns_exists "$foreign_ns" || { echo "FAIL: cleanup removed a foreign namespace" >&2; lab_as_root ip netns del "$foreign_ns" || true; exit 1; }
lab_as_root ip netns del "$foreign_ns"
echo "PASS: preexisting namespace survives scope cleanup"
pass=$((pass + 1))

# 3. Overlapping scopes: scope A can exit while scope B's namespace is still
# active; A's cleanup must not disturb B. This is the resource-lifecycle part
# of concurrent lab execution.
ns_one="hkc${TAG}1"
ns_two="hkc${TAG}2"
ready_one="$TMP/one.ready"
ready_two="$TMP/two.ready"
go_one="$TMP/one.go"
go_two="$TMP/two.go"
(
    lab_scope_init
    lab_netns_add "$ns_one"
    touch "$ready_one"
    while [ ! -e "$go_one" ]; do sleep 0.02; done
    lab_cleanup_scope
) &
p1=$!
(
    lab_scope_init
    lab_netns_add "$ns_two"
    touch "$ready_two"
    while [ ! -e "$go_two" ]; do sleep 0.02; done
    lab_cleanup_scope
) &
p2=$!
release_scope_workers() {
    touch "$go_one" "$go_two" 2>/dev/null || true
    wait "$p1" 2>/dev/null || true
    wait "$p2" 2>/dev/null || true
}
for _ in $(seq 200); do
    [ -e "$ready_one" ] && [ -e "$ready_two" ] && break
    sleep 0.02
done
[ -e "$ready_one" ] && [ -e "$ready_two" ] || { release_scope_workers; echo "FAIL: concurrent scopes did not start" >&2; exit 1; }
touch "$go_one"
wait "$p1"
if ! lab_netns_exists "$ns_two"; then
    release_scope_workers
    echo "FAIL: first scope removed concurrent namespace" >&2
    exit 1
fi
touch "$go_two"
wait "$p2"
! lab_netns_exists "$ns_one" || { echo "FAIL: concurrent namespace one leaked" >&2; exit 1; }
! lab_netns_exists "$ns_two" || { echo "FAIL: concurrent namespace two leaked" >&2; exit 1; }
echo "PASS: overlapping scopes preserve each other's resources"
pass=$((pass + 1))

# 4. Partial veth creation: a run-created pair left in the root namespace is
# removed by name without depending on namespace deletion.
lab_scope_init
link_a="hkv${TAG}a"
link_b="hkv${TAG}b"
lab_root_veth_add "$link_a" "$link_b"
lab_cleanup_scope
! ip link show "$link_a" >/dev/null 2>&1 || { echo "FAIL: root veth $link_a leaked" >&2; exit 1; }
! ip link show "$link_b" >/dev/null 2>&1 || { echo "FAIL: root veth $link_b leaked" >&2; exit 1; }
echo "PASS: partially created root veth pair is owned and removed"
pass=$((pass + 1))

# 5. Process ownership: a privileged process started by this scope is killed;
# a stale/reused PID without the run marker is left alone.
lab_scope_init
lab_as_root env PIDFILE="$TMP/sleep.pid" python3 -c \
    'import os, time; open(os.environ["PIDFILE"], "w").write(str(os.getpid())); time.sleep(60)' \
    "$TMP" &
for _ in $(seq 50); do [ -s "$TMP/sleep.pid" ] && break; sleep 0.02; done
sleep_pid=$(cat "$TMP/sleep.pid")
lab_register_root_pid "$sleep_pid" "$TMP"
lab_cleanup_scope
for _ in $(seq 100); do
    if ! { [ -r "/proc/$sleep_pid/cmdline" ] && tr '\0' ' ' <"/proc/$sleep_pid/cmdline" | grep -q sleep; }; then
        break
    fi
    sleep 0.01
done
if [ -r "/proc/$sleep_pid/cmdline" ] && tr '\0' ' ' <"/proc/$sleep_pid/cmdline" | grep -q sleep; then
    echo "FAIL: owned privileged process survived cleanup" >&2
    exit 1
fi
echo "PASS: owned privileged process is stopped"
pass=$((pass + 1))

echo "session-e2e-safety: $pass case(s) PASSED"
