# Milestone 5 review-fix validation report

- Repository: `RondineleG/HiKablue`
- Fix branch: `fix/milestone-5-review-blockers`
- Base under review: PR #24 HEAD `3be9fe620a1b9b33c4fe1731151e7c04117f4db1`
- Validated code/test commit: `4aa44d1377173783cbecbfbef75e918c5b588c29`
- Final documentation-only commit at report time: `4149ff552839d902f303e20d15f5dacc876b09fb`
- Host for regression/lab gates: Ubuntu 22.04.5 LTS, kernel 6.8.0-1060-aws
- systemd service validation VM: Debian GNU/Linux 12 (bookworm), kernel 6.1.0-56-cloud-amd64, booted as a clean ephemeral cloud-image VM and discarded after validation
- Toolchains observed: Go 1.18.1, Rust 1.99.1, .NET SDK 10.0.300, ShellCheck 0.8.0
- Traffic boundary: only repository-generated isolated-lab traffic in network namespaces and loopback/RFC1918 test addresses. The Debian validation used no tunnel traffic; it checked service startup and local Unix sockets only.

## Regression and coverage

Commands:

```bash
bash tests/checks.sh --lab
export PATH="$HOME/.dotnet:$PATH"
dotnet test control-dotnet/VpnControl.Tests -v q --nologo
bash tests/coverage.sh
bash tests/deploy/install-rollback.test.sh
bash tests/book/check-parity.sh
```

Observed results:

| Verification | Result |
|---|---|
| full repository gate `bash tests/checks.sh --lab` | PASS — `passed: 21 failed: 0 skipped: 0` |
| Rust fmt/clippy/test/release build inside gate | PASS — 115 tests passed |
| Go gofmt/vet/tests inside gate | PASS |
| .NET build/tests inside gate | PASS — 159 tests passed before the final test-only commit |
| final .NET regression at commit `4aa44d1` | PASS — 160 tests passed, 0 failed, 0 skipped |
| offline install/rollback | PASS — installation repeatable, config preserved, upgrade/rollback correct |
| book parity | PASS — en/es/pt-BR chapter parity |
| coverage floor | PASS — Rust 91.80% lines, Go 90.7% statements, C# 96.0% lines |

The three new regression tests are:

- `InterruptedBackendRestartRegistrationRollsBackAndRetryRecovers` — injects a database failure before the reset snapshot insert commit, verifies rollback, then verifies retry recovery.
- `ReconnectBeforeResetAckReusesPendingResetAndAckCompletesRecovery` — reconnects the same backend before ACK, verifies the same reset row is reused, then verifies terminal session state and lease release after valid ACK.
- `ConcurrentCreateWaitsBehindRegistrationAndCannotJoinInvalidatedGeneration` — slows registration inside PostgreSQL, starts session creation concurrently, verifies it waits behind the node lock and cannot join the invalidated generation.
- `PendingBackendResetBlocksNewSessionsUntilAckThenProvisionSurvives` — verifies the node is ineligible while reset is pending and that post-ACK provisioning creates a surviving `ProvisionPeer` command.

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

## Real Debian systemd validation — distinct from offline install

Offline install/rollback is a file-layout validation in a temporary root. It does not start services. Real service startup was validated separately in a clean Debian 12 cloud-image VM with systemd running.

Commands, summarized exactly by phase:

```bash
# built the release on the validation host
deploy/debian/build-release.sh --force --output /tmp/hk-debian-release

# inside the clean Debian VM, after copying the release and lab-only test PKI
sudo bash /tmp/hk-debian-install.sh --release /tmp/hk-debian-release
sudo systemctl start hikablue-engine.service
sudo systemctl start hikablue-agent.service
stat -c '%a %U %G %n' /run/hikablue-engine/*.sock
sudo -u hikablue-agent python3 /tmp/hk-debian-socket-query.py
```

Observed output:

```text
debian=Debian GNU/Linux 12 (bookworm)
kernel=6.1.0-56-cloud-amd64
release=4aa44d137717
engine=active
agent=active
660 hikablue-engine hikablue-agent /run/hikablue-engine/control.sock
660 hikablue-engine hikablue-agent /run/hikablue-engine/engine.sock
GetMetrics:ok:{"dropped_pkts": 13, "epochs": [], "invalid_inner_dst_total": 0, "invalid_inner_src_total": 0, "peers": 0, "rotations_total": 0, "rx_bytes": 0, "rx_pkts": 0, "rx_unknown_sender_index_total": 0, "tx_bytes": 0, "tx_pkts": 0, "uptime_secs": 5}
GetSessions:ok:{"backend_instance_id": "5d7e9cc16021a2ab27a76f15916a9832", "epochs": [], "lab_session_id": null, "peers": [], "revocations": 0}
```

The raw captured output is stored beside this report as `milestone-5-review-fixes-systemd.txt`. The agent deliberately pointed at `127.0.0.1:1` because this validation targeted service account/socket startup, not control-plane availability; it remained active while retrying the orchestrator. The control-plane unit was not started in this VM.

## Explicitly not validated here

- PostgreSQL rollback/backup, certificate issuance by a real CA process, secret rotation, production routing, NAT, DNS, full tunneling, IPv6 policy, and WireGuard remain outside this milestone.
- The Debian systemd validation did not start the control-plane unit or perform end-to-end provisioning; those paths were covered by the isolated laboratory gate on the Ubuntu host.
