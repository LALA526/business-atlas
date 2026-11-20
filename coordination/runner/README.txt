Business Atlas pre-development runner
=====================================

Scope
-----
This is coordination infrastructure only. It does not implement Business Atlas
product features, alter the immutable T1.0 DAG, approve its own output, push Git
branches, or start continuous development. Python 3.13 standard library is the
only runner/test dependency.

Commands (run from the repository root)
---------------------------------------
  python3.13 coordination/runner/atlas_runner.py init
  python3.13 coordination/runner/atlas_runner.py status
  python3.13 coordination/runner/atlas_runner.py preflight
  python3.13 coordination/runner/atlas_runner.py run-once
  python3.13 coordination/runner/atlas_runner.py loop

Optional arguments: --repo PATH, --state PATH, --controls PATH.

init imports every task and dependency from docs/baselines/T1.0/03_开发执行/
task-dag.json. It never writes task status back to that file or coordination/
tasks.json. Repeating init is byte-digest checked and does not duplicate tasks.
The SQLite ledger, persistent single-writer lock, evidence, logs, worktrees,
STOP checkpoint marker and preflight manifest default to the external directory
/Users/fox/Workspace/projects/business-atlas-preflight/runner-state.

Safety and execution gates
--------------------------
* controls.json ships with enabled=false and model.verified=false. loop refuses
  to run while enabled is false. run-once and loop both require a passing real
  preflight; preflight does not assume credentials, model validation, CI or
  deployment readiness.
* Real preflight requires a committed Git base and identity, the unchanged
  44-task baseline, a committed verification policy, an available OpenCode CLI,
  explicit prior verification of the exact Kimi model, Azure credentials, named
  required checks and an HTTPS deployment reconciliation endpoint. The current
  uncommitted repository therefore cannot enter real execution.
* Every role gets a new `opencode run --format json --agent ROLE -m MODEL`
  invocation. There is no --continue and no automatic RoutinAI fallback. stdin
  is /dev/null. Each call has separate stdout/stderr evidence files, a timeout
  and its own process group. JSONL error events, empty text and malformed JSONL
  fail even when the process exits zero.
* 401/403 and 402 stop without blind retry. 429/5xx use at most two retries.
  Provider-blocked loop execution stops instead of trying another provider.
* leader -> developer -> independent reviewer -> independent QA -> integration
  is persisted in SQLite. A new attempt resumes at the first unaccepted stage.
  Accepted artifacts and all logs remain outside the repository.
* Development uses task/Txx on a separate worktree. Review and QA use separate
  detached worktrees at the exact committed candidate SHA. Reviewer and QA must
  emit only {verdict, findings, evidence}; a pass needs evidence locators.
  Tracked-source or HEAD mutation invalidates acceptance.
* QA commands come only from the committed coordinator policy. scripts/ci/verify
  is used when present. T01/T02 have explicit bootstrap entries naming
  scripts/ci/verify_task.py; those task implementations must provide the actual
  verifier. A missing verifier is a failure, never a skipped gate. Model text is
  never executed as a command.
* Integration is injectable. The real Azure backend reuses/creates a PR and
  requires the unchanged candidate source SHA, every configured succeeded
  check, an actual merge SHA, and a successful deployment record for that merge
  SHA. Queued builds, pending integration, local commits, tests and missing
  deployment records are not done. Unavailable integration remains
  pending_integration. No completion is possible without persisted leader,
  developer, reviewer and QA acceptance for the current candidate.
* Claims use BEGIN IMMEDIATE, lease expiry and a monotonically increasing fence.
  Every mutation checks owner/fence. Expired leases are reclaimed only after
  base/candidate Git and accepted-stage reconciliation. The runner intentionally
  permits one running task. Lease seconds must exceed the child timeout.
* Stop controls are checked between stages. Create external runner-state/STOP or
  set stopRequested=true. SIGTERM/SIGINT stop only runner-owned child process
  groups and checkpoint the current unaccepted stage. Accepted stages are not
  rerun after restart.

Configuration
-------------
Copy controls.json before customizing real endpoints. Do not put PATs or model
keys in it. Azure PAT is read from AZURE_DEVOPS_PAT by default. The deployment
status URL must return JSON with status="succeeded", commitSha equal to the
actual merge SHA and a nonempty deploymentId. requiredContexts must name the
checks that are mandatory; an empty list fails preflight.

Status is structured JSON and omits credential values and raw model/provider
error text. Raw stdout/stderr remain only in external evidence logs, which must
be access-controlled because model output can still contain sensitive source.

Tests
-----
  python3.13 -m unittest discover -s coordination/runner/tests -v

Tests use deterministic fake role/integration adapters and local temporary Git
repositories. They perform no network, model, push, Azure or product-development
operation. The real adapter is the only production model execution path.

Known boundaries
----------------
This is not an Azure Boards synchronizer, general-purpose agent platform or
Multica implementation. It does not heartbeat during a child call; the lease
must therefore comfortably exceed configured child/integration waits. External
PR/check/deployment semantics must be validated against the authorized Azure
project before enabling real mode. Worktrees and evidence are retained for
recovery/audit rather than automatically deleted.
