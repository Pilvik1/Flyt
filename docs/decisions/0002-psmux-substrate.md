# ADR 0002: Use psmux as the Windows terminal substrate

## Status

Accepted pending Phase 0 native Windows proof.

## Decision

Use psmux for panes, interactive sessions, persistence, and direct operator takeover. Flyt will not implement a terminal emulator.

## Consequences

- Flyt remains focused on session metadata, mission orchestration, policies, and evidence.
- psmux is a replaceable adapter rather than an authoritative state store.
- Native Windows compatibility and exact command behavior must pass Phase 0 before Desk mode is called usable.

