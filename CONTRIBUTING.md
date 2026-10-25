# Contributing to Flyt

Thanks for considering a contribution. Flyt is young, Windows-first, and
deliberately allergic to cleverness that makes recovery harder.

## Before opening a pull request

1. Open an issue for material behavior or architecture changes.
2. Keep one concern per pull request.
3. Never include credentials, provider transcripts, local databases, or real
   session IDs in issues, fixtures, screenshots, or logs.
4. Add or update tests for behavior changes.
5. Run the local gate:

```powershell
dotnet restore Flyt.slnx
dotnet format Flyt.slnx --verify-no-changes
dotnet build Flyt.slnx --configuration Release --no-restore
dotnet test Flyt.slnx --configuration Release --no-build
dotnet list Flyt.slnx package --vulnerable --include-transitive
```

Windows integration changes should also run `scripts/phase0-windows.ps1` where
practical. CI owns the final clean-room psmux proof.

## Design rules

- Deterministic state beats inferred terminal text.
- One write owner per worktree.
- Provider output is untrusted input.
- Persist the minimum evidence needed for recovery.
- Preserve the operator approval boundary.
- Keep changes readable enough that recovery at 02:00 does not require archaeology.

## Commit and contribution terms

Use clear imperative commit messages. By contributing, you agree that your
contribution is licensed under the repository's MIT License. Flyt does not
currently require a CLA or DCO sign-off.

For vulnerabilities, follow [SECURITY.md](SECURITY.md), not the public issue tracker.
