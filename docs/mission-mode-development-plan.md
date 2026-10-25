# Mission mode development plan

Mission mode is currently a durable control plane: briefs, plans, approvals, state
transitions, and events survive restarts. This plan adds the execution engine in
small slices that can be proved independently.

## Current status

The beta engineering path through M1-M7 is implemented. Commit, push, and draft-PR
preparation require an explicit operator command; merge remains manual. Codex app-server
is implemented behind a typed, fail-closed JSONL client; the bounded `codex exec` path is
an explicit fallback. The remaining release gates are a live app-server turn on the
target Windows host and the five-Mission/two-week Windows pilot. See
[`../roadmap.md`](../roadmap.md) for the live checklist.

## Operating constraints

- One write-capable builder until the single-builder path is reliable.
- Every write task runs in a dedicated Git worktree and branch.
- The approved plan is content-hashed. Execution refuses stale or changed plans.
- Verification commands come from the approved plan; model output cannot replace
  their exit codes.
- Provider output is evidence, not authority over Mission state.
- No automatic merge in the first release. A verified Mission stops for operator
  integration approval.
- Every external process has a timeout, captured exit status, and redacted output.

## Delivery milestones

### M1 — Approval-bound execution contract

Add a stable plan fingerprint, persist it with the approval, and refuse execution
unless the current plan version and fingerprint match the approved values.

**Proof gate**

- Editing a plan invalidates its previous approval.
- Equivalent persisted plans produce the same fingerprint.
- A run cannot start from any state except `Planning` with a current approval.

### M2 — Isolated workspace lifecycle

Create a Mission integration branch and one builder worktree using argument-safe
Git process calls. Persist the branch, worktree path, base commit, and lifecycle
state. Never remove a non-clean worktree automatically.

**Proof gate**

- Creation is idempotent after process restart.
- Paths remain below Flyt's Mission workspace root.
- Dirty or unexpected worktrees block cleanup and preserve evidence.

### M3 — One structured builder

Add one bounded headless provider adapter. The adapter receives the goal, approved
plan, acceptance criteria, and worktree path; it returns a typed result containing
provider identity, timestamps, exit status, session identity when available, and
redacted output references.

Claude uses bounded print-mode JSON. Codex uses app-server v2 over stdio: Flyt performs
the initialize/thread/turn lifecycle, records thread and turn IDs, rejects invisible
approval or input requests, interrupts on cancellation, and treats only a completed
turn as success. Set `FLYT_CODEX_TRANSPORT=exec` only for the explicit compatibility
fallback.

**Proof gate**

- A fake adapter proves success, failure, timeout, and cancellation deterministically.
- Provider success cannot advance the Mission without a recorded Git diff.
- Restart never launches a second builder for an already active attempt.

### M4 — Deterministic verification and evidence

Run the approved verification command in the builder worktree without shell-string
reinterpretation. Persist an evidence receipt with the plan fingerprint, base/head
commits, changed paths, command, exit code, duration, and redacted bounded output.

**Proof gate**

- Exit code zero plus a non-empty diff moves the Mission to `Integrating`.
- Failure moves it to `Blocked` with an inspectable receipt.
- The controller stops for explicit operator integration approval.

### M5 — Recovery and bounded attempts

Persist execution attempts and leases. On restart, reconcile process, worktree,
branch, Git head, and the latest receipt before deciding whether to resume, block,
or request operator input.

**Proof gate**

- Failure injection at every state boundary produces no duplicate mutation.
- Time, retry, and provider-failure ceilings are enforced from persisted state.
- Pause, resume, and cancel work during provider and verification execution.

### M6 — Independent review and fix loop

Add read-only reviewer adapters and structured findings. Only the builder owns the
worktree. Review findings reference paths and evidence; accepted findings create a
bounded fix attempt followed by fresh verification.

**Proof gate**

- Reviewer failure cannot silently pass.
- Every fix invalidates earlier verification evidence.
- Retry exhaustion blocks rather than integrating.

### M7 — Integration and pilot

Add push and draft-PR preparation behind explicit approval. Keep merge manual until
five real Missions across two weeks meet the acceptance target.

Pilot receipts pair a baseline and Mission result by scenario. They record provider
metadata, duration, attempts, review blocks, recoveries, and unplanned operator
interventions without storing prompts or filesystem paths. `mission pilot report`
fails closed until five paired Windows scenarios span 14 days, include recovery proof,
retain complete evidence, and reduce interventions by at least 50%.

**Proof gate**

- Secret and accidental-file checks pass before push.
- Five Missions complete with complete receipts and at least 50% fewer operator
  interventions than the Phase 0 baseline.
- Automatic merge remains a separate, explicit policy decision.

## First usable vertical slice

The first release candidate is intentionally narrow:

```text
approved plan
  -> current approval/fingerprint check
  -> isolated builder worktree
  -> one bounded provider attempt
  -> approved deterministic verification
  -> persisted evidence receipt
  -> stop for operator integration approval
```

It excludes multiple writers, autonomous merge, parallel task graphs, reviewer fix
loops, and provider fallback. Those features become safer after the host can prove
one restartable Mission end to end.

## Definition of done

Mission mode becomes an MVP only when a fresh Windows machine can complete the
vertical slice, Flyt can restart safely at each boundary, and the resulting Git
diff and verification receipt can be inspected without reading terminal scrollback.
