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

## Self-contained Mac8 LAN preview

`azure-pipelines.yml` publishes and deploys PostgreSQL, Keycloak, the API, worker,
and web application. `scripts/fast/deploy.py` generates strong secrets once in
`~/atlas-deploy/product/secrets.json` and writes the initial application and
Keycloak administrator credentials to the host-only, mode-0600 file
`~/atlas-deploy/product/initial-login.json`. PostgreSQL and API data-protection
keys use persistent Docker volumes. The API applies its EF Core migrations on
startup against the deployed Atlas database.

This deployment is explicitly an HTTP LAN preview at
`http://192.168.10.10:15809`; only that deployment sets
`Authentication__AllowInsecureLanPreview=true`. The application otherwise keeps
secure cookies and HTTPS OIDC metadata requirements. A public/production release
must use HTTPS, external production identity configuration, and must not enable
the LAN preview switch.
