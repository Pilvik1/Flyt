<p align="center">
  <img src="docs/assets/flyt-cockpit.svg" alt="Flyt multi-agent cockpit in a Windows Terminal-inspired interface" width="100%">
</p>

<h1 align="center">Flyt</h1>

<p align="center">
  A keyboard-first Windows cockpit for Claude Code, Codex, OpenCode, and shell sessions.<br>
  One Desk. Many repositories. Considerably less tab archaeology.
</p>

<p align="center">
  <a href="https://github.com/Pilvik1/Flyt/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Pilvik1/Flyt/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-22c55e.svg"></a>
  <img alt="Windows 11" src="https://img.shields.io/badge/Windows-11-38bdf8.svg">
  <img alt="PowerShell 7" src="https://img.shields.io/badge/PowerShell-7-7c3aed.svg">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4.svg">
</p>

> **Status:** private release candidate, preparing for public source. Desk mode is
> working and release-gated. Mission orchestration is an explicit work in progress.

## Why Flyt exists

AI coding tools are useful. Five AI coding tools spread across twelve terminal tabs
are a small administrative state. Flyt gives those sessions names, roles, repositories,
state, recovery, and a single place to operate them.

- **1–10 panes:** Claude, Codex, OpenCode, or PowerShell in any mix.
- **Multi-repository Desks:** every pane can start in a different working directory.
- **Visible state:** role, task, provider, attention, queue, repo, branch, and memory.
- **Keyboard control:** jump, zoom, send, restart, hand off, queue, inspect, recover.
- **Durable recovery:** SQLite-backed identities, focus, events, queues, and bounded
  failure receipts.
- **Operator boundaries:** previews and approvals around broad sends and protected work.

psmux owns the terminal panes. Flyt owns the boring-but-important layer above them:
identity, state, evidence, policy, and recovery. Boring infrastructure is underrated;
it is also usually the part keeping the demo alive on Tuesday.

## Five-minute start

### Requirements

- Windows 11
- PowerShell 7
- [psmux 3.4.0+](https://github.com/psmux/psmux)
- at least one provider CLI: Claude Code, OpenAI Codex, or OpenCode

Flyt releases are self-contained; the .NET SDK is only needed for development.

### Install while the repository is private

Authenticate GitHub CLI once, then download and inspect the installer before running it:

```powershell
gh auth login
$installer = Join-Path $env:TEMP 'install-flyt.ps1'
gh api repos/Pilvik1/Flyt/contents/scripts/install.ps1 `
  -H 'Accept: application/vnd.github.raw' | Set-Content $installer
pwsh -NoProfile -File $installer -InstallPsmux
Remove-Item $installer
```

When the repository becomes public, authentication is optional:

```powershell
$installer = Join-Path $env:TEMP 'install-flyt.ps1'
Invoke-WebRequest https://raw.githubusercontent.com/Pilvik1/Flyt/main/scripts/install.ps1 `
  -OutFile $installer
pwsh -NoProfile -File $installer -InstallPsmux
Remove-Item $installer
```

The installer detects x64/ARM64, verifies the release SHA-256, installs for the
current user, imports the PowerShell module, and rolls back failed upgrades.

### Configure a Desk

Run this in any repository:

```powershell
flyt doctor
flyt desk configure --path .
```

The guided flow asks for:

1. a Desk name;
2. **1–10 panes**;
3. Claude, Codex, OpenCode, or Shell for each pane;
4. the repository/working directory for each pane;
5. role and task labels; and
6. whether to open immediately.

Paths accept `.`, `..`, `~`, an absolute path, or a recent-repository number. Flyt
previews the complete pane-to-repository map before saving `.flyt/desk.json`.

Scripted configuration works too:

```powershell
flyt desk configure --path . --name product-review --panes 4 `
  --providers claude,codex,opencode,shell `
  --working-directories 'C:/src/api|C:/src/api|C:/src/web|C:/src/infra' `
  --open
```

## Cockpit controls

| Keys | Action |
|---|---|
| `Alt+1…9`, `Alt+0` | Jump to panes 1–10 |
| `Ctrl+b`, `A` | Next pane needing attention |
| `Ctrl+b`, `Z` | Zoom/unzoom the current pane |
| `Ctrl+b`, `S` | Send text to selected panes |
| `Ctrl+b`, `R` | Restart the active pane |
| `Ctrl+b`, `H` | Create a structured handoff |
| `Ctrl+b`, `Q` | Inspect or dispatch the queue |
| `Ctrl+b`, `L` | View recent output |
| `Ctrl+b`, `O` | Open the action menu |
| `Ctrl+b`, `?` | Show shortcuts when memory has unionized |

Useful commands:

```powershell
flyt desk open --path .
flyt desk status
flyt desk send --name product-review --role reviewer `
  --text 'Review the current diff' --yes
flyt desk recover --name product-review
flyt desk history --name product-review --search failure
```

The complete walkthrough lives in the [operator guide](docs/operator-guide.md),
with deeper cockpit behavior in [Desk cockpit](docs/desk-cockpit.md).

## What is shipped—and what is not

### Desk mode: shipped

- adaptive one-to-ten-pane psmux layouts;
- first-class Claude, Codex, OpenCode, and shell providers;
- multi-repository templates and provider resume identities;
- status reconciliation, pane focus, restart, log capture, and re-adoption;
- targeted multiline send with recipient previews;
- resource-gated queues, handoff bundles, failure receipts, and history;
- Windows notifications plus terminal fallbacks;
- ASCII, `NO_COLOR`, high-contrast, and small-terminal handling; and
- guided target-host acceptance with redacted receipts.

### Mission mode: foundation only

The state machine, acceptance plan, approval, events, and pause/resume/cancel paths
exist. Autonomous `plan → build → review → verify → integrate` execution does not.
The [roadmap](roadmap.md) says what remains. Marketing has been asked to remain calm.

## Architecture

```text
PowerShell module / flyt CLI
              │
    deterministic controller
      ┌───────┼────────┐
      │       │        │
  providers  Git     psmux
      │       │        │
      └───────┼────────┘
              │
      SQLite event ledger
```

Terminal text is useful evidence but never authoritative Mission state. Models can
propose and execute work; they cannot silently approve protected actions or promote
their own opinion into a passing verification result.

Read [architecture.md](architecture.md) and the decisions in [docs/decisions](docs/decisions/).

## Privacy and security

Flyt has no telemetry or hosted control plane. It does store local operational data
needed for recovery: repository paths, provider session IDs, launch commands, queue
payloads, events, plans, and bounded failure output. Failure text is best-effort
redacted before persistence and limited to the latest 20 receipts per Desk.

Provider CLIs have their own network and data flows. Flyt does not make Claude,
Codex, or OpenCode local merely by placing them inside a tasteful border.

Read [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md) before sharing state,
receipts, screenshots, or handoffs.

## Build from source

Requires the .NET 10 SDK.

```powershell
dotnet restore Flyt.slnx
dotnet build Flyt.slnx --configuration Release --no-restore
dotnet test Flyt.slnx --configuration Release --no-build
dotnet run --project src/Flyt.Cli -- doctor
```

For the PowerShell surface:

```powershell
Import-Module ./powershell/Flyt/Flyt.psd1 -Force
$env:FLYT_EXE = (Resolve-Path ./src/Flyt.Cli/bin/Release/net10.0/flyt.exe)
Test-FlytEnvironment
```

`FLYTV1_EXE`, `FLYTV1_STATE_DIR`, and the old `%LOCALAPPDATA%\FlytV1` state folder
remain supported for existing installations. New setups use `FLYT_EXE`,
`FLYT_STATE_DIR`, and `%LOCALAPPDATA%\Flyt`.

## Release evidence

Every release runs Ubuntu and Windows builds/tests. Windows CI additionally proves a
real ten-pane psmux lifecycle, multi-repository working directories, completion,
actions, stop/reopen, package install, and recovery after raw session loss.

Release assets include:

- self-contained x64 and ARM64 ZIPs;
- SHA-256 sidecars;
- an SPDX software bill of materials (the ingredient list); and
- provenance attestations once public GitHub hosting supports them for the repository.

Authenticated provider resume, visual quality, popup ergonomics, and visible Windows
notifications remain target-machine checks:

```powershell
flyt doctor --interactive --template default
```

## Inspiration

Flyt is inspired by:

- [lemonsaurus/agency](https://github.com/lemonsaurus/agency), an agent orchestration
  panel built for ultrawide, multi-agent work; and
- [asheshgoplani/agent-deck](https://github.com/asheshgoplani/agent-deck), a terminal
  session manager for multiple coding-agent CLIs.

They helped validate the core idea: agent sessions need an operating surface, not
another pile of tabs. Flyt takes its own Windows/PowerShell, psmux, durable-state,
multi-repository, and evidence-gated route. No source code from either project is
included here.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and
[SUPPORT.md](SUPPORT.md). The repository is being prepared in private; issues and
contributions open when the publication gate is approved.

## License

[MIT](LICENSE) © 2026 Håkon Pilvik. Commercial use is permitted under the license.
