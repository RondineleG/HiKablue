# Milestone 5 review-fix validation report

- Repository: `RondineleG/HiKablue`
- Fix branch: `fix/milestone-5-review-blockers`
- Base under review: PR #24 HEAD `3be9fe620a1b9b33c4fe1731151e7c04117f4db1`
- Stream-publication implementation commit: `a3e039a4af5348c2b2269d5a57c3a2f5016fb82c`
- Validated executable/test commit: `67c81dbad81b5be2affe1992ead5374d841b97d9`
- Debian-validated release commit: `15f0716e89dcd437cbe89ae71dcb3e4ad336bdaf` (`15f0716e89dc` in the release `VERSION` file; later commits on the branch changed documentation/evidence only)
- Host for regression/lab gates: Ubuntu 22.04.5 LTS, kernel 6.8.0-1060-aws
- systemd end-to-end validation VM: Debian GNU/Linux 12 (bookworm), kernel 6.1.0-56-cloud-amd64, booted as a clean ephemeral cloud-image VM and discarded after validation
- Toolchains observed: Go 1.18.1, Rust 1.99.1, .NET SDK 10.0.300, ShellCheck 0.8.0
- Traffic boundary: repository-generated isolated-lab traffic only in network namespaces and loopback/RFC1918 test addresses. The Debian validation provisioned and revoked one lab session through local services but generated no external tunnel traffic.

## Fixes covered by this round

- The node stream is now allocated and the superseded stream hidden before registration state is touched, but the replacement is not visible to the dispatcher until the registration transaction commits (`BeginAttach`/`Publish`). If registrations race, only the newer committed generation remains published.
- Release and parent directories are normalized to service-readable permissions even when a release archive was unpacked under a restrictive umask. Reinstalling an identical release also repairs existing modes, because content diffing does not compare permissions. Live `.env` files remain root-owned mode `0600`; secrets do not belong in release trees.
- A superseded in-flight registration is rejected after the node-row lock before it can overwrite a newer backend identity or enqueue another reset.
- The earlier review fixes remain covered: atomic backend-reset registration, pending-reset reuse across pre-ACK reconnects, provisioning exclusion until reset ACK, and group-owned sockets with UID authorization preserved.

## Regression and coverage

Commands:

```bash
bash tests/checks.sh --lab
export PATH="$HOME/.dotnet:$PATH"
bash tests/deploy/install-rollback.test.sh
bash tests/book/check-parity.sh
```

Observed results:

| Verification | Result |
|---|---|
| full repository gate `bash tests/checks.sh --lab` | PASS — `passed: 21 failed: 0 skipped: 0` |
| Rust fmt/clippy/test/release build inside gate | PASS — 115 tests passed |
| Go gofmt/vet/tests inside gate | PASS |
| .NET build/tests inside gate | PASS — 166 tests passed, 0 failed, 0 skipped |
| offline install/rollback | PASS — installation repeatable, config preserved, restrictive-umask and repeated-install permissions corrected, upgrade/rollback correct |
| book parity | PASS — en/es/pt-BR chapter parity |
| coverage floor | PASS — Rust 91.80% lines, Go 90.4% statements, C# 96.0% lines |

The stream-publication regression tests include:

- `BeginAttachHidesTheOldStreamWithoutPublishingTheNewOne` — the previous stream is hidden/cancelled while no replacement is visible before commit.
- `PublishKeepsTheNewerStreamWhenRegistrationsRace` — a newer prepared registration wins; the older stream is not left published.
- `StreamIsHiddenUntilRegistrationCommitThenPublished` — a PostgreSQL trigger slows the node update; during the open registration transaction the node has no visible stream, and after commit the replacement becomes visible.
- `SupersededRegistrationCannotOverwriteNewerBackendOrQueueReset` — commits the newer backend first, then verifies the older prepared registration aborts without changing the backend identity or adding a reset.

The reset-recovery regression tests include:

- `InterruptedBackendRestartRegistrationRollsBackAndRetryRecovers` — injects a database failure before the reset snapshot insert commit, verifies rollback, then verifies retry recovery.
- `ReconnectBeforeResetAckReusesPendingResetAndAckCompletesRecovery` — reconnects the same backend before ACK, verifies the same reset row is reused, then verifies terminal session state and lease release after valid ACK.
- `ConcurrentCreateWaitsBehindRegistrationAndCannotJoinInvalidatedGeneration` — slows registration inside PostgreSQL, starts session creation concurrently, verifies it waits behind the node lock and cannot join the invalidated generation.
- `PendingBackendResetBlocksNewSessionsUntilAckThenProvisionSurvives` — verifies the node is ineligible while reset is pending and that post-ACK provisioning creates a surviving `ProvisionPeer` command.
- `BackendResetDeadlineRejectionRetriesInsteadOfReopeningProvisioning` — verifies a first-attempt deadline rejection returns a reset to `pending` instead of reopening provisioning before confirmation.
- `BackendRestartWithoutSessionsStillMarksResetUntilAck` — verifies an idle backend restart still records reset metadata and keeps the node blocked until ACK.

## Laboratory scenarios inside the full gate

| Scenario | Observed result |
|---|---|
| netns ping | PASS |
| telemetry end-to-end | PASS |
| foreign-resource preservation | PASS |
| milestone-4 normal revocation | PASS |
| milestone-4 gRPC reconciliation | PASS |
| milestone-4 local expiry across agent restart | PASS |
| milestone-5 engine restart and route rollback | PASS |
| session resource ownership regressions | 5 cases PASSED |

The engine-restart scenario observed:

```text
OBSERVED: agent detected the new backend instance and exited for supervised restart
OBSERVED: confirmed empty-state reconciliation failed pre-restart sessions and released leases
OBSERVED: pre-restart keys cannot carry traffic on the new backend instance
OBSERVED: abrupt client termination removed tun0 and rolled back its connected tunnel route
OBSERVED: a fresh session with fresh keys restored service connectivity after engine restart
session-e2e (engine-restart): PASS
```

## Real Debian systemd end-to-end validation — distinct from offline install

Offline install/rollback is a file-layout validation in a temporary root. It does not start services. Real service startup and provisioning were validated separately in a clean Debian 12 cloud-image VM with systemd running.

Commands, summarized exactly by phase:

```bash
# built the release on the validation host
deploy/debian/build-release.sh --force --output /tmp/hk-debian-e2e-release

# inside the clean Debian VM, after copying the release, a portable .NET runtime,
# and lab-only test PKI
sudo bash /tmp/hk-debian-install.sh --release /tmp/hk-debian-e2e-release
sudo systemctl restart hikablue-control.service
sudo systemctl restart hikablue-engine.service
sudo systemctl restart hikablue-agent.service
curl --cacert /etc/hikablue/pki/ca.crt -sf -X POST https://127.0.0.1:7443/v1/sessions \
    -H 'Content-Type: application/json' -H 'X-Subject: debian-validation' \
    -H 'Idempotency-Key: debian-validation-1' \
    -d '{"nodeId":"node-a","ttlSeconds":180}' > "$create_json"
jq '.data.sessionToken="<redacted>" | .data.egressKey="<redacted>" | .data.ingressKey="<redacted>"' "$create_json"
sudo -u hikablue-agent python3 /tmp/hk-control-query.py
sudo -u hikablue-agent /opt/hikablue/current/bin/vpn-agent --metrics-sock /run/hikablue-engine/engine.sock
curl --cacert /etc/hikablue/pki/ca.crt -sf -X DELETE "https://127.0.0.1:7443/v1/sessions/$session_id" \
    -H 'X-Subject: debian-validation' -H "X-Session-Token: $session_token"
```

The complete ephemeral in-VM recipe is stored as
`artifacts/validation/milestone-5-debian-e2e-validate.sh`. It includes the
PostgreSQL/.NET setup, random secret generation, service environment files,
the framed `GetSessions` Unix-socket helper, polling loops, revocation
check, lease query, and redactable evidence output. The script was copied
into the VM and executed as the unprivileged `debian` user with sudo.

Observed output:

```text
installed release 15f0716e89dc
control=active
engine=active
agent=active
660 hikablue-engine hikablue-agent /run/hikablue-engine/control.sock
660 hikablue-engine hikablue-agent /run/hikablue-engine/engine.sock
session_status=active
session_virtual_ip=10.80.1.160
outbox_states=SyncState:acked:attempts=1,ProvisionPeer:acked:attempts=1
GetSessions status=ok with one active peer at 10.80.1.160
GetMetrics:{"rx_bytes":0,"rx_pkts":0,"tx_bytes":0,"tx_pkts":0,"dropped_pkts":12,"peers":0,"uptime_secs":2}
session_final_status=revoked
GetSessionsAfterRevoke status=ok with zero peers and revocations=1
leases_remaining=0
debian=Debian GNU/Linux 12 (bookworm)
kernel=6.1.0-56-cloud-amd64
release=15f0716e89dc
control=active engine=active agent=active
DEBIAN-E2E-PASS
```

The sanitized captured output is stored beside this report as `milestone-5-review-fixes-debian-e2e.txt`; ephemeral session keys and the one-time session token are redacted. This validation proves the packaged control-plane, engine, and agent units start under their service accounts, the agent registers over mTLS, REST session creation reaches the outbox/gRPC/agent/engine path, the peer is visible through the UID-authorized control socket, and revocation removes the peer and releases the lease.

## Explicitly not validated here

- PostgreSQL rollback/backup, certificate issuance by a real CA process, secret rotation, production routing, NAT, DNS, full tunneling, IPv6 policy, and WireGuard remain outside this milestone.
- The Debian end-to-end validation did not generate client tunnel traffic; packet forwarding and application connectivity remain covered by the isolated Ubuntu laboratory scenarios above.
