#!/usr/bin/env bash
# Shared ownership registry for the privileged integration laboratories.
# A scope records only resources created by the current shell process; cleanup
# never guesses names or deletes preexisting objects.

lab_as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        sudo "$@"
    fi
}

lab_scope_init() {
    LAB_OWNED_NETNS=()
    LAB_OWNED_ROOT_LINKS=()
    LAB_OWNED_ROOT_PIDS=()
}

lab_netns_exists() {
    lab_as_root ip netns exec "$1" true >/dev/null 2>&1
}

lab_netns_add() {
    local ns=$1
    if lab_netns_exists "$ns"; then
        echo "namespace $ns already exists; refusing to proceed" >&2
        return 1
    fi
    lab_as_root ip netns add "$ns"
    LAB_OWNED_NETNS+=("$ns")
}

lab_root_veth_add() {
    local host_if=$1 peer_if=$2
    lab_as_root ip link add "$host_if" type veth peer name "$peer_if"
    # Both ends are registered: whichever end is still in the root namespace
    # on the failure path is removed; ends already moved into a run-owned
    # namespace disappear with that namespace.
    LAB_OWNED_ROOT_LINKS+=("$host_if" "$peer_if")
}

lab_register_root_pid() {
    local pid=$1 marker=$2
    [ -n "$pid" ] || return 0
    LAB_OWNED_ROOT_PIDS+=("$pid|$marker")
}

lab_kill_root_pid() {
    local entry=$1 pid marker cmdline
    pid=${entry%%|*}
    marker=${entry#*|}
    [ -n "$pid" ] || return 0
    # PID reuse must never turn cleanup into a foreign-process kill. Every
    # privileged lab process carries the run directory in its command line;
    # if that ownership marker is gone, leave the process alone.
    if [ -r "/proc/$pid/cmdline" ]; then
        cmdline=$(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null || true)
        case "$cmdline" in
            *"$marker"*) ;;
            *) return 0 ;;
        esac
    else
        return 0
    fi
    lab_as_root kill "$pid" >/dev/null 2>&1 || true
}

lab_cleanup_scope() {
    local i
    for ((i=${#LAB_OWNED_ROOT_PIDS[@]}-1; i>=0; i--)); do
        lab_kill_root_pid "${LAB_OWNED_ROOT_PIDS[$i]}"
    done
    LAB_OWNED_ROOT_PIDS=()

    for ((i=${#LAB_OWNED_ROOT_LINKS[@]}-1; i>=0; i--)); do
        lab_as_root ip link del "${LAB_OWNED_ROOT_LINKS[$i]}" >/dev/null 2>&1 || true
    done
    LAB_OWNED_ROOT_LINKS=()

    for ((i=${#LAB_OWNED_NETNS[@]}-1; i>=0; i--)); do
        lab_as_root ip netns del "${LAB_OWNED_NETNS[$i]}" >/dev/null 2>&1 || true
    done
    LAB_OWNED_NETNS=()
}
