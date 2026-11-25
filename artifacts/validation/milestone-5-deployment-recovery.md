# Milestone 5 validation report — deployment and recovery

- Repository: `RondineleG/HiKablue`
- Branch: `feat/milestone-5-deployment-recovery`
- Validated tree: commit `32e16a8272e4d43c81f5a56e082431a5236e113f` plus the documentation-only working-tree edit that updates the recorded restart metrics from the same observed run; no code changed after the gate.
- Host: Ubuntu 22.04.5 LTS, kernel 6.8.0-1060-aws
- Toolchains observed: Go 1.18.1, Rust 1.99.1, .NET SDK 10.0.300, ShellCheck 0.8.0
- Traffic boundary: only repository-generated isolated-lab traffic in network namespaces and loopback/RFC1918 test addresses.

## Full gate

Command:

```bash
bash tests/checks.sh --lab
```

Observed final summary:

```text
passed: 21  failed: 0  skipped: 0
checks.sh: ALL PASSED
```

Coverage gate (all at or above the 90% floor):

| Component | Observed coverage |
|---|---:|
| Rust lines | 91.67% |
| Go statements | 90.7% |
| C# lines | 95.3% |

Unit/build results inside the same gate:

- Rust: 114 tests passed; fmt, clippy, tests, and release build passed.
- Go: all package tests passed; gofmt and vet passed.
- .NET: 156 tests passed; build passed.
- Book parity across English, Spanish, and Brazilian Portuguese passed.
- Shell syntax and ShellCheck passed, including extensionless service wrappers.

## Laboratory scenarios

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

Milestone-5 observed lines:

```text
OBSERVED: agent detected the new backend instance and exited for supervised restart
OBSERVED: confirmed empty-state reconciliation failed pre-restart sessions and released leases
OBSERVED: pre-restart keys cannot carry traffic on the new backend instance
OBSERVED: abrupt client termination removed tun0 and rolled back its connected tunnel route
OBSERVED: a fresh session with fresh keys restored service connectivity after engine restart
session-e2e (engine-restart): PASS
```

Final engine metrics in that run:

```json
{"rx_bytes":588,"rx_pkts":5,"tx_bytes":619,"tx_pkts":5,"dropped_pkts":2,"peers":1,"uptime_secs":3}
```

## Debian installation evidence

Offline acceptance command:

```bash
bash tests/deploy/install-rollback.test.sh
```

Observed:

```text
PASS: installation is repeatable, config is preserved, upgrade and rollback switch releases
```

Real release build and staged root install commands:

```bash
deploy/debian/build-release.sh --force --output /tmp/hk-m5-release-final
deploy/debian/install.sh --release /tmp/hk-m5-release-final --root /tmp/hk-m5-install-final --no-systemctl
deploy/debian/install.sh --release /tmp/hk-m5-release-final --root /tmp/hk-m5-install-final --no-systemctl
```

Observed:

```text
release written: /tmp/hk-m5-release-final
installed release 32e16a8272e4
release 32e16a8272e4 already installed unchanged
```

The release contained the Rust engine, Go agent, published .NET control plane, service wrappers, systemd units, and secret-free sample configuration.

## Explicitly not validated here

- systemd units were statically inspected and installed into a staged root, but not started on a real systemd Debian host.
- PostgreSQL rollback/backup, certificate issuance, secret rotation, production routing, NAT, DNS, full tunneling, IPv6 policy, and WireGuard remain deferred.
- Automatic rekey/reprovisioning of the same client session after backend restart is intentionally absent; a fresh API session with fresh keys is required.
