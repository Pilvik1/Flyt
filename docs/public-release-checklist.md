# Public release checklist

Flyt is prepared in private before visibility changes. Publication remains an explicit
owner action because GitHub exposes the full history, releases, Actions history, and
repository metadata when a private repository becomes public.

## Candidate gates

- [ ] Exact `main` commit and release tag are aligned.
- [ ] Full-history Gitleaks scan is clean.
- [ ] Targeted personal-data and credential scan is reviewed.
- [ ] Historical author emails are explicitly accepted or a clean snapshot is chosen.
- [ ] MIT `LICENSE`, third-party notices, and SPDX SBOM are present.
- [ ] README, security, privacy, support, and contribution docs match shipped behavior.
- [ ] Ubuntu and Windows CI are green on the exact candidate.
- [ ] Both release ZIPs pass checksum and archive-integrity verification.
- [ ] Historical pre-public releases are explicitly accepted or converted to drafts.
- [ ] The target Windows acceptance receipt is complete.
- [ ] GitHub Discussions and private vulnerability reporting are enabled if desired.
- [ ] Branch rules and required status checks are active after the visibility change.
- [ ] Public installer path is tested without authentication.
- [ ] Repository description, topics, social preview, and homepage are set.

## Known publication decision

The existing Git history contains the maintainer's Gmail and NHH email addresses in
commit metadata. `.mailmap` presents those commits under a GitHub noreply identity,
but it does not erase the raw metadata. Do not rewrite shared history casually.
Choose one of:

1. accept permanent disclosure of those historical addresses; or
2. publish a fresh source snapshot in a new public repository.

Flyt does not need that decision while it remains private.
