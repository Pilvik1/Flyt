# Roadmap

## Phase 0 — foundation proof

- [ ] Validate PowerShell 7, psmux, Claude Code, and Codex on the target Windows 11 machine.
- [ ] Launch six mixed sessions in one psmux workspace.
- [ ] Complete one Claude-only builder/reviewer/verifier task.
- [ ] Complete one Claude-builder/Codex-reviewer task.
- [ ] Test restart and session re-adoption.
- [ ] Capture intervention baseline and provider metadata.

## Phase 1 — foundation repository

- [x] Private repository.
- [x] .NET 10 solution and PowerShell module structure.
- [x] Architecture, roadmap, policy, and decision records.
- [x] Core state machine and SQLite event ledger.
- [x] Initial provider diagnostics and psmux command planning.
- [x] Green Windows and Ubuntu CI on the foundation.

## Phase 2 — Desk MVP

- [ ] Native Windows psmux execution.
- [x] Deterministic one-to-six-session layouts.
- [ ] Central live dashboard.
- [x] Focus, attach, pane restart, logs, stop, and child-tree cancellation.
- [x] Durable Desk/pane registry and live-state reconciliation.
- [ ] Prove restart re-adoption on the target Windows host.

## Phase 3 — Mission foundation

- [ ] Structured brief and task graph schemas.
- [x] Persisted acceptance plan and enforced operator approval.
- [ ] Claude structured adapter.
- [ ] Codex app-server adapter.
- [ ] Git worktree manager and ownership rules.
- [x] Pause/resume/cancel with exact prior-state recovery.
- [ ] Recovery of interrupted provider execution.

## Phase 4 — quality loop

- [ ] Build → review → verify → fix → integrate workflow.
- [ ] Structured review and evidence receipts.
- [ ] Bounded retries and time/provider-failure ceilings.
- [ ] Secret and accidental-file checks.
- [ ] Push, draft PR, and gated automatic merge policy.

## Phase 5 — daily driver

- [ ] Windows installer and update path.
- [ ] Failure-injection tests.
- [ ] Redaction and log retention.
- [ ] Polished terminal theme.
- [ ] Five real missions across two weeks with at least 50% fewer interventions.
