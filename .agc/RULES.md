# Project Rules

These rules apply to all implementation and verification work. The user controls requested scope; the architecture specification controls protocol and topology; this file controls project safety and engineering workflow. Do not silently resolve conflicts among them.

Each rule states how compliance is checked. A manual review is not an automated test; never describe it as one.

## Safety — blocking rules

### LAB-01 — Isolated test traffic only

Raw UDP tunnel work and packet-capture exercises MUST use test traffic in an isolated, authorized lab. Do not direct experimental tunnel traffic at third-party or production systems.

**Verification:** Confirm the lab endpoints and scope before any live network test; inspect commands and documentation for unintended external targets.

### LAB-02 — Reversible, scoped network changes

Changes to routes, forwarding, NAT, firewall, or TCP MSS MUST be limited to the project-owned lab configuration, documented, idempotent where applicable, and paired with cleanup/restore steps. Full tunneling MUST preserve a route to the UDP endpoint through the original path. Do not run privileged network changes without an explicit, approved lab task.

**Verification:** Review setup and cleanup paths and, when implemented, repeat setup and test cleanup in the isolated lab. Verify route restoration before claiming full-tunnel checks passed.

### SEC-01 — No credential leakage

Do not commit secrets or include credentials in sample configurations, logs, or packet-capture artifacts. Use test-only values in examples.

**Verification:** Inspect changed files and generated artifacts; use repository secret scanning when available.

## Engineering — required rules

### ARCH-01 — Preserve the architecture source of truth

Follow `vpn-debian-specification.md` for protocol and topology. The repository's initial TUN task maps to the TUN milestone; do not conflate it with the separate telemetry milestone or silently reorder milestones. If a request requires resolving an existing roadmap conflict, raise it before changing the design.

**Verification:** Review the requested scope against the specification's milestone table and the relevant acceptance criteria.

### TEST-01 — Verify the smallest observable behavior

Add or update focused tests with code changes. Prefer deterministic unit tests for packet validation, peer selection, and state transitions; use live network checks only in the authorized lab. Run only commands that exist in the checkout and identify missing prerequisites explicitly.

**Verification:** Record exact commands, environment, and observed outcomes; distinguish compilation, forwarding, and application connectivity.

### DOC-01 — Keep code and ebook in sync

Every implementation milestone MUST update the complete English, Spanish, and Brazilian Portuguese editions of **The Journey of my VPN** in the same change set. Keep editable sources in the repository, provide directly readable editions with tables of contents and stable chapter IDs, and make any generated EPUB, PDF, or website reproducible from those sources. Keep chapter IDs, requirements, code/test references, commands, expected results, revision notes, and exercises aligned. Preserve prior reasoning when an approach is superseded; link revisions in all editions and pin historical commands to the release or commit they describe.

**Verification:** Add and run a chapter-parity check with the first chapter; review all three editions against the code and tests for each later milestone.

### DOC-02 — Teach and record evidence

For each implemented requirement, map it to code, tests, and chapter sections. Explain the problem, prerequisites, relevant packet/control flow, implementation choices, tradeoffs, and failure cases; walk through relevant code and include reproducible steps, expected outcomes, troubleshooting, and at least one hands-on exercise with a hint and a way to check the answer. Record deferrals and distinguish expected behavior from observed results. If a prerequisite is unavailable, run independent checks where possible and give exact commands to reproduce the missing checks.

**Verification:** Review the milestone's code/test/book map and reproduce documented commands where prerequisites are available.

### CHANGE-01 — Honor the requested scope

Treat analysis-only requests as read-only. Keep edits limited to the approved task; do not add speculative scaffolds, commands, or dependencies. If an additional file is necessary, explain why in the change summary.

**Verification:** Review `git diff --name-only` and the final diff against the stated request.

### GIT-01 — Use repository conventions

Name branches `feat/<slug>` for additive work and `fix/<slug>` for bug fixes. Use Conventional Commits and the Git identity configured for the repository. Do not add AI attribution trailers.

**Verification:** The `Validate commits` pull-request check verifies the branch prefix and each non-merge commit subject; use the configured Git identity without per-commit overrides.
