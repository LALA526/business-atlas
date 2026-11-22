# Business Atlas Self Project knowledge

This folder records how the product describes its own requirements, source code,
tests, decisions, and releases. These files are not hidden demo data. They are
versioned candidates linked to the real repository.

## Current assets

- `baseline/self-bootstrap.akp.json` is the initial candidate generated from
  commit `95783495c5f79838b03330997d4f4f701bd7966e`.
- `baseline/self-change-T36.akp.json` is a later candidate for a real repository
  interval ending at that same commit.
- `candidates/AKP-SELF-5aeaac1e2254.json` is a fresh candidate generated from
  code commit `5aeaac1e22545b093cee019006c3a80028690765`, covering 22 entities
  and 23 exact Git sources. The package itself is recorded in the following
  repository commit and does not claim to describe that later commit.
- `candidates/AKP-BUSINESS-ATLAS-SELF-2026-11.json` is the expanded Self Project
  candidate. It includes every controlled T1.0 requirement and task, product
  design sources, current implementation paths, and the actual PR 78 / Build 103
  records. It binds the exact Git revision used by `scripts/self-project/build_case.py`.
- `proposals/KCP-T02-001.json`, `KCP-T31-001.json`, `KCP-T31-002.json`, and
  `KCP-T38-001.json` record engineering and knowledge-delivery changes.
- `scripts/self-project/self_project.py` builds and verifies AKP candidates from
  Git objects and exact source hashes.

## Publication state

The two checked-in baseline AKP files are stale relative to the current repository.
The candidates are bound to their stated code commits, but remain distinct from
normal product import, human approval, and publication. The T31 and T38 proposal records still contain
`pullRequestId: PENDING`. No checked-in artifact proves human knowledge approval,
publication into the live Self Project, or release reconciliation. Do not present
these candidates as an approved source of truth.

New candidates belong under `knowledge/candidates/`, must bind an exact commit,
and must pass `scripts/self-project/self_project.py verify`. Import, review,
approval, publication, and deployment reconciliation remain separate recorded
operations. No agent may invent a human approval.
