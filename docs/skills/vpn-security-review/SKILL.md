---
name: vpn-security-review
description: Use when reviewing HiKablue code, configuration, dependencies, or changes that affect packet handling, peer identity, keys, TUN privileges, routing, firewall rules, secrets, or session lifecycle.
slug: vpn-security-review
category: security
scope: project
target_clis: all
---

# VPN Security Review

Use this as a defensive review workflow for HiKablue. It supplements `.agc/RULES.md` and `.agc/CODE_REVIEW.md`; it does not authorize testing outside an isolated, authorized lab.

## Review sequence

1. **Establish scope.** Read the requested diff, `AGENTS.md`, `.agc/RULES.md`, and relevant sections of `vpn-debian-specification.md`. Identify assets, trust boundaries, affected milestone, and any unverified assumptions.
2. **Check packet boundaries.** Review parsing, length and MTU limits, source/destination validation, malformed/truncated input handling, replay defenses, and failure behavior. Treat structural or source-IP checks as validation, not authentication.
3. **Check identity and key lifecycle.** Trace key generation, provisioning, storage, use, rotation, restart, revocation, and errors. Check nonce/counter uniqueness and replay behavior against the selected cryptographic construction. If the specification leaves a lifecycle decision open, report the gap; do not invent a protocol.
4. **Check privilege and network effects.** Review who opens TUN, changes routes, enables forwarding, installs NAT/firewall/MSS rules, and cleans them up. Confirm changes are scoped and reversible and that full tunneling preserves the transport endpoint's original route.
5. **Check secrets and dependencies.** Inspect sample configuration, logs, test captures, lockfiles, dependency sources, and license/advisory status where manifests and suitable tools exist. Do not claim a scan ran unless it did; do not add a scanner or change versions as part of review without approval.
6. **Check lifecycle and resilience.** Review cancellation, partial failure, reconnect, restart, idempotency, revocation, and cleanup paths for stale access or inconsistent state.
7. **Check evidence.** Match tests and documentation to the behavior claimed. Run only safe, available checks; any live network validation must follow `vpn-lab-safety`.

## Findings format

For each finding, report severity, affected file/area, impact, concrete evidence, and a focused remediation. Separate confirmed defects from design questions and unverified behavior. If there are no findings, state the review scope and checks that were actually performed.

Do not include exploit payloads, unrelated traffic captures, secrets, or unsupported compliance claims in the report.
