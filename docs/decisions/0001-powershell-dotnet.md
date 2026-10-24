# ADR 0001: PowerShell surface with a .NET core

## Status

Accepted.

## Decision

Expose Flyt through a PowerShell module while implementing concurrency, persistence, process supervision, recovery, and terminal rendering in a .NET 10 executable.

## Rationale

PowerShell is the operator environment. A compiled core is easier to test and safer for concurrent structured streams, SQLite transactions, restart recovery, and responsive terminal behavior than a PowerShell-only implementation.

