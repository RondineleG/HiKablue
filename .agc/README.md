# HiKablue Agent Coding Guide

This directory contains the small, project-specific workflow for coding agents. It adapts the role routing, explicit rules, and review/implementation playbooks of the OverGeared `.agc` template; it is not a copy of that .NET template or an autonomous multi-agent runner.

## Start here

1. Read the repository-root `AGENTS.md` and `vpn-debian-specification.md`.
2. Read `.agc/RULES.md` for non-negotiable project rules.
3. Use `.agc/AGENTS.md` to select the workflow for the current role.
4. Read `.agc/IMPLEMENTATION.md` only when the user explicitly asks for the initial TUN implementation task.

The architecture specification is the source of truth for protocol and topology. This directory governs the engineering workflow and safety checks; it does not redefine the architecture.

## Structure

| File | Purpose |
|---|---|
| [`AGENTS.md`](AGENTS.md) | Role-based entry point and document routing. |
| [`RULES.md`](RULES.md) | Mandatory safety, verification, documentation, and change rules. |
| [`PLAYBOOK.md`](PLAYBOOK.md) | Review → specify → plan → implement → verify → update workflow. |
| [`IMPLEMENTATION.md`](IMPLEMENTATION.md) | Scope and acceptance checklist for the initial TUN implementation task. |
| [`CODE_REVIEW.md`](CODE_REVIEW.md) | Focused review checklist for network, lifecycle, tests, and ebook parity. |
| [`adr/README.md`](adr/README.md) | When and how to record a durable architecture decision. |

Reusable skills live in [`docs/skills/`](../docs/skills/README.md), separate from mandatory rules. They are reference instructions, not executable commands and not guaranteed to auto-load in every agent runtime.

## Tracking and commands

Use the repository's chosen issue, pull-request, or session tracker as the single source of task status. Do not add a second JSON task tracker unless the project explicitly adopts it.

The Rust engine (`engine-rust/vpn-engine`) is the executable scaffold; `agent-go/`, `control-dotnet/`, and `proto/` remain documented placeholders. Do not document placeholder build/test commands or add scripts that cannot be run. Use the real commands of each scaffold (for `engine-rust`: `cargo check`, `cargo test`, `cargo build --release`; see `engine-rust/README.md`) rather than duplicating their logic; add a root command interface (for example, a `justfile`) when a second executable scaffold lands.
