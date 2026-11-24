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

## Milestone status

- **Milestone 3 (control-plane components) delivered.** Schema lives in EF Core
  migrations under `control-dotnet/VpnControl/Data/Migrations/`:
  `initial-control-plane` (nodes, sessions, ip_leases, outbox) and
  `outbox-dispatch` (stream_epoch, backend_instance_id, status, attempts,
  next_retry_at, deadline on outbox), and `outbox-delivery-indexes`
  (deadline and per-node ordering indexes). Apply with `dotnet ef database
  update` or `db.Database.MigrateAsync()`. The Go orchestration client is
  delivered as `agent-go/pkg/orchestrator` (mTLS channel, fencing, ACKs).
  Wiring it into `cmd/vpn-agent` and the end-to-end gRPC laboratory were
  milestone-4 integration work and are now covered below.
- **Milestone 4 (end-to-end sessions) validated locally.** Rust has the
  versioned AEAD wire format, directional keys, multi-peer `SessionTable`,
  per-peer endpoint pinning, tombstoned key material and the
  StagePeer/Activate/Retire/RevokePeer/GetSessions control socket. The Go
  `vpn-agent -serve` loop owns the mTLS `NodeChannel`, applies commands to
  that socket, enforces expiration locally and reconciles `SyncState`. The
  C# control plane issues epoch-1 directional key material, keeps leases
  reserved through `revoking`, and releases them only on fenced removal
  ACKs. `tests/checks.sh --lab` includes the isolated two-client HTTP
  laboratory in normal-revocation and gRPC-interruption modes. Do not
  advance milestones 5–6.
