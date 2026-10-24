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

`flyt desk watch --notify` refreshes the cockpit and emits an OSC 9/BEL terminal
notification when a pane newly needs input, completes, or fails. Filter `status`
or `watch` with `--name`, `--role`, `--tag`, `--state`, or `--search`.

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

## Resources and notifications

The cockpit displays tracked pane-process memory. Templates define a memory
warning threshold and a maximum number of simultaneously active panes tagged
`expensive`. V0.4 warns rather than killing or pausing interactive work. Queueing
and enforcement belong to Mission execution, where ownership and retry policy are
explicit.

## Structured handoffs

`flyt desk handoff` writes a JSON handoff under `.flyt/handoffs`, captures the
current branch and changed files, and writes a binary-capable Git patch when a
working-tree diff exists. Optional decisions, verification evidence, and blockers
travel with the bundle. `--send` pastes the artifact path and requested action into
the destination pane.

The target-host proof remains mandatory: Windows CI proves portable behavior, but
not authenticated providers, ConPTY rendering, notification delivery, or restart
re-adoption on Håkon's machine.
