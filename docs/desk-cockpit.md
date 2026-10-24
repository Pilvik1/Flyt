# Desk cockpit

Flyt Desk is the human-operated session-control layer. It launches one to six
Claude, Codex, or shell panes, keeps their identities durable, and makes the next
required operator action visible.

## Templates

Create `.flyt/desk.json`:

```powershell
flyt desk template init --path .
```

Launch it with `flyt desk start --template default`. A pane can define its
provider, stable name, role, task, working directory, command, colour, tags, and
provider session ID. Relative working directories resolve from the workspace.
Global named templates can be stored under the Flyt state directory in
`templates/<name>.json`.

## Colour and attention semantics

Role colour is stable: planner cyan, builder blue, researcher purple, reviewer
magenta, verifier green, ops yellow, and shell grey. State colour changes:
Working blue, Needs Input yellow, Done green, Failed red, and inactive/unknown grey.
Every state also has text and a coloured glyph; colour is never the only signal.

State evidence is ranked:

1. psmux OSC 7501 program status (`working`, `blocked`, `done`, `error`);
2. conservative provider-output attention signals;
3. pane process liveness and exit code;
4. persisted registration state.

These states drive attention and notification only. They do not complete Mission
steps or replace deterministic verification.

## Cockpit and filtering

`flyt desk open --template default` is the primary entry point. It starts or
reopens the selected Desk, restores the most recently focused pane, applies the
adaptive layout, runs state reconciliation and queue dispatch while attached,
and enters the interactive psmux grid. With no name or template it opens the most
recent Desk for the current workspace, or creates a default Desk when none exists.

The integrated grid uses:

- a stable role-coloured pane label;
- explicit state glyph and text, plus a state-coloured active border;
- a heavy white/attention border and dimmed inactive panes;
- compact `pane · ROLE · STATE · task · repo` headers;
- an aggregate `repo/branch · agents · working · waiting · queue · RAM` status line;
- adaptive layouts for one through six panes.

Focus and layout metadata survive reconciliation and stop/reopen cycles. `--no-attach`
applies and verifies the cockpit configuration without entering the full-screen client.

### Keyboard controls and overlays

| Keys | Action |
|---|---|
| `Alt+1` … `Alt+6` | Jump directly to a pane |
| `Ctrl+b`, `A` | Focus the next pane needing attention |
| `Ctrl+b`, `Z` | Zoom or restore the active pane |
| `Ctrl+b`, `S` | Preview and send text |
| `Ctrl+b`, `R` | Confirm and restart/resume the active pane |
| `Ctrl+b`, `H` | Create and deliver a structured handoff |
| `Ctrl+b`, `Q` | Queue work for the active pane |
| `Ctrl+b`, `L` | Show recent logs in a popup |
| `Ctrl+b`, `O` | Open the Desk action menu |
| `Ctrl+b`, `?` | Show the psmux shortcut overlay |

The action menu keeps queue state, failure receipts, history, logs, handoffs, and
pane jumping in context instead of forcing the operator into a separate dashboard.

`flyt desk watch --notify` refreshes the cockpit and emits both a native Windows
toast and an OSC 9/BEL terminal fallback when a pane newly needs input, completes,
or fails. Filter `status` or `watch` with `--name`, `--role`, `--tag`, `--state`,
or `--search`. Use `flyt desk notify` to test notification delivery directly.

`flyt desk focus --name NAME --next` prioritizes Needs Input, Failed, then Done.

## Targeted send

`flyt desk send` selects recipients with `--pane`, `--role`, `--tag`, `--state`,
or `--all`. Multi-pane sends require `--yes` after the recipient preview. Shell
panes are excluded unless `--include-shell` is present. Text crosses a base64
bracketed-paste boundary and is submitted separately, preserving multiline input.

## Recovery and failure receipts

`flyt desk recover --name NAME` previews recovery. Add `--apply` to re-create a
missing Desk, add missing panes, or restart dead panes. Claude and Codex resume
identities are translated into provider-owned launch commands when present.

The first dead transition stores a failure receipt with pane identity, command,
working directory, exit code, recent output, tracked memory, and timestamp.
Inspect receipts with `flyt desk failures --name NAME`.

## Resource-gated work queue

The cockpit displays tracked pane-process memory. Templates define a memory
warning threshold and a maximum number of simultaneously active panes tagged
`expensive`. Queue work with:

```powershell
flyt desk queue add --name NAME --pane verifier --text "Run tests" --expensive
flyt desk queue list --name NAME
flyt desk queue dispatch --name NAME
```

The dispatcher never kills or pauses interactive agents. It claims queued work
durably, then sends it only when the target pane is available, the Desk is below
its memory threshold, and the expensive-work ceiling has capacity. `desk watch`
runs the dispatcher continuously. Completion or failure is derived from a newer
structured pane transition after dispatch, not stale output.

## History and reopen

Pane transitions, sends, queue actions, recovery, and handoffs are written to the
SQLite event ledger. Search them with `flyt desk history --search TEXT`; add
`--name NAME` to scope the result. `flyt desk reopen --name NAME` recreates a
stopped or missing Desk from its durable pane definitions and provider resume IDs.

## Structured handoffs

`flyt desk handoff` writes a JSON handoff under `.flyt/handoffs`, captures the
current branch and changed files, and writes a binary-capable Git patch when a
working-tree diff exists. Optional decisions, verification evidence, and blockers
travel with the bundle. `--send` pastes the artifact path and requested action into
the destination pane.

Windows CI runs a real six-pane psmux lifecycle using shell panes and proves
integrated chrome configuration, role/state headers, action dispatch, re-adoption,
queueing, handoffs, history, focus persistence, stop, and reopen. The target-host
proof remains mandatory for authenticated Claude/Codex sessions, visual ConPTY
colour rendering, popup ergonomics, and interactive toast delivery on Håkon's machine.
