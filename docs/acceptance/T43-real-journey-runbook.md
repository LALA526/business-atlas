# T43 real user and Agent acceptance

`tests/EndToEnd` evaluates AT-P01–AT-P14 and J1–J9 against attributable evidence and, when configured, the deployed API. The baseline A–E examples remain synthetic semantic fixtures; they never qualify as the real CPQ/authorized-business pilot or as human approval.

## Required evidence

A case uses JSON schema `1.0` and must include:

- `businessMode`: `real_cpq` or `authorized_real_business`, plus the authorization and exact source captures;
- an independent reference answer created before the tested Agent run, with the reviewer identity and evidence;
- exactly one result for every AT-P01–AT-P14 and J1–J9, with `satisfied | missing | contradictory | unverified | not_applicable`, a reason, requirement IDs and SHA-256-bound evidence;
- actual human decisions for scope/business outcomes. `delegated_ai` and automated actors are not human approvers; `not_applicable` needs a referenced human scope decision;
- the actual provider, model/version, run ID, timing and available token/cost observations;
- same-material original-workflow and Atlas observations, including applicable denominator, critical correctness/omissions, false affirmations/positives, explicit unknowns, review rounds, human time and model cost (use `null` for unknown cost, never an invented zero);
- the authorized `BusinessAtlas-Self` baseline, a second real change, a later task context that reused approved knowledge, and publication of that second change;
- a real PR, final merged commit, CI, human knowledge approval, published baseline, deployment and retry/no-duplicate evidence.

Evidence uses an HTTPS URL or URN and the SHA-256 of the exact captured bytes. Placeholder hosts and synthetic success are rejected. A report is `blocked` until all blockers are resolved; the runner does not manufacture real CPQ facts or approvals.

## Execute

Run the evidence validator tests:

```sh
dotnet test tests/EndToEnd/Atlas.EndToEnd.Tests.csproj --filter 'FullyQualifiedName!~Live_case'
```

Run the live journey with an API bearer token supplied through the process environment (never put it in the case or report):

```sh
ATLAS_T43_CASE=/secure/path/t43-case.json \
ATLAS_E2E_BASE_URL=https://atlas.example \
ATLAS_E2E_ACCESS_TOKEN="$ATLAS_ACCESS_TOKEN" \
ATLAS_T43_REPORT=/secure/path/t43-report.json \
dotnet test tests/EndToEnd/Atlas.EndToEnd.Tests.csproj --filter Live_case
```

The live test always probes `/health/ready`. Add same-origin, read-only `liveProbes` to bind the case to actual project resources. Each probe has `name`, `path`, `expectedStatus` and optional RFC 6901 `jsonPointer`/`expectedValue`. The deployed run fails with explicit blockers on unavailable dependencies, authorization errors, stale/missing evidence, or semantic journey failure. The xUnit test is skipped when live configuration is absent; that skip is not acceptance evidence.
