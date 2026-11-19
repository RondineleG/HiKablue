# HiKablue Review Checklist

Use this checklist for changes involving implementation, deployment, or verification. Read-only reviews must not alter the code or status unless the user asks for fixes.

## Scope and design

- [ ] The change matches the user's request and the documented milestone.
- [ ] Protocol and topology choices agree with `vpn-debian-specification.md`; conflicts are called out rather than silently resolved.
- [ ] Rust, Go, C#, Protobuf, and deployment responsibilities remain separated as specified.

## Network and security

- [ ] Raw UDP is limited to test traffic in an isolated, authorized lab; structural validation is not presented as peer authentication.
- [ ] Packet lengths, addresses, MTU/buffer boundaries, and rejection paths are checked.
- [ ] TUN privileges and cancellation/cleanup are explicit.
- [ ] Routes, forwarding, NAT, firewall, and MSS rules are scoped, reversible, and paired with cleanup. Full-tunnel behavior preserves the endpoint route.
- [ ] No credentials or non-test user traffic appear in logs, examples, or captures.

## Verification and learning material

- [ ] Tests cover changed behavior and relevant failure cases; exact commands and outcomes are reported.
- [ ] Compilation, forwarding, and application connectivity are described separately.
- [ ] Each completed implementation milestone updates all three complete ebook editions in the same change set.
- [ ] Requirements map to implementation, tests, and chapter IDs; examples and expected outputs match observed behavior.
- [ ] The diff contains no unsupported claims, placeholder commands, or unrelated files.
