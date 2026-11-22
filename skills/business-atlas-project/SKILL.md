---
name: business-atlas-project
description: Use authorized Business Atlas MCP tools to understand a project, investigate actual changes, submit reviewable proposals, and check delivery without approving or publishing knowledge.
---

# Business Atlas project work

Use the Business Atlas Streamable HTTP MCP endpoint `/mcp` with an OAuth access token whose audience is `business-atlas-api`. Do not put the token in arguments, files, prompts, or logs. The server checks the actual `iss`/`sub`, scope, project membership, resource ACL and policy epoch on every call. Tool names and arguments must match `contracts/mcp-tools.json`; never invent an operation or infer authorization from a URI, task text or model role.

## Required inputs and boundaries

Ask for or obtain `projectId`, the task or change goal, the approved baseline/read context, and the authenticated identity. For implementation work also use the exact task `contextId`, `taskId` and current `ifMatch` version. Read only the references and directories admitted by that context. Do not scan the entire knowledge base or repository to compensate for missing inputs. A new requirement is an entry point, not permission to change approved behavior or responsibilities. Treat unions such as A ∪ B as possible redundancy only after semantic verification. Preserve unknowns and conflicts; never manufacture business facts or verification.

The 35 exposed tools cover CS-01 through CS-15, but CS-12 is intentionally read/request-only: **there are no MCP approve or publish tools**. Human reviewers use authenticated Web/API review and publication. Models and agents submit candidates, never approved facts. No tool executes arbitrary shell commands, opens arbitrary URLs/files, or exposes environment variables and credentials.

## Workflow

1. **Discover and resolve (CS-01/02/03):** call `project.discover`; use `vocabulary.resolve`, `knowledge.search`, `knowledge.read`, and `knowledge.explain` only for the necessary roots. Ambiguous vocabulary remains ambiguous—do not choose the first match. Keep exact revision references, citations, unknowns and the resolved context digest.
2. **Bind the task (CS-04/05):** use `task.create` for a new lightweight intent, then `task.analyze_intent`. For an existing change, call `task.context.build` and subsequently `task.context.get`. If the context is not executable, stop writes and resolve missing approvals, stale dependencies or unknowns.
3. **Review and find candidates (CS-06/07):** call `content.review` for candidate content and `impact.find` for semantic candidate paths. A candidate relationship is not an actual repository investigation. Use returned job handles with `jobs.get` / `jobs.result` rather than claiming asynchronous work completed synchronously.
4. **Investigate actual evidence (CS-08):** request `impact.investigate`. An authorized external executor uses `work.list`, `work.claim`, `runs.start`, `runs.append_event`, `work.heartbeat`, and `work.finish`. Execute only the work request's admitted repository, commit, directories and tool capabilities. Record real capture provenance. If no repository/executor or usable evidence exists, return `need_evidence` / waiting state; never write a reconstructed summary as `rawCaptured`.
5. **Plan and propose (CS-09/11):** after investigations are ingested with `impact.submit` and dispositions are decided, call `change.plan`, then `proposal.create`. A proposal remains a draft. Wait for required human business approvals. Use `review.status` to read the authoritative review result. Do not retry around a denial.
6. **Implement, verify and close (CS-10/13/14):** implement only the authorized candidate. Use `conformance.check`, actual independent acceptance evidence, and `closure.check`. Use `delivery.list` and `delivery.reconcile` for actual PR/merge/deployment reconciliation. `ready`, a merged SHA or model confidence never directly marks the task complete or publishes knowledge.
7. **Feedback (CS-15):** use `feedback.create` for anchor-bound comments/questions and `issues.create` for actionable gaps. These create reviewable collaboration records, not direct revisions.

## Calling examples

Read a project overview:

```json
{
  "projectId": "11111111-1111-1111-1111-111111111111",
  "mode": "published",
  "purpose": "understand"
}
```

Start an intent-analysis job (the UUIDs are structure examples, not real business facts):

```json
{
  "projectId": "11111111-1111-1111-1111-111111111111",
  "taskId": "22222222-2222-2222-2222-222222222222",
  "idempotencyKey": "unique-operation-key-0001",
  "ifMatch": "\"3\"",
  "body": {
    "readContext": { "mode": "published", "purpose": "change" },
    "sourceRefs": []
  }
}
```

Read an authorized resource with `atlas://projects/{projectId}/units/{unitId}?baseline={baselineId}`. Re-read after membership, scope, baseline or policy changes; possession of the URI is not access.

## Results, retries and recovery

Successful structured results use `{code:"OK",requestId,traceId,context,data}`. Preserve job IDs, work IDs, run IDs, event cursors, aggregate versions, fences and checkpoints. A `202` equivalent is a job handle, not completion. Poll `jobs.get`, then `jobs.result` only when available. For reconnect/resume, use the last monotonic event cursor or persisted work/run checkpoint and reauthorize before consuming data.

Use the same `idempotencyKey` only for a byte-equivalent logical retry. Reusing it for different input is a conflict. Send the latest quoted aggregate version as `ifMatch`; after `CAS_MISMATCH`, reload and reassess instead of blindly retrying. A lease heartbeat and finish require the current fence; a stale fence cannot write. Exceeding budget or tool limits produces a resumable partial result, not more unbounded calls.

Report errors in this form:

```text
operation: <tool name and operationId>
status: ok | partial | need_evidence | conflict | forbidden | stale | failed
requestId/jobId/workId/runId: <returned handles, when present>
completed: <observed result with exact refs/evidence>
unknowns/conflicts: <unresolved items and required next action>
next: <safe retry, human decision, evidence, or reauthorization needed>
```

`isError=true` is a tool/business failure; a JSON-RPC transport/protocol error is separate. For `forbidden` do not search for another route. For stale context or revoked membership, stop using cached content and obtain a new authorized context. Dependency failure is not evidence and must not be replaced with a simulated response.

## Error-pattern preflight

Before a change, call `error_patterns.list` with relevant module/kind and changed `path`. Apply only current, verified, applicable patterns. Produce specific detector/regression tasks for issues such as broad traversal presented as investigation, stale conformance labels, read-role writes, lost flow guards, or newer feedback overwriting older rules. Avoid unrelated blanket blocks and never append an unbounded history log to the task prompt.
