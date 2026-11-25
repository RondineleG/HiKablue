# PR #23 — Milestone 4 validation-gate report

## Identification

| Field | Value |
|---|---|
| Repository | RondineleG/HiKablue |
| PR | [#23 — fix: restore milestone 4 validation gate](https://github.com/RondineleG/HiKablue/pull/23) |
| Branch | `fix/milestone-4-validation-gate` → `main` |
| Base SHA | `0be5f1172b715b7b9b0e1ebc02c9bc61a403a1ab` (`0be5f11`, PR #21 merge) |
| Validated SHA | `0b9f4b76250da5a2df247c98a1165d73945d2854` |
| Date | 2026-11-25 UTC |
| OS | Ubuntu 22.04.5 LTS, kernel `6.8.0-1060-aws` |
| Toolchain | Go 1.18.1 · Rust 1.99.1 · .NET SDK 10.0.300 · ShellCheck 0.8.0 |
| Privileges | passwordless sudo; `/dev/net/tun`; `ip netns`; PostgreSQL 14 binaries installed for the lab |

The validation VM is a disposable sandbox with outbound connectivity, not an air-gapped host. The laboratory traffic itself was confined to repository-created network namespaces, loopback, and lab-only RFC1918 addresses (`192.168.50/57/100.x`, `10.80.x.x`); no third-party or production endpoint was targeted.

## Fixes validated

1. **EF Core migration compilation**: removed duplicate `DbContext`/`Migration` attributes from the migration implementation file. The metadata remains in the `.Designer.cs` partial. `CS0579` is gone.
2. **Migrated session-token replay**: `IssueSessionTokenAsync` now marks `SessionTokenHash` as modified so the hash is persisted even when the scoped EF context already tracks a stale row. The previously failing test `MigratedSessionReplayIssuesOwnerTokenAndStoresOnlyHash` passes.
3. **Go 1.18.1 test hermeticity**: `TestServeRejectsCorruptExpiryState` explicitly chmods its state directory to `0700` instead of inheriting umask-dependent `t.TempDir()` permissions.
4. **Session lab safety fixture**: restored the executable bit, made the owned-process fixture carry the run marker expected by cleanup, waited for process termination, serialized overlapping-scope cleanup, and released worker processes on failure paths.

## Gate results

| Command | Exit | Result |
|---|---:|---|
| `bash tests/checks.sh` | 0 | `PASSED (partial — 1 check skipped)`: 12 passed, 0 failed; only the privileged lab step skipped as designed |
| `bash tests/checks.sh --lab` | 0 | `ALL PASSED`: 19 passed, 0 failed, 0 skipped |

## Build, tests, and coverage by component

Observed in the `--lab` full gate:

| Component | Build | Tests | Coverage |
|---|---|---|---|
| Rust `engine-rust` | PASS — release build | PASS — 114 passed, 0 failed | PASS — 91.79% lines (test code and justified lab-only spans excluded) |
| Go `agent-go` | PASS — test compilation/package build | PASS — all packages passed | PASS — 90.1% statements |
| C# `control-dotnet` | PASS | PASS — 155 passed, 0 failed | PASS — 95.1% lines, 82.2% branches |

The required floor is 90% line/statement coverage per component. All three components measured above the floor in the official `COVERAGE_GATE=1` run.

## Milestone 4 two-client laboratory

All three scenarios passed end to end at the validated SHA. In each scenario, both clients first received distinct virtual IPs and the expected HTTP body `hikablue-m4-ok` through the tunnel. The pre-tunnel direct-path probe covered client A; client B's direct path was not separately probed.

| Scenario | Result | Observed acceptance behavior |
|---|---|---|
| `tests/lab/session-e2e.sh` — normal revocation | PASS | A lost access after revocation; B continued without a failed response; the node ACK completed revocation; only then was A's IP lease released. Final metrics: `peers=1`. |
| `tests/lab/session-e2e.sh --drop` — gRPC interruption/reconciliation | PASS | `RevokePeer` was sent before interruption; A stayed `revoking` with its lease reserved until ACK; after gRPC restoration, reconciliation completed revocation, released A's lease, and B survived with zero failed responses. |
| `tests/lab/session-e2e.sh --expire-restart` — local expiry across agent restart | PASS | With gRPC down and the agent restarted while the engine stayed up, the persisted deadline expired A locally; A was not resurrected during reconciliation; the removal ACK released A's lease; B survived with zero failed responses. |

Other laboratory evidence:

- `netns-ping.sh`: PASS — 3/3 ICMP replies in each direction, 0% packet loss.
- `telemetry-demo.sh`: PASS — metrics reached the control plane, engine stop was reported as unhealthy, and restart recovered metrics collection.
- `telemetry-demo-safety.test.sh`: PASS — 2 cases.
- `session-e2e-safety.test.sh`: PASS — 5 cases. The word `Terminated` in the log is the expected job-control message from killing the owned fixture process; the process-removal assertion passed.
- Lab PKI tests: PASS — 18 cases.
- Book parity EN/ES/PT-BR: PASS.
- Coverage collector self-test: PASS.

## Environment prerequisite found during the base-SHA validation

The initial VM lacked `/usr/lib/postgresql/*/bin/initdb`. PostgreSQL was installed on the validation VM before the successful run. This is an environment prerequisite, not a repository code change; it should remain captured in the development environment blueprint so future validation VMs start with the lab dependencies.

## Remaining unvalidated scope

No Milestone 4 gate check remained skipped or failing at the validated SHA. Client B's pre-tunnel direct-path isolation was not separately probed. This report does not validate Milestones 5–6, production routing, deployment automation, non-lab traffic, or external network reachability.
