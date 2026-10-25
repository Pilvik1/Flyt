# ADR 0002: Use psmux as the Windows terminal substrate

## Status

Accepted. Portable and real Windows psmux lifecycle gates are CI-proven; authenticated
provider and human-visible presentation checks remain target-host acceptance gates.

## Decision

Use psmux for panes, interactive sessions, persistence, and direct operator takeover. Flyt will not implement a terminal emulator.

## Consequences

- Flyt remains focused on session metadata, mission orchestration, policies, and evidence.
- psmux is a replaceable adapter rather than an authoritative state store.
- Native Windows command behavior is release-gated in CI with pinned psmux. Real
  provider authentication, visual fidelity, popup ergonomics, and visible toast
  delivery are recorded separately by `flyt doctor --interactive`.
