Mac16 T1.0 native Multica execution

Current workspace/role/task bindings: coordination/multica-workitems.json.
The dedicated atlas-mac16 Multica profile executes every role with OpenCode 1.19.5
and Kimi on the pinned Mac16 daemon. Role projects carry the exact task worktree
resource, updated before dispatch. Role-run subissues are not requirement completion.
The host coordinator retains the original T1.0 DAG, durable leases, independent
candidate review/QA, deterministic verification, Azure PR policy and deployment gates.
Every scoped requirement delivery has a separate PR with requirement IDs.
Canonical Multica issues become done only after actual merged PR and deployment.
No Windows workspace, APP/ARC issues, agents or code acceptance are carried forward.

source ~/Workspace/projects/business-atlas-preflight/env.sh
atlasctl prepare / preflight / start / status / stop
Current private state: business-atlas-preflight/runner-state-multica
Historical direct-runner state is preserved under runner-state and stays disabled.
The Multica native daemon and host coordinator run under launchd, without a GUI login.
Leader only returns a read-only plan; the coordinator persists it outside Git.
