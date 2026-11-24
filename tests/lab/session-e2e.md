# Milestone 4 session laboratory

`session-e2e.sh` is the reproducible end-to-end acceptance laboratory for
milestone 4. It uses only test traffic inside Linux network namespaces
created by the script and removes those namespaces when it exits.

## Topology

```text
client A namespace ─┐
                    ├─ bridge in exit namespace ── exit engine ── service namespace
client B namespace ─┘        (outer veths)          (TUN/UDP)      HTTP 10.80.1.100:8080
```

The exit engine is provisioned through the real control path:

```text
POST /v1/sessions
  -> PostgreSQL session + lease + outbox row
  -> mTLS gRPC NodeChannel
  -> vpn-agent -serve
  -> Rust control socket StagePeer + Activate
  -> CommandAck
  -> session pending -> active
```

The two client namespaces run `vpn-engine client`. For milestone 4 their
first epoch is bootstrapped with the exact keys returned by the lab session
API (`--tx-key`, `--rx-key`, and complementary wire indexes). The client
direction is the inverse of the exit engine direction. This is lab-only
bootstrap, not the later user-facing client provisioning flow.

The service namespace runs `python3 -m http.server` bound to
`10.80.1.100:8080` behind the exit node. Client-to-client hairpinning is
intentionally denied in the milestone-4 policy; forwarded service answers
may keep the service source address.

## Prerequisites

Run on a Linux host with:

- Rust (`cargo`), Go 1.18.x, .NET 10, Python 3, `jq`, `curl`, `openssl`,
  `xxd`, `iproute2`, and PostgreSQL server binaries;
- passwordless `sudo` for network namespaces and TUN creation;
- `/dev/net/tun`;
- no listener on TCP ports 7443, 7444, or 55432;
- no pre-existing `hkm4exit`, `hkm4ca`, `hkm4cb`, or `hkm4svc` namespace.

The script starts a temporary PostgreSQL cluster inside its own `mktemp`
directory. It does not require Docker. It creates a lab-only CA, a node
client certificate, and a loopback server certificate. All keys are test
material and are deleted with the run directory.

## Commands

From the repository root:

```sh
# normal revocation acceptance
tests/lab/session-e2e.sh

# independent gRPC interruption/reconciliation acceptance
tests/lab/session-e2e.sh --drop

# repository quality gate, including the privileged labs
tests/checks.sh --lab
```

The normal run verifies that both clients receive distinct leases, remain
`pending` until valid provisioning ACKs, pass HTTP through the tunnel, and
that revoking A blocks A while continuous requests from B keep succeeding.
The drop run closes a deliberately delayed TCP proxy while revocation is in
flight, checks that A remains `revoking` with its lease reserved, restores
the proxy, and waits for redelivery/reconciliation to finish removal before
the lease is released.

## Observed milestone-4 results

Observed on the Ubuntu development VM used for this change:

| Area | Result |
|---|---|
| Compilation | Rust release build, Go `vpn-agent` build, and C# `VpnControl` build completed before each laboratory run |
| Packet forwarding | final server metrics in the normal run: `rx_pkts=105`, `tx_pkts=105`, `dropped_pkts=4`, `peers=1`; drop run: `rx_pkts=152`, `tx_pkts=151`, `dropped_pkts=5`, `peers=1` |
| HTTP connectivity | both clients received exactly `hikablue-m4-ok` through the tunnel; the direct-path probe failed before tunnel setup |
| Normal revocation | `session-e2e (normal): PASS` — A lost access, B had no failed continuous responses, and A's lease count changed from 1 to 0 only after the node ACK |
| gRPC interruption | `session-e2e (drop): PASS` — A stayed `revoking` with lease count 1 while the channel was down; restoring the channel completed removal, marked the session `revoked`, released the lease, and A still had no access |

Exact counters vary because keepalives and continuous probe timing vary;
the acceptance output lines are the stable result.


## Contract notes and explicit deferrals

- `vpn-debian-specification.md` still names the Go↔Rust methods
  `AddPeer`, `RemovePeer`, `SyncPeers`, and `GetMetrics` on
  `/run/vpn/engine.sock`. This change deliberately reuses the control
  boundary already delivered for milestone 4 instead of renaming it: a
  separate control socket carries `StagePeer`, `Activate`, `Retire`,
  `RevokePeer`, and `GetSessions`, while the metrics socket remains
  read-only. That naming/socket divergence is documented here rather than
  silently redesigned during the acceptance work.
- The exact client keys appear as command-line arguments only because the
  clients are disposable lab engines in namespaces. This is not the final
  client key-delivery design and must not be copied to production use.
- Backend restart reprovisioning is outside this laboratory. Snapshots
  intentionally carry peer/key references, not raw keys, so a new backend
  instance cannot reconstruct old epochs from `SyncState`; explicit fresh
  provisioning remains a later architecture step.
- Local expiration is enforced by the running agent and its expiration
  deadlines are rehydrated from valid snapshots after restart. The test
  does not kill the agent itself; it validates control-plane channel loss,
  which is the milestone-4 acceptance scenario.

## Cleanup and failure artifacts

The script removes only namespaces, processes, PostgreSQL data, sockets,
keys, and temporary files that it created in its run directory. On failure
it copies the run directory to `/tmp/hk-m4-session-failed` before cleanup
so `control.log`, `agent.log`, engine logs, PostgreSQL logs, and session
JSON can be inspected. Delete that directory manually after investigation.
It can contain lab key material; never commit it.
