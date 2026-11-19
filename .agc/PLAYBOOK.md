# Change Playbook

Use this sequence for non-trivial work. Keep each stage proportional to the change; do not create a plan or tracking artifact solely to satisfy ceremony.

## 1. Review

- Check repository status and the user's exact scope.
- Read root `AGENTS.md`, this playbook, `.agc/RULES.md`, and the relevant architecture sections.
- Keep an analysis-only request read-only. Record relevant gaps without implementing them.

## 2. Specify

- State the behavior or document outcome requested.
- Map requirements to affected files, acceptance criteria, and the smallest useful checks.
- For changes affecting routing, privileges, protocol, or milestone order, surface unresolved design conflicts before editing.
- Choose the layout, dependencies, testing tools, and publishing workflow that fit the project. Explain decisions that affect correctness, routing, privileges, or repeatability.

## 3. Plan

- Share a short plan before editing.
- Reuse existing structure and dependencies. Avoid speculative scaffolds and duplicate task tracking.
- For network work, identify the authorized isolated lab, test endpoints, required privileges, and cleanup path before execution.

## 4. Implement

- Make the smallest coherent change within scope.
- Add focused tests alongside code. Do not claim a placeholder command or script is runnable.
- On an implementation milestone, update all three ebook editions in the same change set.

## 5. Verify

- Run applicable existing format, build, and test commands; report unavailable commands and prerequisites honestly.
- Run live network checks only within the approved isolated lab.
- Report compilation, bidirectional packet forwarding, and application-level connectivity as separate observations.

## 6. Update and deliver

- Reconcile documentation, tests, commands, and all three ebook editions with the final behavior.
- Review the diff for scope, secrets, route/firewall impact, and unsupported claims.
- Use Conventional Commits and the repository-configured Git identity. Summarize evidence and remaining unverified behavior.
- For an implementation milestone, link all three editions, highlight decisions worth studying, separate observed from unverified checks, and give a short code-reading and reproduction sequence.
