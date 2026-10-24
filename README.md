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
- SQLite-backed mission/event state;
- evidence-bearing mission state machine;
- initial tests and Windows CI.

Native Windows integration with psmux, Claude Code, and Codex must pass the Phase 0 proof before the project is considered usable.

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
dotnet run --project src/Flyt.Cli -- mission create --goal "Add CSV import" --path .
dotnet run --project src/Flyt.Cli -- mission list
dotnet run --project src/Flyt.Cli -- mission approve --id <mission-id>
```

## Safety defaults

- One write owner per worktree.
- Deterministic verification outranks agent opinion.
- Secrets never belong in Flyt state.
- Material deletion, system-wide installs, external paid services, and security weakening require approval.
- No repository deletion/archival, public visibility changes, protected-branch force pushes, or unconfirmed remote-branch deletion.
- Missions stop on success, blocker, policy approval, or hard limits.

## License

Private repository. All rights reserved.
