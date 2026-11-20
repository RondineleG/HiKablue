# PR #14 — Final Validation Report

## Identification

| Field | Value |
|---|---|
| Repository | RondineleG/HiKablue |
| PR | [#14 — feat: milestone 4 phase 2 — aead data-plane crypto and replay protection](https://github.com/RondineleG/HiKablue/pull/14) |
| Branch | `feat/data-plane-crypto` → `main` |
| Reference SHA (initial) | `55a2bf2063f08023fcfdad6ee2a0cf1c2ba525c7` |
| Validated SHA (final) | `048c67da866b43d60b96cf3ffe83384b3bbfc8cd` |
| Date | 2026-11-20 (UTC-3) |
| OS | Linux 6.8.0-1060-aws (Debian lab VM, isolated/authorized) |
| Toolchain | rustc 1.99.1 · go 1.18.1 · .NET 10.0.300 |

Commits added after the reference SHA:

- `aa74c13` — fix: make replay acceptance atomic after authentication
- `950a771` — fix: symmetric inner vip ingress and outer-family mtu limits
- `048c67d` — docs: cover client peer flag in crypto chapter

## Corrections

### 1 — Replay-window atomicity (HIGH, blocking)

- **File/line**: `engine-rust/src/crypto/session.rs:181-189`
- **Root cause**: `open()` did `check()` under lock → released lock → AEAD decrypt → `update()` under a new lock. Two concurrent calls could both pass `check()` and both be accepted.
- **Fix**: after `decrypt_in_place_detached` succeeds, the code locks `rx_replay` once more and runs `check(nonce)` then `update(nonce)` inside the same critical section. A second authenticated copy that raced through the preliminary check re-fails as `ReplayError::Duplicate`. The crypto operation stays outside the lock.
- **Regression test**: `concurrent_open_accepts_each_nonce_once` — two threads released simultaneously through a `std::sync::Barrier` (no sleeps) `open()` clones of the same wire datagram; exactly one returns `Ok`, the other `Err(Replay(Duplicate))`. Eight distinct sealed datagrams opened from parallel threads all succeed (no deadlock). Boundary/leap/tamper/rekey cases covered by the pre-existing window and session tests.
- **Proven result**: `cargo test` 90/90 PASS.

### 2 — Symmetric inner-IP validation on the client (MEDIUM)

- **Files**: `engine-rust/src/forward.rs:88-92, 521-531`; `engine-rust/src/cli.rs:60` (`--peer`); `engine-rust/src/main.rs` (`peer_vip: Some(args.peer)`); `tests/lab/netns-ping.sh:88`
- **Root cause**: the client only validated the inner destination; an authenticated peer could inject packets with a forged inner source — authentication ≠ authorization of inner addresses.
- **Fix**: `ClientArgs --peer <server-vip>` is now required; `Config::validate()` rejects client mode without `peer_vip`. Post-decrypt ingress on the client drops packets whose inner src ≠ peer VIP (`invalid_inner_src_total`) or dst ≠ local VIP (`invalid_inner_dst_total`), before the TUN write. `is_server` is now derived from `remote.is_none()` (clients also carry `peer_vip`).
- **Regression tests**: `client_scenario` drops a datagram with `src=PEER_VIP` (foreign source) in addition to the existing foreign-dst/malformed/oversized cases; `client_requires_peer_vip` covers config rejection. `crypto_client_scenario` proves a sealed forged-source datagram never reaches TUN.
- **Proven result**: 90/90 PASS; encrypted netns ping still lands both directions.

### 3 — MTU cap per outer address family (MEDIUM)

- **File/line**: `engine-rust/src/forward.rs:41` (`MAX_MTU_ENCRYPTED_V6 = 1500 - 78 = 1422`), `84` (family mismatch rejection), `126-141` (family-aware cap), `146-152` (`outer_is_ipv6`)
- **Root cause**: the keyed MTU cap assumed IPv4 (58B overhead → 1442) regardless of socket family; IPv6 (78B) allows only 1422.
- **Fix**: `outer_is_ipv6()` resolves the effective family — client: the connected remote; server: the bind (`[::]` is dual-stack, so the conservative 78B applies). `validate()` rejects keyed MTU above the family cap, plus any bind/remote family mismatch. IPv4-mapped IPv6 peers hit the conservative IPv6 bound (documented in the `outer_is_ipv6` doc-comment). Values assume a 1500-byte path with no extra encapsulation, per spec.
- **Regression tests**: `keyed_mtu_limit_follows_outer_family` — v4: 1442 pass / 1443 fail; v6: 1422 pass / 1423 fail. `client_rejects_mismatched_bind_and_remote_family` — v4 bind + v6 remote rejected; v6+v6 accepted.
- **Proven result**: 90/90 PASS.

### 4 — PR description & docs (LOW)

- **Fix**: PR body rewritten to the authoritative format (byte 0 = version nibble + type; byte 1 reserved; bytes 2–5 `session_id` BE; bytes 6–13 counter BE; AEAD IV = 4×0x00 + counter LE; 16B tag). Chapter 4 in all three editions now documents the client's `--peer` flag in the symmetric-ingress section; the static-lab-key limitation and restart nonce-reuse caveat are preserved.

## Complementary crypto re-review (section 7)

- Per-direction KDF `BLAKE2s-256("hikablue-session-key-v1" ‖ index_be ‖ psk)`; indexes must be nonzero and distinct (enforced in `validate()`).
- Nonce uniqueness: `AtomicU64` tx counter, latch at `u64::MAX − 65536` → `NonceExhaustionNeedsRekey`; rx never seals. `Zeroizing` on the PSK input and per-direction keys.
- AAD = exact 14-byte header; tampering fails the tag and never touches the window (test: `header_tamper_fails_auth_and_leaves_window_untouched`).
- Atomicity: re-check + mark in one critical section (fix 1).
- Invalid messages: short/malformed datagrams rejected by `parse_header` before decrypt; foreign endpoints dropped before decrypt on server (pinned) and always on client (connected socket).
- `sender_index`/`peer_index` stamped and verified against the session; keepalives sealed + authenticated, never reach TUN.
- Buffers: `MAX_MTU` 2018 bound on plaintext path, family-capped keyed path; no allocation in seal/open hot path (in-place).
- Restart behavior: counter/window reset with the same PSK replays the key stream — **documented lab limitation**; real provisioning (`key_epoch`) is a later phase. No keys or key material appear in logs (verified: only VIPs, endpoints, metric names are logged).

## Validation matrix

| Check | Result | Evidence |
|---|---|---|
| Atomic anti-replay | PASS | `concurrent_open_accepts_each_nonce_once` (Barrier-synchronized, exactly 1 success) |
| AEAD authentication | PASS | RFC 8439 vector; `header_tamper_fails_auth_and_leaves_window_untouched` |
| Nonce exhaustion | PASS | `counter_exhaustion_latches` |
| Client inner-src validation | PASS | `client_scenario` foreign-src drop; `crypto_client_scenario` |
| Inner-dst validation | PASS | `client_scenario`/`server_scenario` foreign-dst drops |
| MTU IPv4 (1442/1443) | PASS | `keyed_mtu_limit_follows_outer_family` |
| MTU IPv6 (1422/1423) | PASS | `keyed_mtu_limit_follows_outer_family` |
| Sealed keepalive | PASS | `crypto_client_scenario`/`crypto_server_scenario` (0x02 on wire, never to TUN) |
| Encrypted bidirectional ping | PASS | `netns-ping.sh` — 3/3 ICMP replies each direction |
| Rust ≥ 90% | PASS | **95.61%** lines (excl. test code + lab-only spans) |
| Go ≥ 90% | PASS | **91.0%** statements |
| C# ≥ 90% | PASS | **95.9%** lines |
| Book EN/ES/PT-BR | PASS | parity check green; ch04 in all three editions |
| Full gate | PASS | `checks.sh: ALL PASSED` — 15/15, 0 skipped |
| Devin Review | PENDING | new round running on `048c67d` |

## Commands & exit codes

| Command | Exit |
|---|---|
| `cargo test` (engine-rust) | 0 — 90 passed, 0 failed |
| `cargo clippy --all-targets -- -D warnings` | 0 |
| `cargo fmt` | 0 (no diff) |
| `BRANCH_NAME=feat/data-plane-crypto bash tests/checks.sh --lab` | 0 — `checks.sh: ALL PASSED` (run twice: pre- and post-push state identical) |

Privileges: the gate runs unprivileged; `sudo` is used only inside the lab scripts for `ip netns`/TUN steps.

## Residual risks

- **Static lab PSK is not production provisioning**; restarting a session with the same `--key` and reset counters reuses key/nonce pairs. Real key delivery via `ProvisionPeer`/`key_epoch` remains future phase work (escalated SEC-2 finding on the PR).
- Devin Review verdict for HEAD `048c67d` was still running at report time; prior rounds (11 findings total) all triaged.

## Final verdict

**APROVADO PARA MERGE** — every mandatory check passed on the final SHA `048c67d`, with no blocking failures.
