# Security policy

Flyt launches coding agents with access to local repositories. Provider output is
untrusted input; prompts are not a security boundary.

## Supported versions

Security fixes target the latest release. Older pre-1.0 releases are not maintained.

## Report a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/Pilvik1/Flyt/security/advisories/new).
Do not include vulnerabilities, credentials, provider transcripts, or exploit
details in a public issue. While the repository remains private, contact the owner
through an existing private channel.

Expect an acknowledgement within seven days. No bug bounty is currently offered.

## Security boundaries

- Credentials remain in provider-native login stores or operator-approved
  environment mechanisms. Flyt has no credential vault.
- Flyt persists local operational metadata needed for recovery, including workspace
  paths, provider session IDs, launch commands, queue payloads, event details, and
  bounded failure output. See [PRIVACY.md](PRIVACY.md).
- Failure output and launch commands pass through best-effort secret redaction before
  persistence. Redaction reduces accidental exposure; it cannot recognize every
  secret format. Do not send secrets to agent panes.
- Failure receipts retain the latest 20 records per Desk and cap stored text at
  32 KiB per field.
- Worker text cannot approve protected actions. External paid services, production
  changes, system-wide installs, material deletion, security weakening, and writes
  outside the approved workspace require an operator decision.
- Provider processes and verifier commands receive explicit working directories and
  timeouts. A provider still has the permissions of the account that launched it.
- Repository deletion/archival, public visibility changes, protected-branch force
  pushes, and unconfirmed remote-branch deletion are outside Flyt's authority.

## Release integrity

Release ZIPs have SHA-256 sidecars and an SPDX SBOM. GitHub artifact provenance is
generated when the repository is public and the hosting plan supports attestations.
Windows binaries are not yet Authenticode-signed; verify the checksum and provenance
before installation.
