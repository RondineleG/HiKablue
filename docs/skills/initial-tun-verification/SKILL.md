---
name: initial-tun-verification
description: Use when implementing or verifying HiKablue's initial raw IPv4 TUN/UDP task, its tests, or Chapter 1 in the three ebook editions.
slug: initial-tun-verification
category: workflow
scope: project
target_clis: all
---

# Initial TUN Task Verification

This skill applies only when the user requests the initial TUN implementation task. Read `.agc/IMPLEMENTATION.md`, `.agc/RULES.md`, and the relevant sections of `vpn-debian-specification.md` first. This task is distinct from the telemetry milestone; do not silently reorder the roadmap.

## Verification sequence

1. Map each acceptance requirement to a code location, focused test, and Chapter 1 section.
2. Run only build/test commands that exist in the checkout. Record the exact command, environment, exit result, and warnings; report missing manifests or tools as unverified rather than passed.
3. Test malformed/truncated/oversized packet rejection and peer selection with deterministic tests.
4. Run live forwarding, setup/cleanup, or signal tests only in an authorized isolated lab, following `vpn-lab-safety`.
5. Report compilation, bidirectional packet forwarding, and application-level connectivity as separate results. Never infer one from another.
6. Update the English, Spanish, and Brazilian Portuguese Chapter 1 editions in the same change set. Check stable chapter IDs, commands, expected outputs, revision notes, and the requirement-to-test map for parity.

## Evidence report

For each check, record:

- what ran and on which Debian version or host;
- the observed result, not only the expected result;
- what could not be tested and why;
- exact reproduction steps for the unverified behavior.

Do not report network behavior as verified unless the test actually ran in the authorized lab.
