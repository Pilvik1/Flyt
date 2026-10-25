# Public release checklist

Flyt is prepared in private before visibility changes. Publication remains an explicit
owner action because GitHub exposes the full history, branches, releases, Actions
history, and repository metadata when a private repository becomes public.

The intended publication model is an MIT-licensed open-source repository with public
source and downloadable Windows binaries. MIT permits modification, redistribution,
and commercial use. The Flyt name and project identity are not separately licensed as
trademarks.

## Pre-public candidate gates

- [ ] Exact `main` commit, package version, PowerShell module version, and release tag are aligned.
- [ ] Full-history Gitleaks scan is clean on the exact candidate.
- [ ] Targeted personal-data, private-path, and credential scan is reviewed.
- [ ] Historical author and tagger emails are explicitly accepted or a clean snapshot is chosen.
- [ ] Historical releases and every remote branch are explicitly accepted or sanitized.
- [ ] MIT `LICENSE`, corrected third-party notices, bundled legal texts, and SPDX SBOM are present.
- [ ] README, security, privacy, support, and contribution docs match public behavior.
- [ ] Ubuntu and Windows CI are green on the exact candidate.
- [ ] Both release ZIPs pass checksum, decompression, legal-file, and SBOM verification.
- [ ] The target Windows acceptance receipt is complete.

## Cutover gates

- [ ] Repository visibility is changed only after explicit owner approval.
- [ ] Private vulnerability reporting is enabled and its form is reachable.
- [ ] GitHub secret scanning and push protection are enabled.
- [ ] CodeQL is manually dispatched and passes after the repository becomes public.
- [ ] Branch rules and required status checks remain active after the visibility change.
- [ ] The raw installer and latest release download work without authentication.
- [ ] A post-public release produces GitHub artifact attestations.
- [ ] Repository description, topics, social preview, and homepage are reviewed.
- [ ] GitHub Discussions is enabled only if it will be actively used.

## Known publication decisions

### Historical identity metadata

The existing Git history contains the maintainer's Gmail and NHH email addresses in
commit metadata. Older annotated tags also contain the Gmail address. `.mailmap`
presents commits under a GitHub noreply identity, but does not erase raw metadata and
contains the old addresses itself.

Choose one:

1. explicitly accept permanent disclosure and publish the existing repository; or
2. publish a fresh source snapshot in a new public repository, omitting or sanitizing
   `.mailmap` while preserving required copyright and attribution.

Do not rewrite the shared repository history as routine cleanup.

### Existing GitHub objects

All existing releases and remote branches become public with the repository. Review
them as disclosure surfaces, not merely as navigation clutter. Deleting remote branches
requires explicit owner confirmation; converting or replacing historical releases must
be handled as a separate release-management decision.

### Product maturity

Flyt may be published as beta software after the target-host acceptance receipt passes.
The five-real-Missions/two-week roadmap goal remains a stable-release maturity gate, not
a prerequisite for sharing the source as a clearly labeled beta.
