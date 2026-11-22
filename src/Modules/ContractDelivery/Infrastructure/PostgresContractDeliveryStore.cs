using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ContractDelivery.Contracts;
using ContractDelivery.Delivery;
using ImportEvidence.Blob;
using KnowledgeCore.Application;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using TaskCollab.Contracts;

namespace ContractDelivery.Infrastructure;

public sealed class PostgresContractDeliveryStore(string connectionString, PostgresAccessApplicabilityStore access, IPrivateBlobStorage blobs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TaskContextView> BuildContextAsync(AtlasPrincipal actor, Guid project, Guid task, BuildContextRequest request, long expected,
        string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        if (request.Phase is not ("candidate" or "executable")) throw ProjectIdentityException.Invalid("Context phase must be candidate or executable.");
        var digest = Digest(new { task, request, expected });
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); var member = await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (await Replay<TaskContextView>(tx, project, actor.PrincipalId, "buildTaskContext", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var row = await TaskRow(tx, project, task, true, ct); if (row.Version != expected) throw ProjectIdentityException.CasMismatch();
        if (row.State is "completed" or "cancelled") throw ProjectIdentityException.Conflict("A closed task cannot receive a new context.");
        var read = await access.CreateReadContextAsync(actor.PrincipalId, project, request.ReadContext, ct);
        if (read.BaselineId != row.BaseBaseline) throw ProjectIdentityException.Conflict("A task context must freeze the task base baseline.");
        if (read.PolicyEpoch != member.Epoch) throw ProjectIdentityException.Conflict("The authorization policy changed while building the context.");
        var summary = JsonDocument.Parse(row.Summary).RootElement;
        var targets = summary.TryGetProperty("targetChanges", out var targetElement) ? Deserialize<List<TargetChange>>(targetElement) : [];
        var preserve = summary.TryGetProperty("preserve", out var preserveElement) ? Deserialize<List<RevisionRef>>(preserveElement) : [];
        var unknowns = summary.TryGetProperty("unknowns", out var unknownElement)
            ? Deserialize<List<Unknown>>(unknownElement).Select(item => new ContextUnknown(item.Field, item.Question, "blocking", "insufficient", item.Question)).ToList() : [];
        var impacts = await Impacts(tx, project, task, ct);
        var evidence = await Evidence(tx, project, task, ct);
        var required = preserve.Concat(targets.SelectMany(item => new[] { item.Current, item.Candidate }).OfType<RevisionRef>())
            .Concat(impacts.Where(item => item.Target?.RevisionId is not null).Select(item => new RevisionRef(item.Target!.UnitId, item.Target.RevisionId!.Value)))
            .Concat(evidence.Select(item => item.Source)).GroupBy(item => (item.UnitId, item.RevisionId)).Select(item => item.First()).OrderBy(item => item.UnitId).ToList();
        foreach (var reference in required) await AuthorizeExact(tx, actor.PrincipalId, project, row.BaseBaseline, reference, ct);
        var acceptance = await AcceptanceCases(tx, project, row.BaseBaseline, impacts, ct);
        foreach (var reference in acceptance) if (!required.Contains(reference)) required.Add(reference);
        if (targets.Count == 0 && !unknowns.Any(item => item.Key == "targetChanges")) unknowns.Add(new("targetChanges", "No observable target has been recorded.", "blocking", "missing", "Clarify and approve the target."));
        var approvals = row.Classification == "implementation_only" ? [] : await ApprovalIds(tx, project, task, ct);
        if (request.Phase == "executable" && row.Classification != "implementation_only" && approvals.Count == 0)
            unknowns.Add(new("businessApproval", "The business target has no recorded approval.", "blocking", "missing", "Obtain the required human approval."));
        var unresolved = impacts.Where(item => item.CloseState == "open" || item.InspectionState == "stale" || item.Disposition == "unknown" || item.Risk is "high" or "critical" && item.CloseState != "resolved").ToArray();
        if (request.Phase == "executable" && unresolved.Length > 0)
            unknowns.Add(new("impacts", $"{unresolved.Length} impacts remain unresolved or unverified.", "blocking", "unverified", "Resolve every required impact with executed evidence."));
        var id = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddHours(8);
        var status = unknowns.Any(item => item.Severity == "blocking") ? "insufficient" : "valid";
        if (request.Phase == "executable" && status != "valid") throw ProjectIdentityException.Invalid("An executable context cannot freeze unresolved target, approval or impact unknowns.");
        var preflight = await ErrorPatternPreflight(tx, project, row.Classification, row.Summary, ct);
        var body = new TaskContextView(id, project, task, row.BaseBaseline, member.Epoch, request.Phase, new string('0', 64), targets, preserve, required,
            evidence, impacts.Select(item => item.ImpactId).Order().ToArray(), approvals, acceptance,
            new[] { "approved knowledge proposal", "verified CI delivery manifest", "target environment deployment reconciliation" }.Concat(preflight).ToArray(), unknowns, status, expires);
        var contractDigest = CanonicalJson.Sha256(JsonSerializer.SerializeToElement(body with { Digest = string.Empty }, Json));
        body = body with { Digest = contractDigest };
        await Execute(tx, "INSERT INTO delivery.task_context(project_id,context_id,task_id,baseline_id,policy_epoch,phase,contract_digest,body,expires_at,created_by) VALUES(@project,@id,@task,@baseline,@epoch,@phase,@digest,@body::jsonb,@expires,@actor)", ct,
            ("project", project), ("id", id), ("task", task), ("baseline", row.BaseBaseline), ("epoch", member.Epoch), ("phase", request.Phase), ("digest", contractDigest),
            ("body", JsonSerializer.Serialize(body, Json)), ("expires", expires), ("actor", actor.PrincipalId));
        foreach (var reference in required)
            await Execute(tx, "INSERT INTO delivery.context_dependency(project_id,dependency_id,context_id,unit_id,revision_id,required,purpose) VALUES(@project,@dependency,@context,@unit,@revision,true,'required')", ct,
                ("project", project), ("dependency", Guid.NewGuid()), ("context", id), ("unit", reference.UnitId), ("revision", reference.RevisionId));
        await Execute(tx, "UPDATE collab.task SET target_summary=jsonb_set(target_summary,'{contextId}',to_jsonb(@context::text),true),version=version+1 WHERE project_id=@project AND task_id=@task", ct,
            ("project", project), ("task", task), ("context", id.ToString()));
        await Save(tx, project, actor.PrincipalId, "buildTaskContext", key, digest, body, requestId, trace, ct); await tx.CommitAsync(ct); return body;
    }

    public async Task<TaskContextView> GetContextAsync(Guid principal, Guid project, Guid context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        return await ReadContext(tx, principal, project, context, ct);
    }

    public async Task<TaskContextView> ExportContextAsync(AtlasPrincipal actor, Guid project, Guid context, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); var digest = Digest(new { context }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, false, ct);
        if (await Replay<TaskContextView>(tx, project, actor.PrincipalId, "exportContext", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var result = await ReadContext(tx, actor.PrincipalId, project, context, ct);
        await Save(tx, project, actor.PrincipalId, "exportContext", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ConformanceView> CheckConformanceAsync(Guid principal, Guid project, ConformanceCheckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BuildDigest) || string.IsNullOrWhiteSpace(request.Environment)) throw ProjectIdentityException.Invalid("Build digest and environment are required.");
        var read = await access.CreateReadContextAsync(principal, project, request.ReadContext, ct);
        var action = read.Mode == ReadMode.Historical ? "read_history" : "read";
        // Authorize the containing units through the read context, then bind the exact
        // revisions below. Normative and observed revisions may legitimately belong to
        // different baselines, which is precisely what conformance comparison must support.
        await access.AuthorizeReadAsync(read, request.NormativeRevision.UnitId, action, null, ct);
        await access.AuthorizeReadAsync(read, request.ObservedRevision.UnitId, action, null, ct);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, principal, ct);
        await AuthorizeExact(tx, principal, project, null, request.NormativeRevision, ct); await AuthorizeExact(tx, principal, project, null, request.ObservedRevision, ct);
        var expected = await RevisionPayload(tx, project, request.NormativeRevision, ct); var actual = await RevisionPayload(tx, project, request.ObservedRevision, ct);
        var equal = CanonicalJson.Sha256(expected) == CanonicalJson.Sha256(actual);
        var evidence = await ExistingEvidence(tx, project, request.NormativeRevision, request.ObservedRevision, request.BuildDigest, request.Environment, ct);
        // A check always compares the requested revisions again. Prior evidence may support the
        // observation, but a previously recorded verdict is never reused as the new verdict.
        return new(null, request.NormativeRevision, request.ObservedRevision, request.BuildDigest, request.Environment, read.ContextHash, EvidenceHash(evidence),
            evidence, expected, actual, "canonical revision comparison", equal ? "conformant" : "divergent");
    }

    public async Task<ConformanceView> RecordConformanceAsync(AtlasPrincipal actor, Guid project, ConformanceView request, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); var digest = Digest(request);
        if (request.Verdict is not ("conformant" or "divergent" or "insufficient" or "unverified" or "stale" or "conflict") || string.IsNullOrWhiteSpace(request.Method) ||
            string.IsNullOrWhiteSpace(request.BuildDigest) || string.IsNullOrWhiteSpace(request.Environment)) throw ProjectIdentityException.Invalid("Conformance verdict, method, build and environment are required.");
        if (!IsSha256(request.ContextHash) || request.EvidenceHash != EvidenceHash(request.Evidence)) throw ProjectIdentityException.Invalid("Conformance context and evidence hashes must bind the exact supplied observation.");
        if ((request.Verdict is "conformant" or "divergent") && (request.Evidence.Count == 0 || request.Evidence.Any(item => item.CaptureKind is "proposed" or "reconstructed")))
            throw ProjectIdentityException.Invalid("A conformance decision requires actual captured or human-attested evidence.");
        var read = await access.CreateReadContextAsync(actor.PrincipalId, project, new(ReadMode.Published, ReadPurpose.Implement, null, null, null), ct);
        foreach (var unit in new[] { request.NormativeRevision, request.ObservedRevision }.Concat(request.Evidence.Select(item => item.Source)).Select(item => item.UnitId).Distinct())
            await access.AuthorizeReadAsync(read, unit, "read", null, ct);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, actor.PrincipalId, ct); var verifier = await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (actor.Kind != PrincipalKind.Human || !verifier.Roles.Contains("Verifier")) throw ProjectIdentityException.Forbidden();
        if (await Replay<ConformanceView>(tx, project, actor.PrincipalId, "recordConformance", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        await AuthorizeExact(tx, actor.PrincipalId, project, null, request.NormativeRevision, ct); await AuthorizeExact(tx, actor.PrincipalId, project, null, request.ObservedRevision, ct);
        foreach (var item in request.Evidence) await AuthorizeExact(tx, actor.PrincipalId, project, null, item.Source, ct);
        var expected = await RevisionPayload(tx, project, request.NormativeRevision, ct); var actual = await RevisionPayload(tx, project, request.ObservedRevision, ct);
        if (CanonicalJson.Sha256(expected) != CanonicalJson.Sha256(request.Expected) || CanonicalJson.Sha256(actual) != CanonicalJson.Sha256(request.Actual))
            throw ProjectIdentityException.Invalid("Expected and actual payloads must be freshly read from the identified revisions.");
        var equal = CanonicalJson.Sha256(expected) == CanonicalJson.Sha256(actual);
        if (request.Verdict == "conformant" && !equal || request.Verdict == "divergent" && equal)
            throw ProjectIdentityException.Invalid("The conformance verdict must follow the current revision comparison.");
        var id = Guid.NewGuid(); var result = request with { CheckId = id };
        await Execute(tx, "INSERT INTO delivery.conformance(project_id,check_id,normative_revision_id,observed_revision_id,build_digest,environment,context_sha256,evidence_sha256,evidence_refs,expected,actual,verdict,method,created_by) VALUES(@project,@id,@normative,@observed,@build,@environment,@context,@evidenceHash,@evidence::jsonb,@expected::jsonb,@actual::jsonb,@verdict,@method,@actor)", ct,
            ("project", project), ("id", id), ("normative", request.NormativeRevision.RevisionId), ("observed", request.ObservedRevision.RevisionId), ("build", request.BuildDigest), ("environment", request.Environment),
            ("context", request.ContextHash), ("evidenceHash", request.EvidenceHash), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)), ("expected", request.Expected.GetRawText()), ("actual", request.Actual.GetRawText()),
            ("verdict", request.Verdict), ("method", request.Method), ("actor", actor.PrincipalId));
        if (request.Verdict is "divergent" or "stale" or "conflict")
            await Execute(tx, """
                INSERT INTO delivery.context_invalidation(project_id,invalidation_id,context_id,cause)
                SELECT @project,gen_random_uuid(),cd.context_id,@cause
                FROM delivery.context_dependency cd
                WHERE cd.project_id=@project AND cd.required AND cd.revision_id IN (@normative,@observed)
                  AND NOT EXISTS(SELECT 1 FROM delivery.context_invalidation i WHERE i.project_id=cd.project_id AND i.context_id=cd.context_id AND i.cause=@cause)
                """, ct, ("project", project), ("normative", request.NormativeRevision.RevisionId), ("observed", request.ObservedRevision.RevisionId),
                ("cause", $"conformance_{request.Verdict}_{id}"));
        await Save(tx, project, actor.PrincipalId, "recordConformance", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<IdView> RecordAcceptanceAsync(AtlasPrincipal actor, Guid project, Guid task, AcceptanceInput request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); if (request.Result is not ("pass" or "fail" or "not_run" or "unknown") || string.IsNullOrWhiteSpace(request.TestId)) throw ProjectIdentityException.Invalid("Acceptance testId or result is invalid.");
        if (request.Result == "pass" && (!request.Observed.HasValue || string.IsNullOrWhiteSpace(request.BuildDigest) || string.IsNullOrWhiteSpace(request.Environment) ||
            request.Evidence.Count == 0 || request.Evidence.Any(item => item.CaptureKind is "proposed" or "reconstructed")))
            throw ProjectIdentityException.Invalid("A passing acceptance result requires observed output, build, target environment and actual captured evidence.");
        if (request.Result == "pass" && CanonicalJson.Sha256(request.Expected) != CanonicalJson.Sha256(request.Observed!.Value))
            throw ProjectIdentityException.Invalid("A passing acceptance result cannot contradict its observed output.");
        var digest = Digest(new { task, request, expected }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); var member = await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (!member.Roles.Any(role => role is "Verifier" or "CIService")) throw ProjectIdentityException.Forbidden();
        if (await Replay<IdView>(tx, project, actor.PrincipalId, "recordAcceptance", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var row = await TaskRow(tx, project, task, true, ct); if (row.Version != expected) throw ProjectIdentityException.CasMismatch();
        var context = await ReadContext(tx, actor.PrincipalId, project, request.ContextId, ct);
        if (context.TaskId != task || context.Phase != "executable" || context.Status != "valid") throw ProjectIdentityException.Conflict("Acceptance requires the current valid executable task context.");
        foreach (var item in request.Evidence) await AuthorizeExact(tx, actor.PrincipalId, project, context.BaselineId, item.Source, ct);
        if (request.Result == "pass" && !request.Evidence.Any(item => context.RequiredRefs.Contains(item.Source)))
            throw ProjectIdentityException.Invalid("A passing case must cite a requirement, rule or knowledge revision frozen in the task context.");
        var id = Guid.NewGuid(); var result = new IdView(id);
        await Execute(tx, "INSERT INTO delivery.acceptance(project_id,acceptance_id,task_id,context_id,test_id,expected,observed,result,build_digest,environment,evidence_refs,created_by) VALUES(@project,@id,@task,@context,@test,@expected::jsonb,@observed::jsonb,@result,@build,@environment,@evidence::jsonb,@actor)", ct,
            ("project", project), ("id", id), ("task", task), ("context", request.ContextId), ("test", request.TestId), ("expected", request.Expected.GetRawText()), ("observed", Db(request.Observed?.GetRawText())),
            ("result", request.Result), ("build", Db(request.BuildDigest)), ("environment", Db(request.Environment)), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)), ("actor", actor.PrincipalId));
        await Save(tx, project, actor.PrincipalId, "recordAcceptance", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ClosureView> CheckClosureAsync(Guid principal, Guid project, Guid task, Guid context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        return await Closure(tx, principal, project, task, context, ct);
    }

    public async Task<ClosureView> RequestClosureAsync(AtlasPrincipal actor, Guid project, Guid task, CloseTaskRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); if (actor.Kind != PrincipalKind.Human || string.IsNullOrWhiteSpace(request.Reason)) throw ProjectIdentityException.Invalid("An independent human acceptance reason is required.");
        var digest = Digest(new { task, request, expected }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); var member = await RequireMember(tx, project, actor.PrincipalId, false, ct);
        if (!member.Roles.Any(role => role is "BusinessOwner" or "Verifier")) throw ProjectIdentityException.Forbidden();
        if (await Replay<ClosureView>(tx, project, actor.PrincipalId, "requestTaskClosure", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var row = await TaskRow(tx, project, task, true, ct); if (row.Version != expected) throw ProjectIdentityException.CasMismatch(); if (row.CreatedBy == actor.PrincipalId) throw ProjectIdentityException.Forbidden();
        if (row.State == "completed") throw ProjectIdentityException.Conflict("The task is already closed.");
        var closure = await Closure(tx, actor.PrincipalId, project, task, request.ContextId, ct);
        if (closure.State != "ready_for_acceptance") throw ProjectIdentityException.Conflict("All critical closure checks must pass before independent human acceptance.");
        await Execute(tx, "UPDATE collab.task SET state='completed',version=version+1 WHERE project_id=@project AND task_id=@task", ct, ("project", project), ("task", task));
        var result = closure with { State = "complete" };
        await Save(tx, project, actor.PrincipalId, "requestTaskClosure", key, digest, result, requestId, trace, ct);
        await Execute(tx, "INSERT INTO ops.audit_event(project_id,audit_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@audit,@actor,'humanTaskAcceptance',@resource::jsonb,'OK',@request,@trace)", ct,
            ("project", project), ("audit", Guid.NewGuid()), ("actor", actor.PrincipalId), ("resource", JsonSerializer.Serialize(new { task, request.ContextId, request.Reason, closureDigest = Digest(closure) }, Json)),
            ("request", requestId), ("trace", trace));
        await tx.CommitAsync(ct); return result;
    }

    public async Task<IdView> IngestProposalAsync(AtlasPrincipal actor, Guid project, JsonElement proposal, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        KnowledgeDeliveryProtocol.ValidateProposal(proposal);
        var version = RequiredString(proposal, "schemaVersion"); if (version is not ("1.2.0" or "1.3.0")) throw ProjectIdentityException.Invalid("Only proposal schema 1.2.0 or 1.3.0 is supported.");
        var external = RequiredString(proposal, "proposalId"); var task = RequiredGuid(proposal, "taskId"); if (RequiredGuid(proposal, "projectId") != project) throw ProjectIdentityException.Invalid("Proposal project does not match the route.");
        var raw = proposal.GetRawText(); var sha = Sha256(raw); var digest = Digest(new { proposal }); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct); _ = await TaskRow(tx, project, task, false, ct);
        if (await Replay<IdView>(tx, project, actor.PrincipalId, "ingestKnowledgeProposal", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var blob = await StoreBlob(project, raw, tx, actor.PrincipalId, ct); var id = Guid.NewGuid();
        await Execute(tx, "INSERT INTO delivery.proposal_intake(project_id,proposal_sha256,proposal_external_id,schema_version,task_id,repo_key,blob_id,created_by) VALUES(@project,@sha,@external,@version,@task,@repo,@blob,@actor) ON CONFLICT(project_id,proposal_sha256) DO NOTHING", ct,
            ("project", project), ("sha", sha), ("external", external), ("version", version), ("task", task), ("repo", proposal.GetProperty("repo").GetProperty("url").GetString()), ("blob", blob), ("actor", actor.PrincipalId));
        await Save(tx, project, actor.PrincipalId, "ingestKnowledgeProposal", key, digest, new IdView(blob), requestId, trace, ct); await tx.CommitAsync(ct); return new IdView(blob);
    }

    public async Task<DeliveryStatus> IngestManifestAsync(AtlasPrincipal actor, Guid project, JsonElement manifest, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); KnowledgeDeliveryProtocol.ValidateManifest(manifest);
        var version = RequiredString(manifest, "schemaVersion");
        var task = RequiredGuid(manifest, "taskId"); if (RequiredGuid(manifest, "projectId") != project) throw ProjectIdentityException.Invalid("Manifest project does not match the route.");
        var proposalSha = RequiredString(manifest, "proposalSha256"); var external = RequiredString(manifest, "deliveryId"); var repo = RequiredString(manifest.GetProperty("repo"), "url");
        var commit = RequiredString(manifest, "finalCommitSha"); var build = RequiredString(manifest, "buildDigest"); var status = RequiredString(manifest, "status");
        var raw = manifest.GetRawText(); var manifestSha = Sha256(raw); var digest = Digest(manifest); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContext(tx, project, actor.PrincipalId, ct); var member = await RequireMember(tx, project, actor.PrincipalId, true, ct); if (!member.Roles.Contains("CIService") || actor.Kind == PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        if (await Replay<DeliveryStatus>(tx, project, actor.PrincipalId, "ingestDeliveryManifest", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        _ = await TaskRow(tx, project, task, false, ct);
        string proposalStorageKey;
        await using (var check = Command(tx, "SELECT b.storage_key FROM delivery.proposal_intake p JOIN intake.blob b ON b.project_id=p.project_id AND b.blob_id=p.blob_id WHERE p.project_id=@project AND p.proposal_sha256=@sha AND p.task_id=@task AND p.repo_key=@repo", ("project", project), ("sha", proposalSha), ("task", task), ("repo", repo)))
            proposalStorageKey = await check.ExecuteScalarAsync(ct) as string ?? throw ProjectIdentityException.Invalid("The exact proposal bytes must be ingested for this task and repository first.");
        await using (var original = await blobs.OpenReadAsync(project, proposalStorageKey, ct)) using (var document = await JsonDocument.ParseAsync(original, cancellationToken: ct))
            KnowledgeDeliveryProtocol.BindManifestToProposal(manifest, document.RootElement);
        var blob = await StoreBlob(project, raw, tx, actor.PrincipalId, ct); var id = Guid.NewGuid();
        await Execute(tx, "INSERT INTO delivery.delivery_record(project_id,delivery_id,external_delivery_id,task_id,repo_key,final_commit_sha,manifest_sha256,build_digest,proposal_sha256,blob_id,created_by) VALUES(@project,@id,@external,@task,@repo,@commit,@manifest,@build,@proposal,@blob,@actor)", ct,
            ("project", project), ("id", id), ("external", external), ("task", task), ("repo", repo), ("commit", commit), ("manifest", manifestSha), ("build", build), ("proposal", proposalSha), ("blob", blob), ("actor", actor.PrincipalId));
        if (status != "verified") await InvalidateTaskContexts(tx, project, task, $"manifest_{status}", ct);
        var result = await ReconcileOne(tx, project, id, null, ct); await Save(tx, project, actor.PrincipalId, "ingestDeliveryManifest", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<DeliveryStatus> RecordDeploymentAsync(AtlasPrincipal actor, Guid project, DeploymentInput request, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(request.Environment) || string.IsNullOrWhiteSpace(request.RepoKey) || string.IsNullOrWhiteSpace(request.MigrationVersion) ||
            string.IsNullOrWhiteSpace(request.CommitSha) || string.IsNullOrWhiteSpace(request.BuildDigest) || !IsSha256(request.ConfigDigest) || request.Evidence.Count == 0)
            throw ProjectIdentityException.Invalid("Deployment environment, repository, commit, build, migration, configuration digest and evidence are required.");
        if (request.Evidence.Any(item => item.CaptureKind is "proposed" or "reconstructed")) throw ProjectIdentityException.Invalid("Deployment requires actual captured or human-attested evidence.");
        var digest = Digest(request); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, actor.PrincipalId, ct);
        var member = await RequireMember(tx, project, actor.PrincipalId, true, ct); if (!member.Roles.Contains("CIService") || actor.Kind == PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        if (await Replay<DeliveryStatus>(tx, project, actor.PrincipalId, "recordDeployment", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        foreach (var item in request.Evidence) await AuthorizeExact(tx, actor.PrincipalId, project, null, item.Source, ct);
        var id = request.DeploymentId ?? Guid.NewGuid();
        await Execute(tx, "INSERT INTO delivery.deployment_record(project_id,deployment_id,environment,repo_key,final_commit_sha,build_digest,migration_version,config_digest,previous_deployment_id,deployed_at,evidence_refs,created_by) VALUES(@project,@id,@environment,@repo,@commit,@build,@migration,@config,@previous,@deployed,@evidence::jsonb,@actor)", ct,
            ("project", project), ("id", id), ("environment", request.Environment), ("repo", request.RepoKey), ("commit", request.CommitSha), ("build", request.BuildDigest), ("migration", request.MigrationVersion),
            ("config", request.ConfigDigest), ("previous", Db(request.PreviousDeploymentId)), ("deployed", request.DeployedAt), ("evidence", JsonSerializer.Serialize(request.Evidence, Json)), ("actor", actor.PrincipalId));
        var delivery = await Scalar<Guid?>(tx, "SELECT delivery_id FROM delivery.delivery_record WHERE project_id=@project AND repo_key=@repo AND final_commit_sha=@commit AND build_digest=@build ORDER BY created_at DESC LIMIT 1", ct,
            ("project", project), ("repo", request.RepoKey), ("commit", request.CommitSha), ("build", request.BuildDigest));
        if (!delivery.HasValue) throw ProjectIdentityException.Invalid("Deployment must match a recorded final commit and build digest.");
        var result = await ReconcileOne(tx, project, delivery.Value, request.Environment, ct);
        await InvalidateSupersededDeployments(tx, project, request, id, ct);
        await Save(tx, project, actor.PrincipalId, "recordDeployment", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    private static async Task InvalidateSupersededDeployments(NpgsqlTransaction tx, Guid project, DeploymentInput request, Guid deploymentId, CancellationToken ct)
    {
        var superseded = new List<Guid>();
        await using (var command = Command(tx, """
            SELECT DISTINCT d.task_id
            FROM delivery.delivery_record d
            JOIN delivery.deployment_record old ON old.project_id=d.project_id AND old.repo_key=d.repo_key
              AND old.final_commit_sha=d.final_commit_sha AND old.build_digest=d.build_digest
            WHERE d.project_id=@project AND d.repo_key=@repo AND old.environment=@environment
              AND (old.final_commit_sha<>@commit OR old.build_digest<>@build)
              AND (@deployed>old.deployed_at OR @deployed=old.deployed_at AND @deployment>old.deployment_id)
            """, ("project", project), ("repo", request.RepoKey), ("environment", request.Environment), ("commit", request.CommitSha), ("build", request.BuildDigest),
            ("deployed", request.DeployedAt), ("deployment", deploymentId)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) superseded.Add(reader.GetGuid(0));
        }
        foreach (var task in superseded) await InvalidateTaskContexts(tx, project, task, $"deployment_superseded_{deploymentId}", ct);
    }

    public async Task<DeliveryPage> ListDeliveriesAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var after = Cursor(cursor); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, principal, ct); await RequireMember(tx, project, principal, false, ct);
        var ids = new List<Guid>(); await using (var command = Command(tx, "SELECT delivery_id FROM delivery.delivery_record WHERE project_id=@project AND (@after IS NULL OR delivery_id>@after) ORDER BY delivery_id LIMIT @limit", ("project", project), ("after", after), ("limit", limit + 1)))
        { await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0)); }
        var next = ids.Count > limit ? Convert.ToBase64String(ids[limit - 1].ToByteArray()) : null; if (ids.Count > limit) ids.RemoveAt(limit);
        var items = new List<DeliveryStatus>(); foreach (var id in ids) items.Add(await ReconcileOne(tx, project, id, null, ct)); return new(items, next);
    }

    public async Task<DeliveryStatus> ReconcileAsync(AtlasPrincipal actor, Guid project, ReconcileRequest request, string key, string requestId, string? trace, CancellationToken ct)
    {
        ValidateKey(key); if (request.TaskId == Guid.Empty) throw ProjectIdentityException.Invalid("A taskId is required for reconciliation.");
        var digest = Digest(request); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContext(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, true, ct);
        if (await Replay<DeliveryStatus>(tx, project, actor.PrincipalId, "reconcileDelivery", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        _ = await TaskRow(tx, project, request.TaskId, false, ct);
        var id = await Scalar<Guid?>(tx, "SELECT delivery_id FROM delivery.delivery_record WHERE project_id=@project AND task_id=@task ORDER BY created_at DESC,delivery_id DESC LIMIT 1", ct,
            ("project", project), ("task", request.TaskId)) ?? throw ProjectIdentityException.NotFound();
        var result = await ReconcileOne(tx, project, id, request.Environment, ct); await Save(tx, project, actor.PrincipalId, "reconcileDelivery", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    private async Task<ClosureView> Closure(NpgsqlTransaction tx, Guid principal, Guid project, Guid task, Guid contextId, CancellationToken ct)
    {
        var row = await TaskRow(tx, project, task, false, ct); var context = await ReadContext(tx, principal, project, contextId, ct);
        if (context.TaskId != task) throw ProjectIdentityException.NotFound(); var checks = new List<ClosureCheck>();
        var valid = context.Phase == "executable" && context.Status == "valid";
        checks.Add(new("authorization", valid ? "pass" : "fail", true, valid ? "Executable context is current and authorized." : "Context is stale, insufficient or not executable.", [], "Build a reauthorized current context."));
        var approvalRequired = row.Classification != "implementation_only";
        checks.Add(new("approvalCheck", !approvalRequired ? "not_applicable" : context.ApprovalIds.Count > 0 ? "pass" : "fail", true,
            approvalRequired ? "The approved business target and its preserving constraints must remain bound to this context." : "Implementation-only compatibility work does not create a business approval.", []));
        checks.Add(new("targetCheck", valid && context.Target.Count > 0 ? "pass" : "fail", true, "Target changes must be explicitly frozen.", context.Evidence));
        var impacts = await Impacts(tx, project, task, ct);
        var unresolved = impacts.Where(item => context.ImpactIds.Contains(item.ImpactId) && (item.CloseState != "resolved" || item.InspectionState is not ("inspected" or "verified") ||
            item.Disposition == "no_change" && item.Evidence.Count == 0)).ToArray();
        checks.Add(new("impactCheck", unresolved.Length == 0 ? "pass" : "fail", true,
            unresolved.Length == 0 ? "All frozen impacts are resolved, inspected and evidenced." : $"{unresolved.Length} impacts remain unresolved or lack no-change evidence.",
            [], "Resolve and verify every frozen impact."));

        var delivery = await Scalar<Guid?>(tx, "SELECT delivery_id FROM delivery.delivery_record WHERE project_id=@project AND task_id=@task ORDER BY created_at DESC LIMIT 1", ct, ("project", project), ("task", task));
        DeliveryStatus? status = delivery.HasValue ? await ReconcileOne(tx, project, delivery.Value, null, ct) : null;
        checks.Add(new("codeEvidenceCheck", status?.Status is "aligned" or "pending_review" ? "pass" : "fail", true, status is null ? "No verified CI delivery manifest exists." : $"Delivery reconciliation is {status.Status}.", []));
        checks.Add(new("knowledgeCheck", status?.ChangeSetId.HasValue == true && status.PublishedBaselineId.HasValue ? "pass" : "unknown", true, "Knowledge must be approved and published, or explicitly handled as authorized noncritical debt.", []));
        var openCriticalDebt = await Scalar<long>(tx, "SELECT count(*) FROM delivery.knowledge_debt WHERE project_id=@project AND task_id=@task AND state NOT IN ('closed','cancelled') AND risk IN ('high','critical')", ct, ("project", project), ("task", task));
        if (openCriticalDebt > 0) checks.Add(new("knowledgeDebt", "fail", true, $"{openCriticalDebt} critical knowledge debts remain open.", [], "Close required knowledge debt before completion."));

        var acceptance = await LatestAcceptance(tx, project, task, contextId, ct);
        var passing = acceptance.Where(item => item.Result == "pass").ToArray(); var failing = acceptance.Where(item => item.Result != "pass").ToArray();
        var uncoveredCases = context.AcceptanceCases.Where(reference => !passing.Any(item => Covers(item, reference))).ToArray();
        var testPass = passing.Length > 0 && failing.Length == 0 && uncoveredCases.Length == 0;
        checks.Add(new("testCheck", testPass ? "pass" : failing.Length > 0 ? "fail" : "unknown", true,
            testPass ? "Every current acceptance case has passing actual evidence." : failing.Length > 0 ? "A current acceptance result is failed, not run or unknown." : uncoveredCases.Length > 0 ? $"{uncoveredCases.Length} frozen acceptance cases lack passing evidence." : "At least one passing actual acceptance result is required.",
            passing.SelectMany(item => item.Evidence).ToArray(), "Run and record every positive, boundary, regression and permission case."));
        var uncoveredPreserve = context.Preserve.Where(reference => !passing.Any(item => Covers(item, reference))).ToArray();
        var preserveStatus = context.Preserve.Count == 0 ? "not_applicable" : uncoveredPreserve.Length == 0 && failing.Length == 0 ? "pass" : "unknown";
        checks.Add(new("preserveCheck", preserveStatus, true, context.Preserve.Count == 0 ? "No separate preserve revision was frozen." : uncoveredPreserve.Length == 0 ? "Every preserved constraint is covered by passing evidence." : $"{uncoveredPreserve.Length} preserved constraints lack explicit passing evidence.",
            passing.SelectMany(item => item.Evidence).ToArray(), "Add acceptance evidence citing each preserved revision."));

        var deployedAcceptance = passing.FirstOrDefault(item => status is not null && item.BuildDigest == status.BuildDigest && !string.IsNullOrWhiteSpace(item.Environment));
        var targetEnvironment = deployedAcceptance?.Environment;
        var deployed = targetEnvironment is not null && status is not null && await Scalar<long>(tx, """
            WITH latest AS (
                SELECT d.*,row_number() OVER(ORDER BY d.deployed_at DESC,d.deployment_id DESC) AS rank
                FROM delivery.deployment_record d
                WHERE d.project_id=@project AND d.environment=@environment
                  AND d.repo_key=(SELECT repo_key FROM delivery.delivery_record WHERE project_id=@project AND delivery_id=@delivery))
            SELECT count(*) FROM latest WHERE rank=1 AND final_commit_sha=@commit AND build_digest=@build
            """, ct, ("project", project), ("delivery", delivery), ("environment", targetEnvironment), ("commit", status.CommitSha), ("build", status.BuildDigest)) > 0;
        checks.Add(new("deploymentCheck", deployed && status?.Status == "aligned" ? "pass" : "fail", true,
            deployed ? "The latest target-environment deployment matches the delivered commit and build." : "The latest target-environment deployment does not match the accepted delivery; rollback requires fresh reconciliation and acceptance.", []));

        var conformance = 0L; var conformant = 0L;
        if (status is not null && targetEnvironment is not null && context.RequiredRefs.Count > 0)
        {
            var revisions = context.RequiredRefs.Select(item => item.RevisionId).ToArray();
            conformance = await Scalar<long>(tx, """
                WITH latest AS (SELECT c.*,row_number() OVER(PARTITION BY normative_revision_id,observed_revision_id,build_digest,environment ORDER BY created_at DESC,check_id DESC) rn
                  FROM delivery.conformance c WHERE c.project_id=@project AND c.build_digest=@build AND c.environment=@environment AND c.normative_revision_id=ANY(@revisions))
                SELECT count(*) FROM latest WHERE rn=1 AND verdict IN ('divergent','stale','conflict')
                """, ct, ("project", project), ("build", status.BuildDigest), ("environment", targetEnvironment), ("revisions", revisions));
            conformant = await Scalar<long>(tx, """
                WITH latest AS (SELECT c.*,row_number() OVER(PARTITION BY normative_revision_id,observed_revision_id,build_digest,environment ORDER BY created_at DESC,check_id DESC) rn
                  FROM delivery.conformance c WHERE c.project_id=@project AND c.build_digest=@build AND c.environment=@environment AND c.normative_revision_id=ANY(@revisions))
                SELECT count(*) FROM latest WHERE rn=1 AND verdict='conformant'
                """, ct, ("project", project), ("build", status.BuildDigest), ("environment", targetEnvironment), ("revisions", revisions));
        }
        checks.Add(new("conformanceCheck", conformance == 0 && conformant > 0 ? "pass" : conformance > 0 ? "fail" : "unknown", true,
            conformance > 0 ? "The latest reverse observation is divergent, stale or conflicting." : conformant > 0 ? "Fresh conformance evidence supports this build and environment." : "No fresh conformant observation binds the delivered build and target environment.", [],
            "Recheck the exact normative and observed revisions and record actual evidence."));
        var openIssues = await Scalar<long>(tx, "SELECT count(*) FROM collab.issue WHERE project_id=@project AND task_id=@task AND state IN ('open','addressed')", ct, ("project", project), ("task", task));
        checks.Add(new("unresolvedIssues", openIssues == 0 ? "pass" : "fail", true, openIssues == 0 ? "No unresolved task issues." : $"{openIssues} task issues remain open.", []));
        var blocked = checks.Any(item => item.Critical && item.Status is not ("pass" or "not_applicable")); var state = row.State == "completed" && !blocked ? "complete" : blocked ? "blocked" : "ready_for_acceptance";
        var unknowns = checks.Where(item => item.Status == "unknown").Select(item => new ContextUnknown(item.Key, item.Reason, item.Critical ? "blocking" : "warning", "unverified", item.NextAction)).ToArray();
        return new(task, contextId, state, checks, unknowns);
    }

    private static async Task<List<string>> ErrorPatternPreflight(NpgsqlTransaction tx, Guid project, string classification, string taskSummary, CancellationToken ct)
    {
        var result = new List<string>();
        await using var command = Command(tx, """
            WITH latest AS (
                SELECT pattern_key,pattern_revision,applicability::text,detector,mitigation,regression_test_id,state,
                       row_number() OVER(PARTITION BY pattern_key ORDER BY pattern_revision DESC) AS rank
                FROM ops.error_pattern WHERE project_id=@project)
            SELECT pattern_key,pattern_revision,applicability,detector,mitigation,regression_test_id
            FROM latest WHERE rank=1 AND state='reviewed' ORDER BY pattern_key
            """, ("project", project));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var applicability = JsonDocument.Parse(reader.GetString(2)).RootElement;
            if (!AppliesToTask(applicability, classification, taskSummary)) continue;
            result.Add($"error preflight {reader.GetString(0)}@{reader.GetInt32(1)}: detect={reader.GetString(3)}; prevent={reader.GetString(4)}; regression={reader.GetString(5)}");
        }
        return result;
    }

    private static bool AppliesToTask(JsonElement applicability, string classification, string taskSummary)
    {
        if (applicability.ValueKind != JsonValueKind.Object) return false;
        if (applicability.TryGetProperty("kind", out var kind) && !MatchesApplicability(kind, classification)) return false;
        if (applicability.TryGetProperty("path", out var path))
        {
            var paths = path.ValueKind == JsonValueKind.Array ? path.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!) :
                path.ValueKind == JsonValueKind.String ? [path.GetString()!] : [];
            if (!paths.Any(item => !string.IsNullOrWhiteSpace(item) && taskSummary.Contains(item, StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private static bool MatchesApplicability(JsonElement value, string classification)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() is "*" || string.Equals(value.GetString(), classification, StringComparison.Ordinal);
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => MatchesApplicability(item, classification));
    }

    private static bool Covers(AcceptanceRecord acceptance, RevisionRef reference) =>
        acceptance.TestId == reference.RevisionId.ToString() || acceptance.TestId == reference.UnitId.ToString() || acceptance.Evidence.Any(item => item.Source == reference);

    private static async Task<List<AcceptanceRecord>> LatestAcceptance(NpgsqlTransaction tx, Guid project, Guid task, Guid context, CancellationToken ct)
    {
        var result = new List<AcceptanceRecord>();
        await using var command = Command(tx, """
            WITH latest AS (SELECT a.*,row_number() OVER(PARTITION BY test_id ORDER BY created_at DESC,acceptance_id DESC) rn
              FROM delivery.acceptance a WHERE project_id=@project AND task_id=@task AND context_id=@context)
            SELECT test_id,result,build_digest,environment,evidence_refs::text FROM latest WHERE rn=1 ORDER BY created_at DESC,acceptance_id DESC
            """, ("project", project), ("task", task), ("context", context));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            Deserialize<List<ContextEvidence>>(JsonDocument.Parse(reader.GetString(4)).RootElement)));
        return result;
    }

    private async Task<DeliveryStatus> ReconcileOne(NpgsqlTransaction tx, Guid project, Guid delivery, string? environment, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT d.task_id,d.final_commit_sha,d.build_digest,d.change_set_id,d.repo_key,d.manifest_sha256,b.storage_key FROM delivery.delivery_record d JOIN intake.blob b ON b.project_id=d.project_id AND b.blob_id=d.blob_id WHERE d.project_id=@project AND d.delivery_id=@delivery", ("project", project), ("delivery", delivery));
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var task = reader.GetGuid(0); var commit = reader.GetString(1); var build = reader.GetString(2); var changeSet = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3); var repo = reader.GetString(4); var storageKey = reader.GetString(6); await reader.DisposeAsync();
        string manifestState;
        await using (var artifact = await blobs.OpenReadAsync(project, storageKey, ct)) using (var document = await JsonDocument.ParseAsync(artifact, cancellationToken: ct))
        {
            manifestState = RequiredString(document.RootElement, "status");
            if (manifestState == "verified" && (document.RootElement.GetProperty("testEvidence").EnumerateArray().Any(item => RequiredString(item, "result") != "passed") ||
                document.RootElement.GetProperty("anchorResolutions").EnumerateArray().Any(item => RequiredString(item, "verification") is "missing" or "mismatch"))) manifestState = "rejected";
        }
        Guid? baseline = null; string? changeState = null;
        if (changeSet.HasValue) await using (var find = Command(tx, "SELECT state,(SELECT baseline_id FROM gov.baseline b WHERE b.project_id=cs.project_id AND b.change_set_id=cs.change_set_id AND b.sealed) FROM gov.change_set cs WHERE project_id=@project AND change_set_id=@change", ("project", project), ("change", changeSet.Value)))
        { await using var found = await find.ExecuteReaderAsync(ct); if (await found.ReadAsync(ct)) { changeState = found.GetString(0); baseline = found.IsDBNull(1) ? null : found.GetGuid(1); } }
        var deployed = 0L; var rolledBack = 0L;
        await using (var deployments = Command(tx, """
            WITH scoped AS (
                SELECT environment,final_commit_sha,build_digest,deployed_at,deployment_id,
                       row_number() OVER(PARTITION BY environment ORDER BY deployed_at DESC,deployment_id DESC) AS rank
                FROM delivery.deployment_record
                WHERE project_id=@project AND repo_key=@repo AND (@environment::text IS NULL OR environment=@environment::text))
            SELECT environment,final_commit_sha=@commit AND build_digest=@build AS matches
            FROM scoped WHERE rank=1
            """, ("project", project), ("repo", repo), ("commit", commit), ("build", build), ("environment", Db(environment))))
        {
            await using var found = await deployments.ExecuteReaderAsync(ct);
            while (await found.ReadAsync(ct)) { if (found.GetBoolean(1)) deployed++; else rolledBack++; }
        }
        var status = manifestState == "rejected" ? "divergent" : manifestState == "evidence_missing" || deployed + rolledBack == 0 ? "evidence_missing" :
            rolledBack > 0 && deployed > 0 ? "divergent" : rolledBack > 0 ? "rolled_back" : changeSet.HasValue && changeState != "published" ? "pending_review" : "aligned";
        var unknowns = new List<ContextUnknown>();
        if (status == "evidence_missing") unknowns.Add(new("deployment", environment is null ? "No deployment evidence exists for this delivery." : $"No current {environment} deployment evidence matches the exact commit and build.", "blocking", "missing"));
        if (status == "rolled_back") unknowns.Add(new("deployment", environment is null ? "The latest deployment in every observed environment supersedes this delivery." : $"A later {environment} deployment supersedes this delivery; reconciliation and acceptance must be repeated.", "warning", "stale"));
        if (status == "divergent" && rolledBack > 0 && deployed > 0) unknowns.Add(new("deployment", "Observed environments disagree: at least one runs this delivery and another has rolled back or moved to a different build.", "blocking", "conflicting", "Reconcile each environment and repeat acceptance against the intended target."));
        if (status == "pending_review") unknowns.Add(new("knowledge", "Knowledge change awaits approval and publication.", "warning", "unverified"));
        return new(delivery, task, commit, build, changeSet, baseline, status, unknowns);
    }

    private async Task<TaskContextView> ReadContext(NpgsqlTransaction tx, Guid principal, Guid project, Guid context, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT body,contract_digest,baseline_id,policy_epoch FROM delivery.task_context WHERE project_id=@project AND context_id=@context", ("project", project), ("context", context));
        string body, digest; Guid baseline; long epoch;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); body = reader.GetString(0); digest = reader.GetString(1); baseline = reader.GetGuid(2); epoch = reader.GetInt64(3);
        }
        var result = Deserialize<TaskContextView>(JsonDocument.Parse(body).RootElement);
        var actualDigest = CanonicalJson.Sha256(JsonSerializer.SerializeToElement(result with { Digest = string.Empty }, Json));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(actualDigest))) throw ProjectIdentityException.Conflict("The immutable context digest no longer matches its body.");
        var currentBaseline = await Scalar<Guid>(tx, "SELECT current_baseline_id FROM identity.project WHERE project_id=@project", ct, ("project", project));
        var mode = baseline == currentBaseline ? ReadMode.Published : ReadMode.Historical;
        var read = await access.CreateReadContextAsync(principal, project, new(mode, ReadPurpose.Export, baseline, null, null), ct);
        foreach (var reference in result.RequiredRefs) await access.AuthorizeReadAsync(read, reference.UnitId, mode == ReadMode.Historical ? "read_history" : "read", reference.RevisionId, ct);
        var currentEpoch = await Scalar<long>(tx, "SELECT policy_epoch FROM identity.project WHERE project_id=@project", ct, ("project", project));
        var changed = await Scalar<long>(tx, "SELECT count(*) FROM delivery.context_dependency d JOIN identity.project p ON p.project_id=d.project_id LEFT JOIN gov.baseline_entry e ON e.project_id=d.project_id AND e.baseline_id=p.current_baseline_id AND e.unit_id=d.unit_id WHERE d.project_id=@project AND d.context_id=@context AND d.required AND (e.revision_id IS NULL OR e.revision_id<>d.revision_id)", ct,
            ("project", project), ("context", context));
        var invalidations = await Scalar<long>(tx, "SELECT count(*) FROM delivery.context_invalidation WHERE project_id=@project AND context_id=@context", ct, ("project", project), ("context", context));
        var stale = epoch != currentEpoch || changed > 0 || invalidations > 0 || result.ExpiresAt <= DateTimeOffset.UtcNow;
        return result with { Status = stale ? "stale" : result.Status };
    }

    private static async Task InvalidateTaskContexts(NpgsqlTransaction tx, Guid project, Guid task, string cause, CancellationToken ct) =>
        await Execute(tx, "INSERT INTO delivery.context_invalidation(project_id,invalidation_id,context_id,cause) SELECT @project,gen_random_uuid(),context_id,@cause FROM delivery.task_context WHERE project_id=@project AND task_id=@task AND NOT EXISTS (SELECT 1 FROM delivery.context_invalidation i WHERE i.project_id=task_context.project_id AND i.context_id=task_context.context_id AND i.cause=@cause)", ct, ("project", project), ("task", task), ("cause", cause));

    private async Task<List<Guid>> ApprovalIds(NpgsqlTransaction tx, Guid project, Guid task, CancellationToken ct)
    {
        var items = new List<Guid>(); await using var command = Command(tx, """
            SELECT DISTINCT rd.decision_id FROM gov.review_decision rd
            JOIN gov.change_set cs ON cs.project_id=rd.project_id AND cs.change_set_id=rd.change_set_id
            LEFT JOIN delivery.delivery_record d ON d.project_id=cs.project_id AND d.change_set_id=cs.change_set_id
            WHERE rd.project_id=@project AND rd.decision='approve' AND (d.task_id=@task OR EXISTS(
              SELECT 1 FROM gov.change_item ci WHERE ci.project_id=cs.project_id AND ci.change_set_id=cs.change_set_id
                AND (ci.base_revision_id IN (SELECT target_revision_id FROM collab.impact WHERE project_id=@project AND task_id=@task)
                  OR ci.proposed_revision_id IN (SELECT target_revision_id FROM collab.impact WHERE project_id=@project AND task_id=@task))))
            """, ("project", project), ("task", task));
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) items.Add(reader.GetGuid(0)); return items;
    }

    private static async Task<List<RevisionRef>> AcceptanceCases(NpgsqlTransaction tx, Guid project, Guid baseline, List<ImpactView> impacts, CancellationToken ct)
    {
        var result = new List<RevisionRef>(); foreach (var impact in impacts.Where(item => item.Target is not null))
            if (await Scalar<long>(tx, "SELECT count(*) FROM core.unit WHERE project_id=@project AND unit_id=@unit AND kind IN ('BusinessCase','AcceptanceTest')", ct, ("project", project), ("unit", impact.Target!.UnitId)) > 0) result.Add(new RevisionRef(impact.Target.UnitId, impact.Target.RevisionId!.Value));
        return result;
    }

    private static async Task<List<ImpactView>> Impacts(NpgsqlTransaction tx, Guid project, Guid task, CancellationToken ct)
    {
        var result = new List<ImpactView>(); await using var command = Command(tx, "SELECT impact_id,task_id,target_unit_id,target_revision_id,locator,path,disposition,inspection_state,investigation_id,reason,risk,assigned_task_id,evidence_refs,close_state,version FROM collab.impact WHERE project_id=@project AND task_id=@task", ("project", project), ("task", task));
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : new(reader.GetGuid(2), reader.GetGuid(3)), Element(reader.GetString(4)), Elements(reader.GetString(5)), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11), Elements(reader.GetString(12)), reader.GetString(13), reader.GetInt64(14))); return result;
    }

    private static async Task<List<ContextEvidence>> Evidence(NpgsqlTransaction tx, Guid project, Guid task, CancellationToken ct)
    {
        var result = new List<ContextEvidence>(); await using var command = Command(tx, "SELECT evidence_refs FROM collab.investigation WHERE project_id=@project AND task_id=@task", ("project", project), ("task", task));
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result.AddRange(Deserialize<List<ContextEvidence>>(JsonDocument.Parse(reader.GetString(0)).RootElement)); return result;
    }

    private async Task<List<ContextEvidence>> ExistingEvidence(NpgsqlTransaction tx, Guid project, RevisionRef normative, RevisionRef observed, string build, string environment, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT evidence_refs FROM delivery.conformance WHERE project_id=@project AND normative_revision_id=@normative AND observed_revision_id=@observed AND build_digest=@build AND environment=@environment ORDER BY created_at DESC LIMIT 1", ("project", project), ("normative", normative.RevisionId), ("observed", observed.RevisionId), ("build", build), ("environment", environment));
        var value = await command.ExecuteScalarAsync(ct); return value is string json ? Deserialize<List<ContextEvidence>>(JsonDocument.Parse(json).RootElement) : [];
    }

    private async Task<Guid> StoreBlob(Guid project, string raw, NpgsqlTransaction tx, Guid actor, CancellationToken ct)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw)); var stored = await blobs.StoreAsync(project, stream, 10 * 1024 * 1024, ct); var id = Guid.NewGuid();
        await Execute(tx, "INSERT INTO intake.blob(project_id,blob_id,sha256,byte_size,storage_key,scan_status,created_by) VALUES(@project,@id,@sha,@size,@key,'clean',@actor)", ct, ("project", project), ("id", id), ("sha", stored.Sha256), ("size", stored.ByteSize), ("key", stored.StorageKey), ("actor", actor)); return id;
    }

    private static async Task AuthorizeExact(NpgsqlTransaction tx, Guid principal, Guid project, Guid? baseline, RevisionRef reference, CancellationToken ct)
    {
        var count = await Scalar<long>(tx, "SELECT count(*) FROM core.revision r JOIN core.unit u ON u.project_id=r.project_id AND u.unit_id=r.unit_id WHERE r.project_id=@project AND r.revision_id=@revision AND r.unit_id=@unit AND u.archived_at IS NULL AND (@baseline IS NULL OR EXISTS(SELECT 1 FROM gov.baseline_entry e WHERE e.project_id=@project AND e.baseline_id=@baseline AND e.unit_id=@unit AND e.revision_id=@revision))", ct,
            ("project", project), ("revision", reference.RevisionId), ("unit", reference.UnitId), ("baseline", baseline));
        if (count == 0) throw ProjectIdentityException.NotFound();
    }

    private static async Task<JsonElement> RevisionPayload(NpgsqlTransaction tx, Guid project, RevisionRef reference, CancellationToken ct)
    { var value = await Scalar<string>(tx, "SELECT payload::text FROM core.revision WHERE project_id=@project AND revision_id=@revision AND unit_id=@unit", ct, ("project", project), ("revision", reference.RevisionId), ("unit", reference.UnitId)); return Element(value ?? throw ProjectIdentityException.NotFound()); }
    private static async Task<TaskRowData> TaskRow(NpgsqlTransaction tx, Guid project, Guid task, bool update, CancellationToken ct)
    {
        await using var command = Command(tx, $"SELECT base_baseline_id,target_summary,state,version,created_by,classification FROM collab.task WHERE project_id=@project AND task_id=@task {(update ? "FOR UPDATE" : "")}", ("project", project), ("task", task));
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetGuid(4), reader.GetString(5));
    }
    private static async Task<TaskView> ReadTask(NpgsqlTransaction tx, Guid project, Guid task, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT task_id,title,intent,root_unit_id,base_baseline_id,classification,state,version,target_summary,external_ref FROM collab.task WHERE project_id=@project AND task_id=@task", ("project", project), ("task", task));
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); var summary = JsonDocument.Parse(reader.GetString(8)).RootElement;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
            Deserialize<List<TargetChange>>(summary.GetProperty("targetChanges")), Deserialize<List<RevisionRef>>(summary.GetProperty("preserve")), Deserialize<List<Unknown>>(summary.GetProperty("unknowns")), summary.TryGetProperty("nextAction", out var next) ? next.GetString() : null,
            summary.TryGetProperty("contextId", out var context) && Guid.TryParse(context.GetString(), out var id) ? id : null, Element(reader.GetString(9)));
    }

    private static async Task<MemberData> RequireMember(NpgsqlTransaction tx, Guid project, Guid principal, bool write, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT p.version,p.policy_epoch,m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active WHERE p.project_id=@project AND p.archived_at IS NULL", ("project", project), ("principal", principal));
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); var roles = reader.GetFieldValue<string[]>(2); var result = new MemberData(reader.GetInt64(0), reader.GetInt64(1), roles); await reader.DisposeAsync();
        if (write && !roles.Any(role => role is "Contributor" or "ProjectAdmin" or "DeliveryDev" or "Verifier" or "CIService")) throw ProjectIdentityException.Forbidden(); return result;
    }
    private static async Task SetContext(NpgsqlTransaction tx, Guid project, Guid principal, CancellationToken ct) => await Execute(tx, "SELECT set_config('atlas.project_id',@project::text,true),set_config('atlas.principal_id',@principal::text,true)", ct, ("project", project), ("principal", principal));
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static NpgsqlCommand Command(NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] values) { var command = tx.Connection!.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var value in values) Add(command, value.Name, value.Value); return command; }
    private static void Add(NpgsqlCommand command, string name, object? value) { var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value); if (value is null) parameter.NpgsqlDbType = name is "after" or "baseline" or "previous" ? NpgsqlDbType.Uuid : NpgsqlDbType.Text; }
    private static async Task Execute(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] values) { await using var command = Command(tx, sql, values); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<T?> Scalar<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] values) { await using var command = Command(tx, sql, values); var result = await command.ExecuteScalarAsync(ct); return result is null or DBNull ? default : (T)result; }
    private static object? Db(object? value) => value ?? DBNull.Value;
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static List<JsonElement> Elements(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    private static T Deserialize<T>(JsonElement element) => element.Deserialize<T>(Json) ?? throw new JsonException("Invalid stored contract.");
    private static string RequiredString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw ProjectIdentityException.Invalid($"{property} is required.");
    private static Guid RequiredGuid(JsonElement element, string property) => Guid.TryParse(RequiredString(element, property), out var result) ? result : throw ProjectIdentityException.Invalid($"{property} must be a UUID.");
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static string Digest<T>(T value) => Sha256(JsonSerializer.Serialize(value, Json));
    private static string EvidenceHash(IEnumerable<ContextEvidence> evidence) => CanonicalJson.Sha256(JsonSerializer.SerializeToElement(evidence, Json));
    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid? Cursor(string? value) { if (string.IsNullOrEmpty(value)) return null; try { return new Guid(Convert.FromBase64String(value)); } catch { throw ProjectIdentityException.Invalid("Invalid cursor."); } }
    private static async Task<T?> Replay<T>(NpgsqlTransaction tx, Guid project, Guid principal, string operation, string key, string digest, CancellationToken ct)
    { var value = await Scalar<string>(tx, "SELECT result_ref::text FROM ops.idempotency WHERE project_id=@project AND principal_id=@principal AND operation_id=@operation AND idempotency_key=@key", ct, ("project", project), ("principal", principal), ("operation", operation), ("key", key)); if (value is null) return default; var element = Element(value); if (element.GetProperty("requestSha256").GetString() != digest) throw ProjectIdentityException.Conflict("Idempotency key was reused for different input."); return Deserialize<T>(element.GetProperty("result")); }
    private static async Task Save<T>(NpgsqlTransaction tx, Guid project, Guid principal, string operation, string key, string digest, T result, string requestId, string? trace, CancellationToken ct) =>
        await Execute(tx, "INSERT INTO ops.idempotency(project_id,record_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@record,@principal,@operation,@key,@digest,@result::jsonb); INSERT INTO ops.audit_event(project_id,audit_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@audit,@principal,@operation,@resource::jsonb,'OK',@request,@trace)", ct,
            ("project", project), ("record", Guid.NewGuid()), ("principal", principal), ("operation", operation), ("key", key), ("digest", digest), ("result", JsonSerializer.Serialize(new { requestSha256 = digest, result }, Json)),
            ("audit", Guid.NewGuid()), ("resource", JsonSerializer.Serialize(result, Json)), ("request", requestId), ("trace", trace));
    private sealed record AcceptanceRecord(string TestId, string Result, string? BuildDigest, string? Environment, IReadOnlyList<ContextEvidence> Evidence);
    private sealed record TaskRowData(Guid BaseBaseline, string Summary, string State, long Version, Guid CreatedBy, string Classification);
    private sealed record MemberData(long Version, long Epoch, string[] Roles);
}
