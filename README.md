# Business Atlas — Mac development bootstrap

This repository currently contains the T1.0 input baseline, frozen UI references,
OpenCode role definitions, and preparation/CI infrastructure. The Business Atlas
product has not been implemented. All original T01–T44 remain planned.

Developer host: Mac16 (M1, 16GB), /Users/fox/Workspace/projects/business-atlas.
Runtime host: Mac8 (M1, 8GB), isolated atlas-deploy directories and Colima profile.
AI engine: OpenCode 1.19.5 with authenticated Kimi Code. RoutinAI is not required.

Use source ~/Workspace/projects/business-atlas-preflight/env.sh before local tools.
The baseline is docs/baselines/T1.0. Its original bytes remain authoritative input.
UI reference: docs/ui-reference/working-v9.4.tar.gz, frozen from the user's current
uncommitted prototype working copy. The V9.3 Git checkpoint is also preserved.
UI appearance is a reference; localStorage and simulated approval/run/deploy
behavior are not production implementations.

The initial Azure pipeline validates preparation and deploys an explicitly
labelled preflight container, not Business Atlas. Full product CI, migrations,
security, authentication and deployment remain original implementation tasks.

Daily implementation and technical review are delegated to AI by the user's
2026-11-20 instruction. AI decisions must be identified as AI decisions. No agent
may invent real CPQ facts, human signatures, CI results, or deployment evidence.
Unknown inputs block their affected tasks without fabricating completion.

Never commit model credentials, Azure PATs, private keys or database passwords.
The coordinator uses private host configuration; product source does not store it.
