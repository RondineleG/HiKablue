---
name: vpn-lab-safety
description: Use when designing, running, reviewing, or documenting TUN/UDP tests, packet captures, routes, forwarding, NAT, firewall, or TCP MSS changes in HiKablue.
slug: vpn-lab-safety
category: workflow
scope: project
target_clis: all
---

# VPN Lab Safety

Apply this skill before any live network test or network-mutating operation. `.agc/RULES.md` remains authoritative.

## Before a test

1. Confirm the requested test is within an isolated, authorized lab and uses test traffic only. If the target or authorization is unclear, do not run it.
2. Identify both lab endpoints, the original route to the UDP endpoint, interfaces, CIDRs, and the exact routes/firewall settings the test may change.
3. Identify required privileges and define cleanup/restore steps before setup. Do not broaden privileges or mutate host networking as a convenience.
4. Keep captures and logs limited to lab traffic; do not collect unrelated user traffic or credentials.

## During and after a test

- Treat peer-IP/packet-structure checks as input validation, not cryptographic authentication.
- Keep split tunneling as the default. If full tunneling is explicitly in scope, preserve and verify the UDP endpoint's path through the original route.
- Apply only project-owned, scoped network rules. Setup should be repeatable where required; cleanup must remove only rules created by the project.
- Verify restoration and report what actually ran. Separate packet forwarding from application-level connectivity.
- Do not invent commands: use only scripts and targets present in the checkout.

## Stop conditions

Stop and ask before proceeding if the test would use a production/third-party target, alter unrelated routes or firewall state, expose non-test traffic, or lacks a safe cleanup path.
