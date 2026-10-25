# Privacy and local data

Flyt has no telemetry, analytics, crash upload, account system, or hosted control
plane. The Flyt controller itself does not send repository content to a Flyt server
because no such server exists—a rare victory for architecture diagrams everywhere.

## Data stored locally

Flyt's SQLite state stores operational data required for recovery and coordination:

- Desk and pane names, roles, tags, state, and stable identifiers;
- workspace and per-pane working-directory paths;
- provider launch commands and provider session IDs used for resume;
- queue payloads, mission goals, acceptance criteria, plans, approvals, and events;
- bounded, best-effort-redacted failure output; and
- target-host acceptance receipts when the operator requests them.

Repository-local `.flyt/` data may contain Desk templates and handoff bundles. A
handoff can contain diffs or file references. `.flyt/` and local SQLite files are
ignored by Git by default, but users remain responsible for reviewing staged files.

Failure receipts retain the latest 20 records per Desk. Persisted text is capped at
32 KiB per field. Other Desk and Mission history currently remains until the local
state database is removed.

## External data flows

Claude Code, OpenAI Codex, OpenCode, Git, GitHub CLI, package managers, and any shell
commands launched in panes operate under their own configuration and privacy terms.
Those tools may transmit prompts, repository content, diagnostics, or usage data to
their providers. Flyt does not proxy, inspect, or override those flows.

The installer contacts GitHub to discover and download releases. CI contacts NuGet,
GitHub, and the pinned psmux release URL.

## Operator guidance

- Keep credentials in provider-native stores or approved secret managers.
- Treat provider session IDs as sensitive metadata.
- Review `.flyt/`, acceptance receipts, screenshots, and SQLite exports before sharing.
- Use a separate worktree or account boundary when an agent should not see a file.
- Deleting the Flyt state directory removes local controller state but does not delete
  provider-side conversations or data.
