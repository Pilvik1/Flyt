# Roadmap

## Phase 0 — foundation proof

- [ ] Validate PowerShell 7, psmux, Claude Code, and Codex on the target Windows 11 machine.
- [ ] Launch six mixed sessions in one psmux workspace.
- [ ] Complete one Claude-only builder/reviewer/verifier task.
- [ ] Complete one Claude-builder/Codex-reviewer task.
- [ ] Test restart and session re-adoption.
- [ ] Capture intervention baseline and provider metadata.

## Phase 1 — foundation repository

- [x] Private repository prepared for an explicit public-release gate.
- [x] .NET 10 solution and PowerShell module structure.
- [x] Architecture, roadmap, policy, and decision records.
- [x] Core state machine and SQLite event ledger.
- [x] Initial provider diagnostics and psmux command planning.
- [x] Green Windows and Ubuntu CI on the foundation.

## Phase 2 — Desk cockpit

- [x] Native Windows psmux execution in CI.
- [x] Deterministic one-to-ten-session layouts.
- [x] Adaptive one-to-ten-pane layouts and zoom-preserving focus mode.
- [x] Guided pane/provider configuration with Claude, Codex, OpenCode, and Shell.
- [x] Integrated role/state headers, active-pane emphasis, and aggregate status line.
- [x] One-command start/reopen/monitor/attach with focus persistence.
- [x] Keyboard-first pane jumps and contextual action popups.
- [x] Central live dashboard with role labels, pane state, and attention cues.
- [x] Focus, attach, pane restart, logs, stop, and child-tree cancellation.
- [x] Durable Desk/pane registry and live-state reconciliation.
- [x] Directory-aware session templates and targeted operator broadcast.
- [x] Durable failure receipts and structured Desk handoff bundles.
- [x] Resource-gated queue, searchable history, Desk reopen, and Windows toast path.
- [x] Real ten-pane psmux shell lifecycle in Windows CI.
- [ ] Prove restart re-adoption on the target Windows host.

## Phase 3 — Mission foundation

Execution order, constraints, and proof gates are defined in
[`docs/mission-mode-development-plan.md`](docs/mission-mode-development-plan.md).

- [x] Structured brief schema and sequential persisted task plan (parallel DAG is post-beta).
- [x] Persisted acceptance plan and enforced operator approval.
- [x] Bounded Claude print-mode JSON adapter.
- [x] Typed Codex app-server adapter with fail-closed JSONL contract tests.
- [ ] Live Codex app-server turn on the target Windows host.
- [x] Bounded Codex exec JSON adapter retained as an explicit fallback.
- [x] Git worktree manager and ownership rules.
- [x] Hash-bound approval for repository setup and execution.
- [x] Pause/resume/cancel with exact prior-state recovery.
- [x] Conservative recovery of interrupted Mission execution.

## Phase 4 — quality loop

- [x] Build → review → verify → bounded fix → integrate workflow.
- [x] Structured review and evidence receipts.
- [x] Bounded retries and time/provider-failure ceilings.
- [x] Secret and accidental-file checks.
- [x] Operator-approved commit, push, and draft PR; merge remains manual by policy.

## Phase 5 — daily driver

- [x] Checksum-verified Windows installer supporting private and public releases.
- [x] Failure-injection tests for timeout, no-diff, stale evidence, and interrupted execution.
- [x] Best-effort secret redaction and bounded failure-receipt retention.
- [x] MIT license, privacy/security docs, SBOM, and contribution surface.
- [x] Polished terminal theme with stable role colors and task labels.
- [ ] Five real missions across two weeks with at least 50% fewer interventions.
