# Business Atlas

Business Atlas is an implemented knowledge-governance product with a Web/API/Worker
stack. The source in `main` is deployable; final T1.0 product acceptance is still
separate and is not declared complete by a merged task or a healthy preview.

Default build host: Mac16 (M1, 16GB). The product owner authorized this Windows clone for the current UI and Self Project corrections; production deployments still run through the configured pipeline.
Runtime host: Mac8 (M1, 8GB), isolated atlas-deploy directories and Colima profile.
AI engine: OpenCode 1.19.5 with authenticated Kimi Code. RoutinAI is not required.

Use source ~/Workspace/projects/business-atlas-preflight/env.sh before local tools.
The baseline is docs/baselines/T1.0. Its original bytes remain authoritative input.
UI direction: retain the V9.2/V9.3 project workspace, six-entry navigation,
human-readable project overview and reading patterns. The V9.4 working copy is
preserved as history, but it is not the product design target. See
`docs/ui-reference/README.md` and `docs/baselines/T1.0/05_原始基线/V9.3原评审报告.md`.

Azure Pipelines builds the API, worker and Web app, then deploys to Mac8. Product
authentication, data, migrations and operational behavior live in the application
and must be validated against the deployed release; a green readiness endpoint is
not a full product acceptance result.

Daily implementation and technical review are delegated to AI by the user's
2026-11-20 instruction. AI decisions must be identified as AI decisions. No agent
may invent real CPQ facts, human signatures, CI results, or deployment evidence.
Unknown inputs block their affected tasks without fabricating completion.

Never commit model credentials, Azure PATs, private keys or database passwords.
The coordinator uses private host configuration; product source does not store it.

## Preview and self knowledge

`azure-pipelines.yml` publishes and deploys PostgreSQL, Keycloak, the API, worker,
and web application. `scripts/fast/deploy.py` generates strong secrets once in
`~/atlas-deploy/product/secrets.json` and writes the initial application and
Keycloak administrator credentials to the host-only, mode-0600 file
`~/atlas-deploy/product/initial-login.json`. PostgreSQL and API data-protection
keys use persistent Docker volumes. The API applies its EF Core migrations on
startup against the deployed Atlas database.

The runtime is installed at `https://atlas.lalapilot.com`. Deployment creates a
host-private local CA and a server certificate covering the LAN address and
loopback. Authorized clients must trust the CA identified by `caCertificate` in
`initial-login.json`; the CA private key and all credentials remain on the runtime
host. Browser cookies and the public OIDC issuer are HTTPS-only, while OIDC
discovery, signing-key, token and user-info requests use the private container
network (avoiding a LAN hairpin without changing the issuer).
The non-secret `scripts/fast/secrets.env.example` lists required configuration
names without containing credentials. Never copy login passwords from host-only
configuration into Git or task output.

`knowledge/README.md` is the entry point for Business Atlas Self Project knowledge.
AKP packages and KnowledgeChange proposals are evidence-bearing candidates. They
remain distinct from human-reviewed, published knowledge baselines.

## Verification and acceptance

Run focused checks from a clean checkout:

```sh
dotnet test tests/Domain/Atlas.Domain.Tests.csproj -c Release
cd apps/web && npm ci && npm run typecheck && npm run build
```

The latest handoff is in `docs/release/t1.0-handoff/HANDOFF.md`. It records the
current acceptance state and must be updated with real requirement, build, test,
knowledge and deployment evidence. Do not infer a complete product acceptance
from task board status or deployment alone.
