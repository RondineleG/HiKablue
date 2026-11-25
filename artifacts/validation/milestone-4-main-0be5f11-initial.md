# HiKablue Milestone 4 validation report

Date: 2026-11-25  
Environment: throwaway Ubuntu validation VM, **Ubuntu 22.04.5 LTS**, kernel `6.8.0-1060-aws`  
Repository: `https://github.com/RondineleG/HiKablue`  
Checkout: clean clone at `/home/ubuntu/hikablue-validation`, branch `main`  
Tested SHA: **`0be5f1172b715b7b9b0e1ebc02c9bc61a403a1ab`**  
Merge confirmation: `origin/main` is exactly that SHA; it is the merge commit `Merge pull request #21 from RondineleG/feat/milestone-4-e2e-sessions` (`0be5f11`). `git merge-base --is-ancestor 0be5f11 HEAD` returned 0. The working tree remained clean; no repository files were modified.

## Verdict

**Milestone 4 is not validated at the tested SHA.** The unprivileged gate and the privileged lab gate both failed. Rust passed its build, tests, and coverage gate. Go built but its official test/coverage gate failed under the VM's normal `umask 0022`. C#/.NET did not compile, which prevented the control-plane tests, coverage, telemetry demo, and all three two-client Milestone 4 laboratory scenarios from reaching their behavioral assertions.

## Toolchain pins and observed versions

| Component | Pinned | Observed | Status |
|---|---:|---:|---|
| Go | `1.18.1` (`agent-go/.go-version`) | `go1.18.1 linux/amd64` | matched |
| Rust | `1.99.1` (`rust-toolchain.toml`) | `rustc 1.99.1` / `cargo 1.99.1` | matched |
| .NET SDK | `10.0.300` (`global.json`) | `10.0.300` | matched |

Other observed prerequisites:

- Passwordless `sudo`: available.
- `/dev/net/tun`: present.
- `ip netns`: available as root.
- ShellCheck: `0.8.0`.
- PostgreSQL server binaries: **missing** (`/usr/lib/postgresql/*/bin/initdb` did not exist). The Milestone 4 scenarios did not reach this check because the build failed first; after the compile failure is fixed, PostgreSQL binaries must be installed or the scenarios will stop at `postgresql binaries not installed`.
- The VM is a separate validation sandbox but does have outbound network connectivity, so it is not air-gapped. The repository laboratory itself is namespace-scoped: inspected endpoints were loopback plus lab-only RFC1918 addresses (`192.168.50/57/100.x`, `10.80.1.x`). Only repository-generated lab traffic was run.

## Gate results

### `bash tests/checks.sh`

Exit: **1 — FAILED**  
Summary: **9 passed, 3 failed, 1 skipped**

Failed:

1. `go: gofmt, vet, test`
2. `dotnet: build + test vpn-control`
3. `coverage >= 90% (rust/go/c#, TEST-02)`

Skipped:

- `lab integration: ip netns ping + telemetry demo` — expected without `--lab`.

### `bash tests/checks.sh --lab`

Exit: **1 — FAILED**  
Summary: **11 passed, 8 failed, 0 skipped**

Failed:

1. `go: gofmt, vet, test`
2. `dotnet: build + test vpn-control`
3. `coverage >= 90% (rust/go/c#, TEST-02)`
4. `lab integration: telemetry demo (end to end)`
5. `lab integration: milestone 4 sessions normal revocation`
6. `lab integration: milestone 4 sessions gRPC reconciliation`
7. `lab integration: milestone 4 local expiry across agent restart`
8. `lab integration: session resource ownership regressions (sudo)`

Passed lab-adjacent checks:

- `lab integration: ip netns ping (sudo)`: passed. Both directions delivered 3/3 ICMP echo replies with 0% packet loss between `10.80.1.1` and `10.80.1.2` inside network namespaces.
- `lab integration: telemetry demo preserves foreign resources (sudo)`: passed 2 cases.
- Lab PKI issuance tests: passed 18 cases.
- Coverage collector self-test: passed.

Note: because the checkout was `main` itself, diff-scoped checks used `BASE_SHA=HEAD` and therefore had no PR diff to inspect. The compile, unit-test, coverage, and lab steps still ran normally.

## Component results

| Component | Build | Tests | Coverage |
|---|---|---|---|
| Rust `engine-rust` | **PASS** — `cargo build --release`, exit 0 | **PASS** — `cargo test`, 114 passed, 0 failed | **PASS** — 91.67% lines, excluding test code and justified lab-only spans; above the 90% floor |
| Go `agent-go` | **PASS** — `go build ./...`, exit 0 | **FAIL in official gate** — see below | **FAIL in official gate** because `go test -coverprofile` failed; diagnostic restrictive-umask run measured 90.1% statements |
| C# `control-dotnet` | **FAIL** — CS0579 duplicate `Migration` attribute | **Not reached** because the project does not compile | **Not measured** because the project does not compile |

### Rust details

Commands:

```sh
(cd engine-rust && cargo build --release)
(cd engine-rust && cargo test)
COVERAGE_COMPONENTS=rust bash tests/coverage.sh
```

Observed:

- Release build: exit 0.
- Tests: `114 passed; 0 failed`.
- Coverage: `91.67%` lines in the selective run, above `COVERAGE_MIN=90`.

### Go details

Commands:

```sh
(cd agent-go && go build ./...)
(cd agent-go && go test ./...)
COVERAGE_COMPONENTS=go bash tests/coverage.sh
```

Observed under the VM's normal `umask 0022`:

- Build: exit 0.
- Tests: exit 1.
- Failing test: `TestServeRejectsCorruptExpiryState` in `agent-go/cmd/vpn-agent/serve_test.go`.
- Observed error:

```text
serve_test.go:267: exit 1 stderr "vpn-agent: expiry state directory \"/tmp/TestServeRejectsCorruptExpiryState.../001\" must be private (0700)\n"
```

The test expected the agent to reach the corrupt-JSON decode path, but the agent exited earlier because the state directory was not mode `0700`. With this pinned Go 1.18.1 toolchain, `t.TempDir()`'s numbered child directory is created as `0755` under `umask 0022`; a diagnostic run with `umask 077` made the directory `0700` and then all Go tests passed. That diagnostic also measured **90.1% statement coverage**. This does not make the official gate pass: `tests/checks.sh` does not set a restrictive umask, and the observed default run failed.

### C#/.NET details

Commands:

```sh
dotnet build control-dotnet/VpnControl/VpnControl.csproj -v minimal --nologo
dotnet test control-dotnet/VpnControl.Tests -v normal --nologo
dotnet test control-dotnet/VpnControl.Tests/VpnControl.Tests.csproj --collect:"XPlat Code Coverage" --settings control-dotnet/coverlet.runsettings -v minimal
```

Observed error in build, test, and coverage attempts:

```text
control-dotnet/VpnControl/Data/Migrations/20261125001000_session-token-key-removal.Designer.cs(16,6): error CS0579: Duplicate 'Migration' attribute
```

The duplicate attributes are present in both:

- `control-dotnet/VpnControl/Data/Migrations/20261125001000_session-token-key-removal.cs` lines 9-10
- `control-dotnet/VpnControl/Data/Migrations/20261125001000_session-token-key-removal.Designer.cs` lines 15-16

Because compilation failed, no C# unit test or coverage percentage was produced.

## Milestone 4 two-client laboratory scenarios

The scenario commands were run through `bash tests/checks.sh --lab`. All three stopped in their initial `== build` stage, before session creation, client startup, revocation, interruption, or expiry behavior could be exercised. The immediate blocker was the C# compile error above. PostgreSQL binaries were also absent as an independent environment prerequisite, but the scripts did not reach that check.

| Scenario | Command inside gate | Observed result | Behavioral validation status |
|---|---|---|---|
| Revocation without interrupting the other client | `tests/lab/session-e2e.sh` | **FAIL** during build; no two-client traffic or revocation assertions reached | **Unvalidated** |
| Reconciliation after gRPC interruption | `tests/lab/session-e2e.sh --drop` | **FAIL** during build; no gRPC interruption or reconciliation assertions reached | **Unvalidated** |
| Local expiry after agent restart | `tests/lab/session-e2e.sh --expire-restart` | **FAIL** during build; no restart or expiry assertions reached | **Unvalidated** |

### Additional lab regression

The official step `sudo tests/lab/session-e2e-safety.test.sh` failed immediately with `command not found` because the file is not executable (`-rw-r--r--`), unlike `telemetry-demo-safety.test.sh`. Running the same content explicitly as `sudo bash tests/lab/session-e2e-safety.test.sh` bypassed only the invocation-mode issue and observed:

```text
PASS: partial creation cleans only created namespaces
PASS: preexisting namespace survives scope cleanup
PASS: overlapping scopes preserve each other's resources
PASS: partially created root veth pair is owned and removed
FAIL: owned privileged process survived cleanup
```

The manually spawned leftover test process was cleaned up after observation. This safety regression is separate from the three Milestone 4 scenarios but is part of the `--lab` gate failure.

## Unvalidated scope

The following Milestone 4 acceptance behavior remains unvalidated at this SHA:

1. End-to-end REST → PostgreSQL outbox → mTLS gRPC `NodeChannel` → Go agent → Rust control-socket provisioning.
2. Two simultaneous client sessions receiving distinct tunnel IPs and application connectivity through the HTTP service namespace.
3. Revoking client A while client B continues without failed responses.
4. Lease retention in `revoking` and release only after the fenced removal ACK.
5. gRPC interruption and later `SyncState` reconciliation without resurrecting client A or interrupting client B.
6. Persisted local expiry across an agent restart while the engine remains running.
7. End-to-end telemetry demo behavior dependent on the C# control-plane binary.
8. C# unit tests and C# coverage.
9. Go official-gate tests/coverage under the repository's default invocation environment.

## Conclusion

The tested merge commit is confirmed, the Rust engine meets the observed build/test/coverage gate, and the low-level TUN/netns ping laboratory passes. However, Milestone 4 end-to-end sessions are **not validated** at `0be5f1172b715b7b9b0e1ebc02c9bc61a403a1ab` because C# does not compile and the Go gate fails under the default environment. Any code corrections should be made in a separate PR, as requested.
