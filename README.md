# FlytV1

FlytV1 is a local-first PowerShell command center for agent-driven work. It combines:

- **Desk mode** for running several Claude Code, Codex, or shell sessions in one psmux workspace.
- **Mission mode** for bounded plan → build → review → verify → fix → integrate workflows.

The product is optimized for one operator on Windows 11 with PowerShell 7. It is private, opinionated, and built to reduce repeated copy/paste coordination between chat interfaces and terminal agents.

## Current status

Foundation slice under active development:

- .NET 10 command-line application;
- PowerShell module wrapper;
- provider and dependency diagnostics;
- deterministic psmux command planning;
- transactional psmux startup with rollback on partial failure;
- SQLite-backed Desk registry, mission plans, approvals, and event state;
- Desk status reconciliation plus pane focus, restart, and log capture;
- colour-coded live Desk cockpit with role, task, state, resource, and tag views;
- repo-local Desk templates and resumable provider identities;
- targeted multiline send, recovery previews, and durable failure receipts;
- structured builder/reviewer/verifier handoff bundles;
- evidence-bearing mission state machine;
- Manual, Bounded, and Auto policy profiles;
- initial tests and Windows CI.

Native Windows integration with psmux, Claude Code, and Codex must pass the Phase 0 proof before the project is considered usable.

## Install on Windows

FlytV1 ships as a self-contained Windows package, so the .NET SDK is not required.
Because this repository is private, the bootstrap uses an authenticated GitHub CLI
session instead of pretending that a public `irm | iex` URL exists.

Prerequisites: Windows 11, PowerShell 7, and an authenticated GitHub CLI (`gh auth login`).

```powershell
gh api repos/Pilvik1/FlytV1/contents/scripts/install.ps1 -H "Accept: application/vnd.github.raw" | pwsh -NoProfile -Command -
```

The installer selects x64 or ARM64 automatically, downloads the latest private
release, verifies its SHA-256 checksum, installs the `flyt` executable and PowerShell
module for the current user, updates the user PATH, and runs a smoke test. Existing
installations are restored if installation fails.

psmux remains explicit because dependency installation changes the machine. To
install it with Flyt, download the installer first and pass `-InstallPsmux`:

```powershell
$installer = Join-Path $env:TEMP 'install-flyt.ps1'; gh api repos/Pilvik1/FlytV1/contents/scripts/install.ps1 -H "Accept: application/vnd.github.raw" | Set-Content $installer; pwsh -NoProfile -File $installer -InstallPsmux; Remove-Item $installer
```

Useful installer options include `-Version 0.4.0`, `-Architecture x64|arm64`,
`-SkipPathUpdate`, and local `-PackagePath` / `-ChecksumPath` inputs for offline or
CI validation.

## Architecture

```text
PowerShell module / Flyt CLI
           |
  deterministic controller
  /        |         \
providers  git      psmux
  \        |         /
     SQLite event ledger
```

psmux owns panes. Flyt owns mission state, policy, evidence, and recovery. Terminal output is never parsed as authoritative mission state.

See [architecture.md](architecture.md) and [roadmap.md](roadmap.md).

## Developer quick start

Requires the .NET 10 SDK.

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet run --project src/Flyt.Cli -- doctor
```

Import the development PowerShell module:

```powershell
Import-Module ./powershell/Flyt/Flyt.psd1 -Force
$env:FLYTV1_EXE = (Resolve-Path ./src/Flyt.Cli/bin/Release/net10.0/flyt.exe)
Test-FlytEnvironment
```

Preview a six-agent Desk layout without launching psmux:

```powershell
dotnet run --project src/Flyt.Cli -- desk start `
  --name demo `
  --sessions "claude:planner,claude:builder,claude:researcher,codex:reviewer,codex:verifier,shell:ops" `
  --dry-run
```

Create and approve a mission:

```powershell
dotnet run --project src/Flyt.Cli -- mission create `
  --goal "Add CSV import" `
  --path . `
  --profile bounded
dotnet run --project src/Flyt.Cli -- mission plan `
  --id <mission-id> `
  --acceptance "CSV files import|invalid rows are reported" `
  --steps "implement|independent review|verify" `
  --verify "dotnet test"
dotnet run --project src/Flyt.Cli -- mission list
dotnet run --project src/Flyt.Cli -- mission approve --id <mission-id>
```

Approval is rejected until a plan is persisted. Pause/resume records the exact prior
state instead of allowing an arbitrary restart point.

Reconcile and operate a running Desk:

```powershell
dotnet run --project src/Flyt.Cli -- desk status
dotnet run --project src/Flyt.Cli -- desk focus --name demo --pane builder
dotnet run --project src/Flyt.Cli -- desk logs --name demo --pane builder --lines 200
dotnet run --project src/Flyt.Cli -- desk restart-pane --name demo --pane builder
```

Create a reusable repo-local template, launch it, and open the live cockpit:

```powershell
flyt desk template init --path .
flyt desk start --path . --template default
flyt desk watch --name full-review --notify
```

Desk colours use separate channels: the role is stable (`planner`, `builder`,
`reviewer`, `verifier`, `ops`) while attention state changes between Working,
Needs Input, Done, Failed, Paused, and Unknown. Pane titles also carry coloured
state glyphs so the psmux grid remains scannable while the Flyt cockpit is not
in the foreground.

Target selected agents without retyping a prompt in every pane:

```powershell
flyt desk send --name full-review --role reviewer --text "Review the current diff" --yes
flyt desk send --name full-review --state NeedsInput --text "Continue with the documented default" --yes
```

Recover and inspect failures:

```powershell
flyt desk recover --name full-review
flyt desk recover --name full-review --apply
flyt desk failures --name full-review
```

Create a structured handoff bundle and deliver its path to the reviewer:

```powershell
flyt desk handoff --name full-review --from builder --to reviewer `
  --goal "Ship CSV import" --action "Review correctness and regressions" `
  --verification "dotnet test" --send
```

See [docs/desk-cockpit.md](docs/desk-cockpit.md) for templates, selectors,
attention sources, colours, recovery, resource warnings, and handoffs.

## Safety defaults

- One write owner per worktree.
- Deterministic verification outranks agent opinion.
- Secrets never belong in Flyt state.
- Material deletion, system-wide installs, external paid services, and security weakening require approval.
- No repository deletion/archival, public visibility changes, protected-branch force pushes, or unconfirmed remote-branch deletion.
- Missions stop on success, blocker, policy approval, or hard limits.
- Cancelling a hosted command kills its complete child process tree.
- Multi-pane sends preview recipients and require explicit `--yes`; shell panes
  are excluded unless requested.

## Proof boundary

Release builds and tests run on Ubuntu and Windows CI. This proves compilation,
state transitions, SQLite migrations, command planning, CLI smoke behavior, and
PowerShell module import. It does **not** prove authenticated Claude/Codex sessions,
Windows ConPTY rendering, manual takeover, or restart re-adoption on the target
machine. Those remain explicit Phase 0 release gates.

## License

Private repository. All rights reserved.
