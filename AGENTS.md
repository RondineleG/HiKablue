# The Journey of my VPN

Build an educational VPN in Rust, Go, and C#. Treat learning and verification as deliverables alongside working software. These rules apply to every implementation milestone.

## Agent entry point

Before editing, inspect the repository and read the [architecture specification](vpn-debian-specification.md), `.agc/AGENTS.md`, and `.agc/RULES.md`. Share a short plan, then proceed within the requested scope. For the initial TUN implementation task, also read `.agc/IMPLEMENTATION.md`. Requests limited to analysis must not modify files.

The architecture specification is authoritative for protocol and topology. `.agc/RULES.md` contains project-wide engineering and safety rules; `.agc/AGENTS.md` routes agents to the right workflow. If these documents conflict or a request would change routing, privileges, or milestone order, surface the conflict instead of silently choosing a new design.

## Project-wide deliverables

- Maintain the living ebook **The Journey of my VPN** in English, Spanish, and Brazilian Portuguese. Keep complete, directly readable editions, editable sources, tables of contents, and stable chapter IDs in the repository.
- Update all three editions in the same change set as each implementation milestone. Keep chapter IDs, code references, commands, expected results, and revision notes aligned. Add a chapter-parity check with the first chapter.
- Map implemented requirements to code, tests, and book sections. Explain prerequisites, packet/control flow, design choices, tradeoffs, failure cases, troubleshooting, and reproducible exercises; record deferrals and reasons.
- Preserve learning history across revisions and distinguish observed results from expectations. Report where checks ran and their outcomes; identify unverified behavior and reproduction commands. Report compilation, packet forwarding, and application connectivity separately.
- Raw UDP is for test traffic in an isolated, authorized lab only. See `.agc/RULES.md` for required safeguards.

Name branches `feat/<slug>` for additive work and `fix/<slug>` for bug fixes. Use Conventional Commits and the Git identity configured for the repository. Do not add AI attribution trailers such as `Generated with ...` or `Co-Authored-By`. The pull-request check validates branch names and commit subjects.
