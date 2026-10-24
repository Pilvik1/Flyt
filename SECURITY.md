# Security

FlytV1 runs coding agents with access to local repositories. Treat provider output as untrusted instructions and keep host policy enforcement outside model prompts.

## Report a vulnerability

This is a private personal repository. Report security findings directly to the repository owner through a private channel. Do not open a public disclosure.

## Invariants

- Credentials remain in provider-native login stores or approved environment mechanisms.
- Flyt never intentionally writes secret values to SQLite, configuration, logs, or evidence.
- Worker text cannot approve protected actions.
- Repository deletion/archival, public visibility changes, protected-branch force pushes, and unconfirmed remote-branch deletion are prohibited.
- External paid services, production changes, system-wide installs, material deletion, security weakening, and writes outside the approved workspace require an operator decision.
- Provider processes and verifier commands receive explicit working directories and timeouts.

