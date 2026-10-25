# Flyt operator guide

This is the practical guide for installing, opening, operating, recovering, and
accepting Flyt on Windows. Start here if the command list feels like it was
written by the command list.

## Mental model

Flyt has two layers:

- **Desk** is the daily cockpit: one to ten Claude, Codex, OpenCode, or PowerShell panes.
- **Mission** is the controlled workflow: plan, approve, execute, review, verify,
  and stop at explicit limits.

Use a Desk for interactive work. Use a Mission when the work needs a durable plan,
approval boundary, and deterministic verification.

## The four commands to learn first

```powershell
flyt doctor
flyt desk configure --path .
flyt desk open
flyt desk status
```

Run `flyt help quickstart`, `flyt help desk`, or `flyt help mission` inside the CLI
for the same task-oriented navigation.

## First-time setup

### 1. Verify prerequisites

```powershell
flyt doctor
```

Flyt expects Windows 11, PowerShell 7, Git, psmux 3.4.0 or newer, Claude Code,
and Codex for the core acceptance flow. `doctor` also reports OpenCode when it is
installed. It changes nothing on the machine.

### 2. Choose panes and providers

From the repository you want to operate:

```powershell
flyt desk configure --path .
```

Choose 1–10 panes. For every pane, select Claude, Codex, OpenCode, or Shell, then
set its name, role, and task label. Flyt previews the result and writes
`.flyt/desk.json`; pane names must be unique. It can open the Desk immediately.

For repeatable setup without prompts:

```powershell
flyt desk configure --path . --name solo --panes 1 --providers claude
flyt desk configure --path . --name pair --panes 2 --providers claude,codex
flyt desk configure --path . --name mixed --panes 4 `
  --providers claude,codex,opencode,shell
```

Use `--force` to replace an existing template non-interactively. Use
`flyt desk template init` only when you prefer to start from the opinionated
five-pane JSON template and edit it by hand.

### 3. Open the cockpit

```powershell
flyt desk open --path . --template default
```

`desk open` starts or restores the Desk, reconciles state, dispatches eligible
queued work, restores focus, and attaches to the psmux grid.

### 4. Prove the real Windows host

```powershell
flyt doctor --interactive
```

This launches a disposable Claude/Codex Desk and guides you through authentication,
one trivial prompt per provider, navigation, zoom, popup actions, multiline paste,
colour/contrast, unexpected psmux loss, focus/identity recovery, and a visible
Windows notification. It writes redacted JSON and Markdown receipts under the Flyt
state directory.

Provider-specific resume needs existing provider session IDs. Supply a template
containing one Claude and one Codex pane with `providerSessionId` values:

```powershell
flyt doctor --interactive --template default
```

Without those IDs, the receipt marks the resume gate `NOT RUN` instead of inventing
success. Prompts, session IDs, tokens, usernames, environment variables, and paths
are excluded from the receipt.

Flyt's resume commands follow the official provider interfaces: Anthropic documents
[`claude --resume <session-id>`](https://docs.anthropic.com/en/docs/claude-code/cli-usage),
and OpenAI documents [`codex resume`](https://developers.openai.com/fr-FR/docs/projects).

## Daily workflow

### Enter the most recent Desk

```powershell
flyt desk open
```

Or select it explicitly:

```powershell
flyt desk open --name full-review
```

### See what needs attention

```powershell
flyt desk status
flyt desk watch --name full-review --notify
```

`status` is a one-time reconciliation. `watch` continuously reconciles, dispatches
eligible queued work, and requests notifications for new attention states.

### Send work

```powershell
flyt desk send --name full-review --pane builder --text "Implement the approved plan"
flyt desk send --name full-review --role reviewer --text "Review the current diff"
flyt desk send --name full-review --state NeedsInput --text "Use the documented default" --yes
```

Multi-pane sends show recipients and require `--yes`. Shell panes are excluded
unless `--include-shell` is explicit.

### Queue expensive work

```powershell
flyt desk queue add --name full-review --pane verifier `
  --text "Run the complete release suite" --expensive
flyt desk queue list --name full-review
flyt desk queue dispatch --name full-review
```

Queued work waits for the target pane, memory gate, and expensive-work concurrency
limit. The dispatcher does not kill or pause agents.

### Hand work to another pane

```powershell
flyt desk handoff --name full-review --from builder --to reviewer `
  --goal "Ship CSV import" `
  --action "Review correctness and regressions" `
  --verification "dotnet test" `
  --send
```

Flyt writes a JSON bundle plus a Git patch when a working-tree diff exists, then
sends the artifact path to the destination pane.

## Cockpit keyboard map

| Keys | Result |
|---|---|
| `Alt+1` … `Alt+9` | Jump directly to panes 1–9 |
| `Alt+0` | Jump directly to pane 10 |
| `Ctrl+b`, then `A` | Jump to the next pane needing attention |
| `Ctrl+b`, then `Z` | Zoom or restore the active pane |
| `Ctrl+b`, then `S` | Preview and send text |
| `Ctrl+b`, then `R` | Restart or resume the active pane |
| `Ctrl+b`, then `H` | Create a structured handoff |
| `Ctrl+b`, then `Q` | Queue work for the active pane |
| `Ctrl+b`, then `L` | Open recent logs |
| `Ctrl+b`, then `O` | Open the action menu |
| `Ctrl+b`, then `?` | Show the psmux shortcut overlay |
| `Ctrl+b`, then `d` | Detach without stopping the Desk |

If a terminal consumes an `Alt` shortcut, use the action menu or `flyt desk focus`.

## Recovery recipes

### A pane failed or disappeared

```powershell
flyt desk status
flyt desk failures --name full-review
flyt desk recover --name full-review
flyt desk recover --name full-review --apply
```

The first `recover` is a preview. `--apply` performs the recovery.

### The entire Desk was stopped or psmux vanished

```powershell
flyt desk reopen --name full-review
flyt desk open --name full-review
```

Flyt recreates the registered pane identities, restores the adaptive layout and
focus, and uses provider resume IDs when the template supplied them.

### Inspect recent output or history

```powershell
flyt desk logs --name full-review --pane builder --lines 200
flyt desk history --name full-review --search failure
```

### Stop cleanly

```powershell
flyt desk stop --name full-review
```

Stopping a Desk preserves its registration so it can be reopened later.

## Terminal sizing and accessibility

Flyt blocks an attach when the viewport is too small for the requested pane count:

| Panes | Minimum viewport |
|---:|---:|
| 1 | 80×24 |
| 2 | 100×28 |
| 3 | 120×32 |
| 4 | 120×36 |
| 5–6 | 160×40 |
| 7–8 | 200×48 |
| 9–10 | 220×50 |

Resize the terminal first. `--allow-small-terminal` is an explicit override when
you accept narrow panes.

Presentation fallbacks:

```powershell
$env:NO_COLOR = '1'            # remove role/state colours
$env:FLYT_ASCII = '1'          # replace Unicode glyphs and separators
$env:FLYT_HIGH_CONTRAST = '1'  # disable inactive-pane dimming
```

Set these before launching Flyt. State text remains visible even when colour is
disabled.

Ten panes are intended for large displays. Flyt blocks an undersized attach unless
`--allow-small-terminal` is explicit; terminal lasagna remains opt-in.

## PowerShell tab completion

Importing the Flyt module registers completion for native `flyt` commands,
options, Desk names, pane names, and templates:

```powershell
Import-Module Flyt
Enable-FlytCompletion
```

The registration is automatic on import; the explicit command is safe and
idempotent. Add `Import-Module Flyt` to `$PROFILE` to enable it in new shells.

## Notification truth

`flyt desk notify` can prove that Windows accepted the notification API request.
It cannot prove that Windows displayed the toast; Focus Assist, notification
settings, or app identity can suppress it. Only `flyt doctor --interactive` records
human confirmation that the toast was visible. Flyt also emits an OSC/BEL terminal
fallback.

## Mission workflow

```powershell
$mission = flyt mission create --goal "Add CSV import" --path . --profile bounded
flyt mission plan --id <id> `
  --acceptance "CSV files import|invalid rows are reported" `
  --steps "implement|independent review|verify" `
  --verify "dotnet test"
flyt mission approve --id <id>
flyt mission show --id <id>
```

A Mission cannot be approved before its plan is persisted. Pause, resume, and
cancel use `flyt mission pause|resume|cancel --id <id>`.

## Command finder

| I want to… | Command |
|---|---|
| Open or restore my cockpit | `flyt desk open` |
| Choose pane count and providers | `flyt desk configure` |
| See every Desk | `flyt desk status` |
| Watch without attaching | `flyt desk watch --notify` |
| Jump to attention | `flyt desk focus --name N --next` |
| Send a prompt | `flyt desk send` |
| Defer expensive work | `flyt desk queue add` |
| Pass work to a reviewer | `flyt desk handoff` |
| Read pane output | `flyt desk logs` |
| Understand a failure | `flyt desk failures` and `desk history` |
| Repair missing panes | `flyt desk recover --apply` |
| Restore a stopped Desk | `flyt desk reopen` |
| Leave but keep it running | `Ctrl+b`, then `d` |
| Stop it | `flyt desk stop` |
| Prove the real host | `flyt doctor --interactive` |

Run `flyt help desk` for the full Desk command surface and `flyt help mission` for
Mission commands.
