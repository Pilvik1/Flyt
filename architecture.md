# Architecture

## Product boundary

Flyt has two independently testable layers that share adapters, persistence, policies, and workspace ownership.

### Desk mode

Desk mode creates and manages visible interactive sessions. psmux provides native Windows ConPTY panes; Flyt provides naming, layout, provider selection, status, and recovery metadata.

Desk attention is evidence-ranked: structured provider/psmux program state first,
conservative provider-output signals second, process liveness third, and operator
override last. Attention state is operational metadata, not proof that a Mission
step succeeded.

### Mission mode

Mission mode is a deterministic host state machine. Models may propose plans and perform work, but they cannot silently change state, approve protected actions, or reinterpret failing verification as success.

## Components

| Component | Responsibility |
|---|---|
| `Flyt.Cli` | Commands, terminal rendering, operator interactions |
| `Flyt.Core` | Domain models, state transitions, autonomy policy |
| `Flyt.Persistence` | Versioned SQLite migrations; Desk metadata, failure receipts, plan, approval, and append-only event records |
| `Flyt.Providers` | Claude, Codex, and shell discovery/command contracts |
| `Flyt.Terminal` | psmux command plans and process adapter |
| `powershell/Flyt` | PowerShell-native command surface |

## Mission states

```text
Briefing -> PlanApproval -> Planning -> Executing
                                     -> Reviewing
                                     -> Verifying
                                     -> Fixing -> Reviewing
                                     -> Integrating -> Completed

Any active state -> Paused -> previous active state (future recovery work)
Any active state -> Blocked
Any non-terminal state -> Cancelled
```

Every persisted state change appends a corresponding event in the same SQLite transaction.

## Provider strategy

- Desk mode launches normal interactive `claude` and `codex` processes in psmux panes.
- Mission mode uses bounded Claude print-mode JSON or a typed Codex app-server v2
  client in a dedicated builder worktree. The independent reviewer runs read-only.
- Provider exit status and review markers are persisted, but deterministic commands and
  Git evidence—not provider prose—control integration eligibility.
- Codex app-server runs over JSONL stdio. Flyt persists protocol/version/thread/turn
  metadata, rejects interactive requests, and interrupts a known turn before terminating
  a cancelled process. `codex exec` remains an explicit compatibility fallback.
- Native Claude teams may be piloted, but do not own Flyt mission state.

## Workspace ownership

- One mission integration branch.
- One branch/worktree for each write-capable task owner.
- Reviewers consume diffs and evidence without editing builder worktrees.
- Integration happens only after review and deterministic verification pass.

## Persistence

SQLite is the sole authority. Schema changes use ordered `PRAGMA user_version`
migrations. The current schema stores missions, content-bound plan approvals,
workspaces, provider attempts, provider session metadata, review and verification
receipts, pilot measurements, registered Desks and panes, durable Desk failure receipts,
and append-only Mission events. Provider JSONL is parsed in flight; it is not the system
of record.

## Recovery contract

The controller must be restartable. Replaying or reading durable state must never
repeat an already-recorded transition or mutation. psmux sessions are reconciled by
stable Desk and pane names, while live pane IDs are refreshed in SQLite. Terminal
presence is not proof that a mission step completed. Mission transitions use a
persisted version column to reject stale concurrent updates.

Desk templates live in `.flyt/desk.json` or the user's Flyt template directory.
They define roles, tasks, colours, tags, provider commands, resume identities,
and resource thresholds. psmux titles begin with the stable Flyt pane name so
restart reconciliation remains deterministic while the visible suffix can show
state, role, and task.

The integrated cockpit remains psmux-native. Flyt uses renderer-safe border
formats with role-colour markers embedded in durable pane titles, plus
attention-aware active borders, adaptive layouts, keyboard actions, and popup
commands, while a foreground `desk open` controller reconciles state and
dispatches queued work beside the attached psmux client. SQLite persists the
preferred layout and most recently active stable pane name for stop/reopen recovery.

Targeted sending uses psmux's base64 `send-paste` boundary, followed by a separate
submit key. This avoids command parsing and multiline corruption. Structured
handoffs are durable JSON plus an optional Git patch under `.flyt/handoffs`;
the pane receives only the artifact path and requested action.
