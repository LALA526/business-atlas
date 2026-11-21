using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using TaskCollab.Contracts;
using TaskCollab.Impact;

namespace TaskCollab.Infrastructure;

public sealed class PostgresTaskCollabStore(string connectionString, PostgresAccessApplicabilityStore access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ChangeTypes = ["add", "revise", "retire", "preserve", "revalidate"];
    private static readonly HashSet<string> Dispositions = ["required", "potential", "no_change", "deferred", "unknown"];
    private static readonly HashSet<string> InspectionStates = ["candidate", "inspected", "verified", "stale"];
    private static readonly HashSet<string> CloseStates = ["open", "resolved", "deferred"];
    private static readonly HashSet<string> Risks = ["low", "medium", "high", "critical"];
    private static readonly HashSet<string> CaptureKinds = ["rawCaptured", "verifiedScript", "reconstructed", "proposed", "humanAttestation"];

    public async Task<TaskPage> ListTasksAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var after = Cursor(cursor);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        var ids = new List<Guid>();
        await using (var command = Command(tx))
        {
            command.CommandText = "SELECT task_id FROM collab.task WHERE project_id=@project AND (@after IS NULL OR task_id>@after) ORDER BY task_id LIMIT @limit";
            Add(command, "project", project); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }
        var next = ids.Count > limit ? Encode(ids[limit - 1]) : null; if (ids.Count > limit) ids.RemoveAt(limit);
        var items = new List<TaskView>();
        foreach (var id in ids)
        {
            var task = await ReadTask(tx, project, id, false, ct);
            if (task.RootUnitId.HasValue && !await CanRead(principal, project, task.RootUnitId.Value, null, ct)) continue;
            items.Add(task);
        }
        return new(items, next);
    }

    public async Task<TaskView> GetTaskAsync(Guid principal, Guid project, Guid task, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        var result = await ReadTask(tx, project, task, false, ct);
        if (result.RootUnitId.HasValue) await AuthorizeUnit(principal, project, result.RootUnitId.Value, null, ct);
        return result;
    }

    public async Task<TaskView> CreateTaskAsync(AtlasPrincipal actor, Guid project, CreateTaskRequest request, string key, string requestId, string? trace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Intent) || request.Intent.Length > 20000) throw ProjectIdentityException.Invalid("A non-empty intent of at most 20000 characters is required.");
        if (request.Title is not null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 500)) throw ProjectIdentityException.Invalid("Task title must contain 1-500 characters.");
        var digest = Digest(request);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); var membership = await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        if (await Replay<TaskView>(tx, project, actor.PrincipalId, "createTask", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var baseline = request.BaseBaselineId ?? membership.Baseline;
        if (!await SealedBaseline(tx, project, baseline, ct)) throw ProjectIdentityException.Invalid("The base baseline must be sealed and belong to the project.");
        if (request.RootUnitId.HasValue) await AuthorizeUnit(actor.PrincipalId, project, request.RootUnitId.Value, null, ct);
        foreach (var reference in request.SourceRefs ?? []) await AuthorizeExact(tx, actor.PrincipalId, project, reference, ct);
        var id = Guid.NewGuid(); var title = request.Title?.Trim() ?? Title(request.Intent);
        var summary = JsonSerializer.Serialize(new { targetChanges = Array.Empty<object>(), preserve = Array.Empty<object>(), unknowns = new[] { new { field = "targetChanges", question = "What observable outcome should change?", reason = "Intent capture does not establish a target contract." } }, nextAction = "Analyze intent and clarify the desired outcome", sourceRefs = request.SourceRefs ?? [] }, Json);
        await Execute(tx, "INSERT INTO collab.task(project_id,task_id,root_unit_id,base_baseline_id,intent,title,classification,state,target_summary,created_by) VALUES(@project,@id,@root,@baseline,@intent,@title,'unknown','captured',@summary::jsonb,@actor)", ct,
            ("project", project), ("id", id), ("root", Db(request.RootUnitId)), ("baseline", baseline), ("intent", request.Intent.Trim()), ("title", title), ("summary", summary), ("actor", actor.PrincipalId));
        var result = await ReadTask(tx, project, id, false, ct);
        await Save(tx, project, actor.PrincipalId, "createTask", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public Task<TaskView> AmendTaskAsync(AtlasPrincipal actor, Guid project, Guid task, AmendTaskRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) =>
        MutateTask(actor, project, task, "amendTask", expected, key, request, requestId, trace, async (tx, row) =>
        {
            if (row.State is "completed" or "cancelled") throw ProjectIdentityException.Conflict("A completed or cancelled task cannot be amended.");
            if (request.Title is not null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 500)) throw ProjectIdentityException.Invalid("Task title must contain 1-500 characters.");
            if (request.TargetChanges is not null) ValidateChanges(request.TargetChanges);
            if (request.ExternalRef.HasValue && request.ExternalRef.Value.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("externalRef must be an object.");
            foreach (var reference in request.Preserve ?? []) await AuthorizeExact(tx, actor.PrincipalId, project, reference, ct);
            foreach (var reference in (request.TargetChanges ?? []).SelectMany(change => new[] { change.Current, change.Candidate }).OfType<RevisionRef>()) await AuthorizeExact(tx, actor.PrincipalId, project, reference, ct);
            var summary = Summary(row.Summary); if (request.TargetChanges is not null) summary.TargetChanges = request.TargetChanges; if (request.Preserve is not null) summary.Preserve = request.Preserve;
            if (request.TargetChanges is not null) summary.Unknowns = summary.Unknowns.Where(item => item.Field != "targetChanges").ToArray();
            await Execute(tx, "UPDATE collab.task SET title=COALESCE(@title,title),target_summary=@summary::jsonb,external_ref=COALESCE(@external::jsonb,external_ref),version=version+1 WHERE project_id=@project AND task_id=@task", ct,
                ("project", project), ("task", task), ("title", Db(request.Title?.Trim())), ("summary", JsonSerializer.Serialize(summary, Json)), ("external", Db(request.ExternalRef?.GetRawText())));
        }, ct);

    public Task<JobView> AnalyzeIntentAsync(AtlasPrincipal actor, Guid project, Guid task, AnalyzeIntentRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) =>
        MutateTask(actor, project, task, "analyzeIntent", expected, key, request, requestId, trace, async (tx, row) =>
        {
            _ = await access.CreateReadContextAsync(actor.PrincipalId, project, request.ReadContext, ct);
            foreach (var reference in request.SourceRefs) await AuthorizeExact(tx, actor.PrincipalId, project, reference, ct);
            var summary = Summary(row.Summary); summary.SourceRefs = request.SourceRefs;
            var hasTarget = summary.TargetChanges.Count > 0;
            summary.Unknowns = hasTarget ? summary.Unknowns.Where(item => item.Field != "targetChanges").ToArray() : [new Unknown("targetChanges", "What observable outcome should change?", "The original wording alone is not a complete contract.")];
            summary.NextAction = hasTarget ? "Review candidate impacts and unresolved semantics" : "Answer the clarification question before planning a change";
            var classification = hasTarget || ContainsAny(row.Intent, "修改", "变更", "新增", "删除", "change", "add", "remove") ? "business_change" : ContainsAny(row.Intent, "重构", "实现", "refactor", "implementation") ? "implementation_only" : "unknown";
            var job = Guid.NewGuid(); var resultRef = $"task:{task}:intent:{row.Version + 1}";
            await Execute(tx, "UPDATE collab.task SET classification=@classification,target_summary=@summary::jsonb,version=version+1 WHERE project_id=@project AND task_id=@task; INSERT INTO ops.job(project_id,job_id,kind,requester_id,state,input,result,attempt,max_attempt) VALUES(@project,@job,'intent_analysis',@actor,'succeeded',@input::jsonb,@result::jsonb,0,1)", ct,
                ("project", project), ("task", task), ("classification", classification), ("summary", JsonSerializer.Serialize(summary, Json)), ("job", job), ("actor", actor.PrincipalId),
                ("input", JsonSerializer.Serialize(request, Json)), ("result", JsonSerializer.Serialize(new { resultRef, needsClarification = !hasTarget }, Json)));
            row.PendingJob = new(job, "intent_analysis", "succeeded", 1, 0, 1, resultRef, null, summary.Unknowns);
        }, ct);

    public Task<JobView> FindImpactsAsync(AtlasPrincipal actor, Guid project, Guid task, FindImpactsRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) =>
        MutateTask(actor, project, task, "findImpacts", expected, key, request, requestId, trace, async (tx, row) =>
        {
            if (!ChangeTypes.Contains(request.ChangeType) || request.RootRefs.Count == 0 || request.RootRefs.Count > 100) throw ProjectIdentityException.Invalid("A supported changeType and 1-100 rootRefs are required.");
            var read = await access.CreateReadContextAsync(actor.PrincipalId, project, request.ReadContext, ct);
            var action = read.Mode == ReadMode.Historical ? "read_history" : "read";
            var queue = new Queue<ImpactWorkItem>();
            var visited = new HashSet<ImpactVisit>();
            var candidates = new Dictionary<Guid, ImpactCandidate>();
            var rootUnits = request.RootRefs.Select(root => root.UnitId).ToHashSet();
            var blindSpots = new List<ImpactBlindSpot>();
            var blindSpotUnits = new HashSet<Guid>();
            foreach (var root in request.RootRefs)
            {
                var baselineRevision = await BaselineRevision(tx, project, row.BaseBaseline, root.UnitId, ct) ?? throw ProjectIdentityException.Invalid("Every root must resolve to a baseline revision.");
                if (root.RevisionId.HasValue && root.RevisionId != baselineRevision) throw ProjectIdentityException.Invalid("Root revisions must match the task base baseline.");
                await access.AuthorizeReadAsync(read, root.UnitId, action, baselineRevision, ct);
                var kind = await UnitKind(tx, project, root.UnitId, ct);
                var work = new ImpactWorkItem(root.UnitId, root.UnitId, baselineRevision, kind, null, []);
                if (visited.Add(new(root.UnitId, root.UnitId, null, read.ContextHash))) queue.Enqueue(work);
            }

            var edges = 0; var truncated = false;
            while (queue.Count > 0)
            {
                if (candidates.Count >= 500 || edges >= 1000) { truncated = true; break; }
                var current = queue.Dequeue();
                await using var command = Command(tx);
                command.CommandText = """
                    SELECT r.relation_revision_id,r.predicate,r.from_unit_id,r.to_unit_id,
                      CASE WHEN r.from_unit_id=@unit THEN r.to_unit_id ELSE r.from_unit_id END AS neighbor,
                      CASE WHEN r.from_unit_id=@unit THEN 'forward' ELSE 'reverse' END AS direction,
                      er.unit_id AS edge_unit_id
                    FROM core.relation_index r
                    JOIN core.revision er ON er.project_id=r.project_id AND er.revision_id=r.relation_revision_id
                    JOIN gov.baseline_entry edge ON edge.project_id=r.project_id AND edge.baseline_id=@baseline AND edge.revision_id=r.relation_revision_id
                    JOIN gov.baseline_entry target ON target.project_id=r.project_id AND target.baseline_id=@baseline
                      AND target.unit_id=CASE WHEN r.from_unit_id=@unit THEN r.to_unit_id ELSE r.from_unit_id END
                    WHERE r.project_id=@project AND (r.from_unit_id=@unit OR r.to_unit_id=@unit)
                      AND r.predicate=ANY(@predicates)
                    ORDER BY r.relation_revision_id LIMIT @edgeLimit
                    """;
                Add(command, "project", project); Add(command, "baseline", row.BaseBaseline); Add(command, "unit", current.Unit);
                Add(command, "predicates", ImpactTraversalSemantics.Predicates.ToArray()); Add(command, "edgeLimit", 1001 - edges);
                var found = new List<ImpactEdge>();
                await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) found.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(4), reader.GetString(5), reader.GetGuid(6)));
                var completeNode = found.Count <= 1000 - edges;
                if (!completeNode) truncated = true;

                if (completeNode && ImpactTraversalSemantics.NeedsImplementation(current.Kind) &&
                    !found.Any(edge => edge.Predicate == "implements" && edge.Direction == "reverse") &&
                    blindSpotUnits.Add(current.Unit))
                {
                    blindSpots.Add(new(current.Unit, current.Revision, "implementation", "candidate", "No implementation relation is recorded in this baseline. Code existence and behavior remain uninvestigated; this is not evidence of no impact."));
                }

                foreach (var edge in found)
                {
                    if (edges >= 1000 || candidates.Count >= 500) { truncated = true; break; }
                    edges++;
                    if (!ImpactTraversalSemantics.Allows(current.Kind, edge.Predicate, edge.Direction)) continue;
                    var revision = await BaselineRevision(tx, project, row.BaseBaseline, edge.Neighbor, ct) ?? throw ProjectIdentityException.NotFound();
                    var resource = await access.AuthorizeReadAsync(read, edge.Neighbor, action, revision, ct);
                    await access.AuthorizeReadAsync(read, edge.EdgeUnit, action, edge.Relation, ct);
                    if (ApplicabilityEvaluator.Evaluate(resource.Applicability, read.Context).Status == ApplicabilityStatus.NotApplicable) continue;
                    var kind = await UnitKind(tx, project, edge.Neighbor, ct);
                    var path = current.Path.Append(JsonSerializer.SerializeToElement(new { edge = new RevisionRef(edge.EdgeUnit, edge.Relation), predicate = edge.Predicate, direction = edge.Direction }, Json)).ToList();
                    var reason = $"Potential {request.ChangeType} impact through {edge.Direction} {edge.Predicate}; semantic reachability requires investigation and is not execution evidence.";
                    if (!rootUnits.Contains(edge.Neighbor) && !candidates.ContainsKey(edge.Neighbor)) candidates.Add(edge.Neighbor, new(edge.Neighbor, revision, kind, path, reason));
                    var visit = new ImpactVisit(current.Root, edge.Neighbor, edge.Predicate, read.ContextHash);
                    if (visited.Add(visit)) queue.Enqueue(new(current.Root, edge.Neighbor, revision, kind, edge.Predicate, path));
                }
            }

            var job = Guid.NewGuid(); var ids = new List<Guid>();
            foreach (var candidate in candidates.Values)
            {
                var id = Guid.NewGuid(); ids.Add(id);
                await Execute(tx, "INSERT INTO collab.impact(project_id,impact_id,task_id,target_unit_id,target_revision_id,locator,path,disposition,inspection_state,reason,risk,evidence_refs,close_state,created_by) VALUES(@project,@id,@task,@unit,@revision,'{}',@path::jsonb,'potential','candidate',@reason,'medium','[]','open',@actor)", ct,
                    ("project", project), ("id", id), ("task", task), ("unit", candidate.Unit), ("revision", candidate.Revision), ("path", JsonSerializer.Serialize(candidate.Path, Json)),
                    ("reason", candidate.Reason), ("actor", actor.PrincipalId));
            }
            var summary = Summary(row.Summary);
            summary.ImpactCoverage = new ImpactCoverage
            {
                candidateCount = candidates.Count, edgeCount = edges, truncated = truncated,
                coverage = truncated ? "partial" : "bounded_semantic_graph", maxCandidates = 500, maxEdges = 1000,
                exclusions = ["contains", "dependsOn", "observedAt", "evidencedBy", "supersedes", "conflictsWith"],
                blindSpots = blindSpots
            };
            summary.Unknowns = summary.Unknowns.Where(item => item.Field != "impactCoverage").ToArray();
            if (truncated || blindSpots.Count > 0)
                summary.Unknowns = [.. summary.Unknowns, new Unknown("impactCoverage", "Complete the truncated traversal or investigate the recorded missing implementation relations.", "Semantic candidates do not establish source-code coverage.")];
            summary.NextAction = truncated ? "Continue the bounded impact investigation from the recorded frontier" : blindSpots.Count > 0 ? "Investigate candidate impacts and missing implementation relations" : candidates.Count == 0 ? "No semantic candidates found; record any uninvestigated scope" : "Investigate candidate impacts";
            var resultRef = $"task:{task}:impacts:{row.Version + 1}";
            await Execute(tx, "UPDATE collab.task SET target_summary=@summary::jsonb,version=version+1 WHERE project_id=@project AND task_id=@task; INSERT INTO ops.job(project_id,job_id,kind,requester_id,state,input,result,attempt,max_attempt) VALUES(@project,@job,'impact_analysis',@actor,'succeeded',@input::jsonb,@result::jsonb,0,1)", ct,
                ("project", project), ("task", task), ("summary", JsonSerializer.Serialize(summary, Json)), ("job", job), ("actor", actor.PrincipalId), ("input", JsonSerializer.Serialize(request, Json)),
                ("result", JsonSerializer.Serialize(new { resultRef, impactIds = ids, truncated, blindSpotCount = blindSpots.Count }, Json)));
            row.PendingJob = new(job, "impact_analysis", "succeeded", 1, 0, 1, resultRef, null, summary.Unknowns);
        }, ct);

    public async Task<ImpactPage> ListImpactsAsync(Guid principal, Guid project, Guid task, string? cursor, int limit, CancellationToken ct)
    {
        var after = Cursor(cursor); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct); await AuthorizeTask(tx, principal, project, task, ct);
        var rows = new List<ImpactView>(); await using var command = Command(tx);
        command.CommandText = "SELECT impact_id,task_id,target_unit_id,target_revision_id,locator,path,disposition,inspection_state,investigation_id,reason,risk,assigned_task_id,evidence_refs,close_state,version FROM collab.impact WHERE project_id=@project AND task_id=@task AND (@after IS NULL OR impact_id>@after) ORDER BY impact_id LIMIT @limit";
        Add(command, "project", project); Add(command, "task", task); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) rows.Add(ReadImpact(reader)); await reader.DisposeAsync();
        var next = rows.Count > limit ? Encode(rows[limit - 1].ImpactId) : null; if (rows.Count > limit) rows.RemoveAt(limit);
        var visible = new List<ImpactView>(); foreach (var impact in rows) if (impact.Target is null || await CanRead(principal, project, impact.Target.UnitId, impact.Target.RevisionId, ct)) visible.Add(impact);
        return new(visible, next);
    }

    public async Task<InvestigationInput> SubmitInvestigationAsync(AtlasPrincipal actor, Guid project, Guid task, InvestigationInput request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        if (request.TaskId != task || request.BaselineId == Guid.Empty || string.IsNullOrWhiteSpace(request.Coverage) || request.Methods.Count == 0 || request.Methods.Any(string.IsNullOrWhiteSpace) || !CaptureKinds.Contains(request.CaptureKind)) throw ProjectIdentityException.Invalid("Investigation task, baseline, methods, coverage and captureKind are required.");
        if (request.CommitSha is not null && (request.CommitSha.Length is < 40 or > 64 || request.CommitSha.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))) throw ProjectIdentityException.Invalid("commitSha must be lowercase hexadecimal.");
        if (request.SearchedPaths.Any(string.IsNullOrWhiteSpace) || request.Exclusions.Any(string.IsNullOrWhiteSpace)) throw ProjectIdentityException.Invalid("Investigation paths and exclusions cannot be blank.");
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct);
        var digest = Digest(new { task, request }); if (await Replay<InvestigationInput>(tx, project, actor.PrincipalId, "submitInvestigation", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var row = await ReadTask(tx, project, task, true, ct); if (row.Version != expected) throw ProjectIdentityException.CasMismatch(); if (request.BaselineId != row.BaseBaselineId) throw ProjectIdentityException.Conflict("Investigation must use the task base baseline.");
        var id = request.InvestigationId ?? Guid.NewGuid(); if (request.InvestigationId.HasValue) throw ProjectIdentityException.Invalid("investigationId is assigned by the server.");
        var scope = new { request.Repo, request.CommitSha, request.SearchedPaths, request.Methods, request.RunId };
        await Execute(tx, "INSERT INTO collab.investigation(project_id,investigation_id,task_id,base_baseline_id,scope,evidence_refs,coverage,capture_kind,created_by) VALUES(@project,@id,@task,@baseline,@scope::jsonb,@evidence::jsonb,@coverage::jsonb,@capture,@actor)", ct,
            ("project", project), ("id", id), ("task", task), ("baseline", request.BaselineId), ("scope", JsonSerializer.Serialize(scope, Json)), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)),
            ("coverage", JsonSerializer.Serialize(new { request.Coverage, request.Exclusions }, Json)), ("capture", request.CaptureKind), ("actor", actor.PrincipalId));
        var inspected = request.Evidence.Count > 0 && request.CaptureKind is not ("proposed" or "reconstructed");
        await Execute(tx, "UPDATE collab.impact SET investigation_id=@investigation,inspection_state=CASE WHEN @inspected THEN 'inspected' ELSE inspection_state END,evidence_refs=@evidence::jsonb,version=version+1 WHERE project_id=@project AND task_id=@task AND close_state='open'; UPDATE collab.task SET version=version+1 WHERE project_id=@project AND task_id=@task", ct,
            ("project", project), ("task", task), ("investigation", id), ("inspected", inspected), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)));
        var result = request with { InvestigationId = id }; await Save(tx, project, actor.PrincipalId, "submitInvestigation", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ImpactView> DecideImpactAsync(AtlasPrincipal actor, Guid project, Guid task, Guid impact, DecideImpactRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateDecision(request); var digest = Digest(new { task, impact, request }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (await Replay<ImpactView>(tx, project, actor.PrincipalId, "decideImpact", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var existing = await ReadImpactForUpdate(tx, project, task, impact, ct); if (existing.Version != expected) throw ProjectIdentityException.CasMismatch();
        if (request.InvestigationId.HasValue && !await InvestigationBelongs(tx, project, task, request.InvestigationId.Value, ct)) throw ProjectIdentityException.Invalid("The investigation must belong to this task.");
        if (request.AssignedTaskId.HasValue) _ = await ReadTask(tx, project, request.AssignedTaskId.Value, false, ct);
        if (request.Disposition == "no_change" && (!request.InvestigationId.HasValue || request.Evidence.Count == 0 || request.InspectionState is not ("inspected" or "verified"))) throw ProjectIdentityException.Invalid("no_change requires an actual investigation, evidence, and inspected or verified state.");
        if (request.Disposition == "deferred" && !request.AssignedTaskId.HasValue) throw ProjectIdentityException.Invalid("A deferred impact requires an assigned task.");
        await Execute(tx, "UPDATE collab.impact SET disposition=@disposition,inspection_state=@inspection,investigation_id=@investigation,reason=@reason,risk=@risk,assigned_task_id=@assigned,evidence_refs=@evidence::jsonb,close_state=@close,version=version+1 WHERE project_id=@project AND impact_id=@impact", ct,
            ("project", project), ("impact", impact), ("disposition", request.Disposition), ("inspection", request.InspectionState), ("investigation", Db(request.InvestigationId)), ("reason", request.Reason.Trim()), ("risk", request.Risk), ("assigned", Db(request.AssignedTaskId)), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)), ("close", request.CloseState));
        var result = await ReadImpactForUpdate(tx, project, task, impact, ct); await Save(tx, project, actor.PrincipalId, "decideImpact", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public Task<JobView> PlanChangeAsync(AtlasPrincipal actor, Guid project, Guid task, PlanChangeRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) =>
        MutateTask(actor, project, task, "planChange", expected, key, request, requestId, trace, async (tx, row) =>
        {
            if (!await ContextBelongs(tx, project, task, request.ContextId, ct)) throw ProjectIdentityException.Invalid("contextId must belong to this task.");
            foreach (var investigation in request.InvestigationIds) if (!await InvestigationBelongs(tx, project, task, investigation, ct)) throw ProjectIdentityException.Invalid("Every investigation must belong to this task.");
            var summary = Summary(row.Summary); var unresolved = await Scalar<long>(tx, "SELECT count(*) FROM collab.impact WHERE project_id=@project AND task_id=@task AND close_state='open'", ct, ("project", project), ("task", task));
            var ready = summary.TargetChanges.Count > 0 && summary.Unknowns.Count == 0 && unresolved == 0;
            summary.NextAction = ready ? "Build an executable context and begin implementation" : "Resolve target unknowns and open impacts before implementation";
            var job = Guid.NewGuid(); var resultRef = $"task:{task}:plan:{row.Version + 1}";
            await Execute(tx, "UPDATE collab.task SET state=CASE WHEN @ready AND state IN ('captured','investigating','needs_decision') THEN 'ready' ELSE state END,target_summary=@summary::jsonb,version=version+1 WHERE project_id=@project AND task_id=@task; INSERT INTO ops.job(project_id,job_id,kind,requester_id,context_id,state,input,result,attempt,max_attempt) VALUES(@project,@job,'change_plan',@actor,@context,'succeeded',@input::jsonb,@result::jsonb,0,1)", ct,
                ("project", project), ("task", task), ("ready", ready), ("summary", JsonSerializer.Serialize(summary, Json)), ("job", job), ("actor", actor.PrincipalId), ("context", request.ContextId), ("input", JsonSerializer.Serialize(request, Json)), ("result", JsonSerializer.Serialize(new { resultRef, ready, unresolvedImpacts = unresolved, unknowns = summary.Unknowns }, Json)));
            row.PendingJob = new(job, "change_plan", "succeeded", 1, 0, 1, resultRef, null, summary.Unknowns);
        }, ct);

    public Task<TaskView> TransitionTaskAsync(AtlasPrincipal actor, Guid project, Guid task, TransitionTaskRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) =>
        MutateTask(actor, project, task, "transitionTask", expected, key, request, requestId, trace, async (tx, row) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw ProjectIdentityException.Invalid("A transition reason of 1-4000 characters is required.");
            var next = request.Event switch
            {
                "begin_investigation" when row.State is "captured" or "needs_decision" => "investigating",
                "request_decision" when row.State is "captured" or "investigating" => "needs_decision",
                "begin_implementation" when row.State == "ready" && request.ContextId.HasValue && await ContextBelongs(tx, project, task, request.ContextId.Value, ct) => "implementing",
                "submit_verification" when row.State == "implementing" => "verifying",
                "request_cancel" when row.State is not ("completed" or "cancelled") => "cancelled",
                "resume" when row.State == "blocked" => "investigating",
                _ => throw ProjectIdentityException.Conflict("The requested event is not valid for the current task state or context.")
            };
            await Execute(tx, "UPDATE collab.task SET state=@state,version=version+1 WHERE project_id=@project AND task_id=@task", ct, ("project", project), ("task", task), ("state", next));
        }, ct);

    public Task<ActivityPage> ListActivitiesAsync(Guid principal, Guid project, string kind, Guid? task, Guid? unit, Guid? changeSet, string? cursor, int limit, CancellationToken ct) => ListActivitiesInternal(principal, project, kind, task, unit, changeSet, cursor, limit, ct);
    public Task<ActivityView> CreateActivityAsync(AtlasPrincipal actor, Guid project, string kind, ActivityInput request, string key, string requestId, string? trace, CancellationToken ct) => CreateActivityInternal(actor, project, kind, request, key, requestId, trace, ct);
    public Task<ActivityView> ResolveActivityAsync(AtlasPrincipal actor, Guid project, string kind, Guid activity, ResolveActivityRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct) => ResolveActivityInternal(actor, project, kind, activity, request, expected, key, requestId, trace, ct);

    private async Task<ActivityPage> ListActivitiesInternal(Guid principal, Guid project, string kind, Guid? task, Guid? unit, Guid? changeSet, string? cursor, int limit, CancellationToken ct)
    {
        var table = Table(kind); var idColumn = kind == "feedback" ? "feedback_id" : kind + "_id"; var after = Cursor(cursor);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        var items = new List<ActivityView>(); await using var command = Command(tx);
        command.CommandText = $"SELECT {idColumn},unit_id,revision_id,task_id,change_set_id,anchor,body,state,created_at,version FROM collab.{table} WHERE project_id=@project AND (@task IS NULL OR task_id=@task) AND (@unit IS NULL OR unit_id=@unit) AND (@changeSet IS NULL OR change_set_id=@changeSet) AND (@after IS NULL OR {idColumn}>@after) ORDER BY {idColumn} LIMIT @limit";
        Add(command, "project", project); Add(command, "task", task, NpgsqlDbType.Uuid); Add(command, "unit", unit, NpgsqlDbType.Uuid); Add(command, "changeSet", changeSet, NpgsqlDbType.Uuid); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) items.Add(ReadActivity(reader)); await reader.DisposeAsync();
        var next = items.Count > limit ? Encode(items[limit - 1].Id) : null; if (items.Count > limit) items.RemoveAt(limit);
        var visible = new List<ActivityView>(); foreach (var item in items) if ((!item.Target.UnitId.HasValue || await CanRead(principal, project, item.Target.UnitId.Value, item.Target.RevisionId, ct)) && (!item.Target.TaskId.HasValue || await CanReadTask(tx, principal, project, item.Target.TaskId.Value, ct))) visible.Add(item);
        return new(visible, next);
    }

    private async Task<ActivityView> CreateActivityInternal(AtlasPrincipal actor, Guid project, string kind, ActivityInput request, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateActivity(request); var digest = Digest(new { kind, request }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct); if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        if (await Replay<ActivityView>(tx, project, actor.PrincipalId, "create" + Kind(kind), key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        await ValidateActivityTargets(tx, actor.PrincipalId, project, request, ct); var id = Guid.NewGuid(); var table = Table(kind); var idColumn = kind == "feedback" ? "feedback_id" : kind + "_id";
        await Execute(tx, $"INSERT INTO collab.{table}(project_id,{idColumn},unit_id,revision_id,task_id,change_set_id,anchor,body,state,created_by) VALUES(@project,@id,@unit,@revision,@task,@changeSet,@anchor::jsonb,@body,'open',@actor)", ct,
            ("project", project), ("id", id), ("unit", Db(request.UnitId)), ("revision", Db(request.RevisionId)), ("task", Db(request.TaskId)), ("changeSet", Db(request.ChangeSetId)), ("anchor", request.Anchor.GetRawText()), ("body", request.Body.Trim()), ("actor", actor.PrincipalId));
        var result = await ReadActivityById(tx, project, kind, id, ct); await Save(tx, project, actor.PrincipalId, "create" + Kind(kind), key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    private async Task<ActivityView> ResolveActivityInternal(AtlasPrincipal actor, Guid project, string kind, Guid activity, ResolveActivityRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw ProjectIdentityException.Invalid("A resolution reason of 1-4000 characters is required.");
        var digest = Digest(new { activity, request }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct);
        var operation = "resolve" + Kind(kind); if (await Replay<ActivityView>(tx, project, actor.PrincipalId, operation, key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var current = await ReadActivityById(tx, project, kind, activity, ct); if (current.Version != expected) throw ProjectIdentityException.CasMismatch(); if (current.State is "closed" or "withdrawn") throw ProjectIdentityException.Conflict("The activity is already closed.");
        if (request.TaskId.HasValue) await AuthorizeTask(tx, actor.PrincipalId, project, request.TaskId.Value, ct); if (request.ChangeSetId.HasValue && !await ChangeSetBelongs(tx, project, request.ChangeSetId.Value, ct)) throw ProjectIdentityException.Invalid("The linked change set was not found.");
        var state = request.TaskId.HasValue || request.ChangeSetId.HasValue ? "addressed" : "closed"; var table = Table(kind); var idColumn = kind == "feedback" ? "feedback_id" : kind + "_id";
        await Execute(tx, $"UPDATE collab.{table} SET task_id=COALESCE(@task,task_id),change_set_id=COALESCE(@changeSet,change_set_id),state=@state,version=version+1 WHERE project_id=@project AND {idColumn}=@id", ct,
            ("project", project), ("id", activity), ("task", Db(request.TaskId)), ("changeSet", Db(request.ChangeSetId)), ("state", state));
        var result = await ReadActivityById(tx, project, kind, activity, ct); await Save(tx, project, actor.PrincipalId, operation, key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<AuditEntryPage> ListActivityAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var after = Cursor(cursor); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        var items = new List<AuditEntryView>(); await using var command = Command(tx);
        command.CommandText = "SELECT audit_id,action,resource_ref,result_code,occurred_at,request_id,actor_id FROM ops.audit_event WHERE project_id=@project AND (@after IS NULL OR audit_id>@after) ORDER BY audit_id LIMIT @limit";
        Add(command, "project", project); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), Element(reader.GetString(2)), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6))); await reader.DisposeAsync();
        var next = items.Count > limit ? Encode(items[limit - 1].AuditId) : null; if (items.Count > limit) items.RemoveAt(limit);
        var visible = new List<AuditEntryView>();
        foreach (var item in items)
        {
            var allowed = true;
            if (item.ResourceRef.TryGetProperty("unitId", out var unit) && Guid.TryParse(unit.GetString(), out var unitId)) allowed = await CanRead(principal, project, unitId, null, ct);
            if (allowed && item.ResourceRef.TryGetProperty("taskId", out var task) && Guid.TryParse(task.GetString(), out var taskId)) allowed = await CanReadTask(tx, principal, project, taskId, ct);
            if (allowed) visible.Add(item);
        }
        return new(visible, next);
    }

    private async Task<T> MutateTask<TRequest, T>(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, TRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) where T : class
    {
        ValidateKey(key); if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden(); var digest = Digest(new { task, request });
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (await Replay<T>(tx, project, actor.PrincipalId, operation, key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var row = await ReadTaskRow(tx, project, task, ct); if (row.Version != expected) throw ProjectIdentityException.CasMismatch(); if (row.RootUnitId.HasValue) await AuthorizeUnit(actor.PrincipalId, project, row.RootUnitId.Value, null, ct);
        await action(tx, row); T result = row.PendingJob is not null ? (T)(object)row.PendingJob : (T)(object)await ReadTask(tx, project, task, false, ct);
        await Save(tx, project, actor.PrincipalId, operation, key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    private Task<TaskView> MutateTask(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, AmendTaskRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) => MutateTask<AmendTaskRequest, TaskView>(actor, project, task, operation, expected, key, request, requestId, trace, action, ct);
    private Task<TaskView> MutateTask(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, TransitionTaskRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) => MutateTask<TransitionTaskRequest, TaskView>(actor, project, task, operation, expected, key, request, requestId, trace, action, ct);
    private Task<JobView> MutateTask(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, AnalyzeIntentRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) => MutateTask<AnalyzeIntentRequest, JobView>(actor, project, task, operation, expected, key, request, requestId, trace, action, ct);
    private Task<JobView> MutateTask(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, FindImpactsRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) => MutateTask<FindImpactsRequest, JobView>(actor, project, task, operation, expected, key, request, requestId, trace, action, ct);
    private Task<JobView> MutateTask(AtlasPrincipal actor, Guid project, Guid task, string operation, long expected, string key, PlanChangeRequest request, string requestId, string? trace, Func<NpgsqlTransaction, TaskRow, Task> action, CancellationToken ct) => MutateTask<PlanChangeRequest, JobView>(actor, project, task, operation, expected, key, request, requestId, trace, action, ct);

    private async Task<TaskRow> ReadTaskRow(NpgsqlTransaction tx, Guid project, Guid task, CancellationToken ct)
    {
        var view = await ReadTask(tx, project, task, true, ct);
        await using var command = Command(tx); command.CommandText = "SELECT target_summary FROM collab.task WHERE project_id=@project AND task_id=@task";
        Add(command, "project", project); Add(command, "task", task);
        return new TaskRow(view, (string)(await command.ExecuteScalarAsync(ct))!);
    }

    private async Task<TaskView> ReadTask(NpgsqlTransaction tx, Guid project, Guid task, bool update, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT t.task_id,t.title,t.intent,t.root_unit_id,t.base_baseline_id,t.classification,t.state,t.version,t.target_summary,t.external_ref,c.context_id FROM collab.task t LEFT JOIN LATERAL (SELECT context_id FROM delivery.task_context WHERE project_id=t.project_id AND task_id=t.task_id ORDER BY created_at DESC LIMIT 1) c ON true WHERE t.project_id=@project AND t.task_id=@task" + (update ? " FOR UPDATE OF t" : "");
        Add(command, "project", project); Add(command, "task", task); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var summary = Summary(reader.GetString(8)); return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), summary.TargetChanges, summary.Preserve, summary.Unknowns, summary.NextAction, reader.IsDBNull(10) ? null : reader.GetGuid(10), Element(reader.GetString(9)));
    }

    private async Task<ImpactView> ReadImpactForUpdate(NpgsqlTransaction tx, Guid project, Guid task, Guid impact, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT impact_id,task_id,target_unit_id,target_revision_id,locator,path,disposition,inspection_state,investigation_id,reason,risk,assigned_task_id,evidence_refs,close_state,version FROM collab.impact WHERE project_id=@project AND task_id=@task AND impact_id=@impact FOR UPDATE";
        Add(command, "project", project); Add(command, "task", task); Add(command, "impact", impact); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); return ReadImpact(reader);
    }

    private static ImpactView ReadImpact(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : new(reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)), Element(reader.GetString(4)), Elements(reader.GetString(5)), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11), Elements(reader.GetString(12)), reader.GetString(13), reader.GetInt64(14));

    private async Task<ActivityView> ReadActivityById(NpgsqlTransaction tx, Guid project, string kind, Guid id, CancellationToken ct)
    {
        var table = Table(kind); var idColumn = kind == "feedback" ? "feedback_id" : kind + "_id"; await using var command = Command(tx);
        command.CommandText = $"SELECT {idColumn},unit_id,revision_id,task_id,change_set_id,anchor,body,state,created_at,version FROM collab.{table} WHERE project_id=@project AND {idColumn}=@id FOR UPDATE"; Add(command, "project", project); Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); return ReadActivity(reader);
    }

    private static ActivityView ReadActivity(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(7), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt64(9), new(reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), Element(reader.GetString(5)), reader.GetString(6)));

    private async Task ValidateActivityTargets(NpgsqlTransaction tx, Guid principal, Guid project, ActivityInput request, CancellationToken ct)
    {
        if (!request.UnitId.HasValue && !request.TaskId.HasValue && !request.ChangeSetId.HasValue) throw ProjectIdentityException.Invalid("An activity must link to a unit, task, or change set.");
        if (request.RevisionId.HasValue && !request.UnitId.HasValue) throw ProjectIdentityException.Invalid("revisionId requires unitId.");
        if (request.UnitId.HasValue)
        {
            if (request.RevisionId.HasValue) await AuthorizeExact(tx, principal, project, new(request.UnitId.Value, request.RevisionId.Value), ct); else await AuthorizeUnit(principal, project, request.UnitId.Value, null, ct);
        }
        if (request.TaskId.HasValue) await AuthorizeTask(tx, principal, project, request.TaskId.Value, ct);
        if (request.ChangeSetId.HasValue && !await ChangeSetBelongs(tx, project, request.ChangeSetId.Value, ct)) throw ProjectIdentityException.NotFound();
    }

    private static void ValidateActivity(ActivityInput request)
    {
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 20000) throw ProjectIdentityException.Invalid("Activity body must contain 1-20000 characters.");
        if (request.Anchor.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw ProjectIdentityException.Invalid("anchor must be an object."); if (request.Anchor.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("anchor must be an object.");
    }

    private static void ValidateChanges(IReadOnlyList<TargetChange> changes)
    {
        if (changes.Count > 500 || changes.Any(change => !ChangeTypes.Contains(change.Action) || string.IsNullOrWhiteSpace(change.Description) || string.IsNullOrWhiteSpace(change.Reason))) throw ProjectIdentityException.Invalid("Each target change needs a supported action, description and reason.");
        if (changes.Any(change => change.Action == "revise" && change.Current is null)) throw ProjectIdentityException.Invalid("A revise target must identify its current revision.");
    }

    private static void ValidateDecision(DecideImpactRequest request)
    {
        if (!Dispositions.Contains(request.Disposition) || !InspectionStates.Contains(request.InspectionState) || !CloseStates.Contains(request.CloseState) || !Risks.Contains(request.Risk) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw ProjectIdentityException.Invalid("Impact disposition, inspection, close state, risk and reason are invalid.");
        if (request.CloseState == "resolved" && request.Disposition is "potential" or "unknown") throw ProjectIdentityException.Invalid("Potential or unknown impacts cannot be marked resolved.");
        if (request.CloseState == "deferred" && request.Disposition != "deferred") throw ProjectIdentityException.Invalid("A deferred close state requires deferred disposition.");
    }

    private async Task AuthorizeExact(NpgsqlTransaction tx, Guid principal, Guid project, RevisionRef reference, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM core.revision WHERE project_id=@project AND unit_id=@unit AND revision_id=@revision)"; Add(command, "project", project); Add(command, "unit", reference.UnitId); Add(command, "revision", reference.RevisionId);
        if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("A referenced revision does not belong to the specified unit."); await AuthorizeUnit(principal, project, reference.UnitId, reference.RevisionId, ct);
    }

    private async Task AuthorizeTask(NpgsqlTransaction tx, Guid principal, Guid project, Guid task, CancellationToken ct)
    { var linked = await ReadTask(tx, project, task, false, ct); if (linked.RootUnitId.HasValue) await AuthorizeUnit(principal, project, linked.RootUnitId.Value, null, ct); }
    private async Task<bool> CanReadTask(NpgsqlTransaction tx, Guid principal, Guid project, Guid task, CancellationToken ct)
    { try { await AuthorizeTask(tx, principal, project, task, ct); return true; } catch (ProjectIdentityException exception) when (exception.StatusCode is 403 or 404) { return false; } }
    private async Task AuthorizeUnit(Guid principal, Guid project, Guid unit, Guid? revision, CancellationToken ct)
    { var context = await access.CreateReadContextAsync(principal, project, new(ReadMode.Published, ReadPurpose.Change, null, null, null), ct); await access.AuthorizeReadAsync(context, unit, "read", revision, ct); }
    private async Task<bool> CanRead(Guid principal, Guid project, Guid unit, Guid? revision, CancellationToken ct) { try { await AuthorizeUnit(principal, project, unit, revision, ct); return true; } catch (ProjectIdentityException exception) when (exception.StatusCode is 403 or 404) { return false; } }

    private static async Task<MembershipContext> RequireMember(NpgsqlTransaction tx, Guid project, Guid principal, bool propose, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT p.current_baseline_id,m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active WHERE p.project_id=@project AND p.archived_at IS NULL"; Add(command, "project", project); Add(command, "principal", principal);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); var result = new MembershipContext(reader.GetGuid(0), reader.GetFieldValue<string[]>(1));
        if (propose && !result.Roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden(); return result;
    }

    private static async Task<bool> SealedBaseline(NpgsqlTransaction tx, Guid project, Guid baseline, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM gov.baseline WHERE project_id=@project AND baseline_id=@baseline AND sealed)"; Add(command, "project", project); Add(command, "baseline", baseline); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<Guid?> BaselineRevision(NpgsqlTransaction tx, Guid project, Guid baseline, Guid unit, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT revision_id FROM gov.baseline_entry WHERE project_id=@project AND baseline_id=@baseline AND unit_id=@unit"; Add(command, "project", project); Add(command, "baseline", baseline); Add(command, "unit", unit); return await command.ExecuteScalarAsync(ct) as Guid?; }
    private static async Task<string> UnitKind(NpgsqlTransaction tx, Guid project, Guid unit, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT kind FROM core.unit WHERE project_id=@project AND unit_id=@unit"; Add(command, "project", project); Add(command, "unit", unit); return await command.ExecuteScalarAsync(ct) as string ?? throw ProjectIdentityException.NotFound(); }
    private static async Task<bool> InvestigationBelongs(NpgsqlTransaction tx, Guid project, Guid task, Guid id, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM collab.investigation WHERE project_id=@project AND task_id=@task AND investigation_id=@id)"; Add(command, "project", project); Add(command, "task", task); Add(command, "id", id); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<bool> ContextBelongs(NpgsqlTransaction tx, Guid project, Guid task, Guid id, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM delivery.task_context WHERE project_id=@project AND task_id=@task AND context_id=@id AND phase='executable' AND NOT EXISTS(SELECT 1 FROM delivery.context_invalidation i WHERE i.project_id=@project AND i.context_id=@id))"; Add(command, "project", project); Add(command, "task", task); Add(command, "id", id); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<bool> ChangeSetBelongs(NpgsqlTransaction tx, Guid project, Guid id, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM gov.change_set WHERE project_id=@project AND change_set_id=@id)"; Add(command, "project", project); Add(command, "id", id); return await command.ExecuteScalarAsync(ct) is true; }

    private static async Task Save<T>(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, T result, string request, string? trace, CancellationToken ct)
    {
        ValidateKey(key); await using var command = Command(tx); command.CommandText = "INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@actor,@operation,@key,@digest,@result::jsonb); INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@actor,@operation,@result::jsonb,'OK',@request,@trace)";
        Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key); Add(command, "digest", digest); Add(command, "result", JsonSerializer.Serialize(result, Json), NpgsqlDbType.Jsonb); Add(command, "request", request); Add(command, "trace", trace); await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> Replay<T>(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, CancellationToken ct) where T : class
    {
        ValidateKey(key); await using var command = Command(tx); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@actor AND operation_id=@operation AND idempotency_key=@key"; Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null; if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest))) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request."); return JsonSerializer.Deserialize<T>(reader.GetString(1), Json);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for task collaboration."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static async Task Context(NpgsqlTransaction tx, Guid project, Guid principal, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true)"; Add(command, "project", project.ToString()); Add(command, "principal", principal.ToString()); await command.ExecuteNonQueryAsync(ct); }
    private static async Task Execute(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) { await using var command = Command(tx); command.CommandText = sql; foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<T> Scalar<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) where T : struct { await using var command = Command(tx); command.CommandText = sql; foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value); var value = await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("The scalar query returned no value."); return (T)Convert.ChangeType(value, typeof(T)); }
    private static NpgsqlCommand Command(NpgsqlTransaction tx) { var command = tx.Connection!.CreateCommand(); command.Transaction = tx; return command; }
    private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType? type = null) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; if (type.HasValue) parameter.NpgsqlDbType = type.Value; command.Parameters.Add(parameter); }
    private static object Db(object? value) => value ?? DBNull.Value;
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static IReadOnlyList<JsonElement> Elements(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static string Title(string intent) { var title = intent.Trim(); return title.Length <= 100 ? title : title[..100]; }
    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static string Table(string kind) => kind is "issue" or "feedback" or "comment" ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    private static string Kind(string kind) => char.ToUpperInvariant(kind[0]) + kind[1..];
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static string Encode(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? Cursor(string? value) { if (string.IsNullOrEmpty(value)) return null; try { return new Guid(Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4))); } catch (Exception exception) when (exception is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }

    private static TaskSummary Summary(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        return new()
        {
            TargetChanges = root.TryGetProperty("targetChanges", out var target) ? target.Deserialize<List<TargetChange>>(Json) ?? [] : [],
            Preserve = root.TryGetProperty("preserve", out var preserve) ? preserve.Deserialize<List<RevisionRef>>(Json) ?? [] : [],
            Unknowns = root.TryGetProperty("unknowns", out var unknown) ? unknown.Deserialize<List<Unknown>>(Json) ?? [] : [],
            SourceRefs = root.TryGetProperty("sourceRefs", out var refs) ? refs.Deserialize<List<RevisionRef>>(Json) ?? [] : [],
            NextAction = root.TryGetProperty("nextAction", out var next) ? next.GetString() : null,
            ImpactCoverage = root.TryGetProperty("impactCoverage", out var coverage) ? coverage.Deserialize<ImpactCoverage>(Json) : null
        };
    }

    private sealed class TaskSummary { public IReadOnlyList<TargetChange> TargetChanges { get; set; } = []; public IReadOnlyList<RevisionRef> Preserve { get; set; } = []; public IReadOnlyList<Unknown> Unknowns { get; set; } = []; public IReadOnlyList<RevisionRef> SourceRefs { get; set; } = []; public string? NextAction { get; set; } public ImpactCoverage? ImpactCoverage { get; set; } }
    private sealed class ImpactCoverage
    {
        public int candidateCount { get; set; } public int edgeCount { get; set; } public bool truncated { get; set; }
        public string coverage { get; set; } = "bounded_semantic_graph"; public int maxCandidates { get; set; } public int maxEdges { get; set; }
        public IReadOnlyList<string> exclusions { get; set; } = []; public IReadOnlyList<ImpactBlindSpot> blindSpots { get; set; } = [];
    }
    private sealed record ImpactBlindSpot(Guid UnitId, Guid RevisionId, string Kind, string State, string Reason);
    private sealed record ImpactWorkItem(Guid Root, Guid Unit, Guid Revision, string Kind, string? IncomingPredicate, List<JsonElement> Path);
    private sealed record ImpactVisit(Guid Root, Guid Unit, string? Predicate, string ContextHash);
    private sealed record ImpactEdge(Guid Relation, string Predicate, Guid Neighbor, string Direction, Guid EdgeUnit);
    private sealed record ImpactCandidate(Guid Unit, Guid Revision, string Kind, List<JsonElement> Path, string Reason);
    private sealed record MembershipContext(Guid Baseline, string[] Roles);
    private sealed class TaskRow
    {
        public TaskRow(TaskView view, string summary) { TaskId = view.TaskId; Version = view.Version; State = view.State; Intent = view.Intent; BaseBaseline = view.BaseBaselineId; RootUnitId = view.RootUnitId; Summary = summary; }
        public Guid TaskId { get; } public long Version { get; } public string State { get; } public string Intent { get; } public Guid BaseBaseline { get; } public Guid? RootUnitId { get; } public string Summary { get; } public JobView? PendingJob { get; set; }
    }
}
