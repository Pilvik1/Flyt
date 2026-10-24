# Architecture

## Product boundary

FlytV1 has two independently testable layers that share adapters, persistence, policies, and workspace ownership.

### Desk mode

Desk mode creates and manages visible interactive sessions. psmux provides native Windows ConPTY panes; Flyt provides naming, layout, provider selection, status, and recovery metadata.

### Mission mode

Mission mode is a deterministic host state machine. Models may propose plans and perform work, but they cannot silently change state, approve protected actions, or reinterpret failing verification as success.

## Components

| Component | Responsibility |
|---|---|
| `Flyt.Cli` | Commands, terminal rendering, operator interactions |
| `Flyt.Core` | Domain models, state transitions, autonomy policy |
| `Flyt.Persistence` | SQLite mission and append-only event records |
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
- Mission mode will use structured streams: Claude headless stream JSON and Codex app-server.
- CLI execution remains a bounded fallback.
- Native Claude teams may be piloted, but do not own Flyt mission state.

## Workspace ownership

- One mission integration branch.
- One branch/worktree for each write-capable task owner.
- Reviewers consume diffs and evidence without editing builder worktrees.
- Integration happens only after review and deterministic verification pass.

## Persistence

SQLite is the sole authority. The initial schema stores missions and append-only events. Future migrations add tasks, sessions, approvals, evidence receipts, and artifact references. JSONL is permitted only as a diagnostic export.

## Recovery contract

The controller must be restartable. Replaying or reading durable state must never repeat an already-recorded transition or mutation. psmux sessions are re-adopted by stable names and metadata; terminal presence is not proof that a mission step completed.

