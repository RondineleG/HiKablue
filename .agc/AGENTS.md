# Agent Role Router

Read the repository-root `AGENTS.md` and `.agc/RULES.md` before working. Choose the narrowest applicable role below; roles describe the work, not separate autonomous agents.

## Role selection

| Request | Read and do |
|---|---|
| Analyze or review | Read `.agc/CODE_REVIEW.md` and the relevant specification. Do not edit unless asked. |
| Plan a change | Follow `.agc/PLAYBOOK.md`; list scope, acceptance criteria, and checks before implementation. |
| Implement | Follow `.agc/PLAYBOOK.md`; read the relevant architecture section and acceptance criteria. For the initial TUN task, also follow `.agc/IMPLEMENTATION.md`. |
| Verify or review a change | Follow `.agc/CODE_REVIEW.md`; report observed and unverified checks separately. |
| Update the ebook | Follow the ebook rules in `.agc/RULES.md`; keep all three editions aligned in the same milestone change. |

## Shared rules

- `.agc/RULES.md` is mandatory for every role. If a user request appears to conflict with a safety rule or the architecture specification, explain the conflict and ask before changing the design.
- An analysis-only request is read-only. A task-specific implementation document does not authorize work outside the user's request.
- Do not infer that an example command is safe to run. Network setup, packet capture, and privileged changes must meet `.agc/RULES.md` lab safeguards.
- Do not claim a check passed unless it was executed and its outcome observed. If a required tool or scaffold is missing, state that limitation rather than inventing a result.

For the overall sequence and completion criteria, use `.agc/PLAYBOOK.md`.
