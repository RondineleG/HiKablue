# Architecture Decision Records

Use ADRs only for durable choices that affect architecture, trust boundaries, protocol behavior, or operational safety. The architecture specification remains the canonical design document; an ADR records why a decision was made and links to the corresponding specification section.

Before creating an ADR, search existing specifications and ADRs. Do not use an ADR to duplicate the current design or to record routine implementation details.

Suggested format:

```markdown
# ADR-NNNN: Short decision title

- Status: Proposed | Accepted | Superseded
- Date: YYYY-MM-DD
- Related specification: path and section

## Context
## Decision
## Consequences
## Evidence and alternatives
```

Do not create an ADR for a decision that is still being discussed. When a decision changes, preserve the old record and link the superseding ADR.
