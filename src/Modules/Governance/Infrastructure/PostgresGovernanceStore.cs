using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Governance.Contracts;
using Governance.Baselines;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Application;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Governance.Infrastructure;

public sealed class PostgresGovernanceStore(string connectionString, PostgresKnowledgeStore knowledge, PostgresAccessApplicabilityStore access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> States = ["draft", "submitted", "in_review", "changes_requested", "approved", "rejected", "published", "withdrawn", "conflict"];
    private static readonly HashSet<string> Actions = ["add", "revise", "retire", "preserve", "revalidate"];

    public async Task<ChangeSetPage> ListChangeSetsAsync(Guid principalId, Guid projectId, string? state, bool mine, string? cursor, int limit, CancellationToken ct)
    {
        if (state is not null && !States.Contains(state)) throw ProjectIdentityException.Invalid("The change-set state is invalid.");
        var after = DecodeGuidCursor(cursor);
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, principalId, ct);
        await RequireMemberAsync(tx, projectId, principalId, false, ct);
        var ids = new List<Guid>();
        await using (var command = tx.CreateCommand())
        {
            command.CommandText = """
                SELECT change_set_id FROM gov.change_set
                WHERE project_id=@project AND (@state IS NULL OR state=@state)
                  AND (@actor IS NULL OR created_by=@actor) AND (@after IS NULL OR change_set_id>@after)
                ORDER BY change_set_id LIMIT @limit;
                """;
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)state ?? DBNull.Value);
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, mine ? principalId : DBNull.Value);
            command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }
        string? next = ids.Count > limit ? EncodeGuidCursor(ids[limit - 1]) : null;
        if (ids.Count > limit) ids.RemoveAt(limit);
        var items = new List<ChangeSetView>();
        foreach (var id in ids)
        {
            var item = await ReadChangeSetAsync(tx, projectId, id, ct);
            try { await AuthorizeChangeSetAsync(principalId, projectId, item, ct); items.Add(item); }
            catch (ProjectIdentityException exception) when (exception.StatusCode is 403 or 404) { }
        }
        return new(items, next);
    }

    public async Task<ChangeSetView> GetChangeSetAsync(Guid principalId, Guid projectId, Guid changeSetId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, principalId, ct);
        await RequireMemberAsync(tx, projectId, principalId, false, ct);
        var result = await ReadChangeSetAsync(tx, projectId, changeSetId, ct);
        await AuthorizeChangeSetAsync(principalId, projectId, result, ct);
        return result;
    }

    private async Task AuthorizeChangeSetAsync(Guid principalId, Guid projectId, ChangeSetView changeSet, CancellationToken ct)
    {
        ResolvedReadContext candidateContext;
        if (changeSet.PublishedBaselineId.HasValue)
            candidateContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Historical, ReadPurpose.Change, changeSet.PublishedBaselineId, null, null), ct);
        else
            candidateContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Draft, ReadPurpose.Change, changeSet.BaseBaselineId, changeSet.ChangeSetId, null), ct);
        ResolvedReadContext baseContext;
        try { baseContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Published, ReadPurpose.Change, changeSet.BaseBaselineId, null, null), ct); }
        catch (ProjectIdentityException) { baseContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Historical, ReadPurpose.Change, changeSet.BaseBaselineId, null, null), ct); }
        foreach (var item in changeSet.Items)
        {
            if (item.After is not null) await access.AuthorizeReadAsync(candidateContext, item.UnitId, candidateContext.Mode == ReadMode.Historical ? "read_history" : "read", item.After.RevisionId, ct);
            if (item.Before is not null) await access.AuthorizeReadAsync(baseContext, item.UnitId, baseContext.Mode == ReadMode.Historical ? "read_history" : "read", item.Before.RevisionId, ct);
        }
    }

    public Task<GovernanceMutation<ChangeSetView>> CreateChangeSetAsync(AtlasPrincipal actor, Guid projectId, CreateChangeSetRequest request, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, null, "createChangeSet", null, key, request, requestId, traceId, async (tx, id, row) =>
        {
            ValidateTitle(request.Title);
            if (request.BaseBaselineId == Guid.Empty || !await SealedBaselineExistsAsync(tx, projectId, request.BaseBaselineId, ct)) throw ProjectIdentityException.NotFound();
            var changeSetId = Guid.NewGuid();
            await ExecuteAsync(tx, "INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,created_by) VALUES(@project,@id,@base,@title,'draft',@actor);", ct,
                ("project", projectId), ("id", changeSetId), ("base", request.BaseBaselineId), ("title", request.Title.Trim()), ("actor", actor.PrincipalId));
            return changeSetId;
        }, ct);

    public Task<GovernanceMutation<ChangeSetView>> AmendChangeSetAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, AmendChangeSetRequest request, long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, changeSetId, "amendChangeSet", expectedVersion, key, request, requestId, traceId, async (tx, id, row) =>
        {
            RequireEditable(row);
            if (request.Title is not null) ValidateTitle(request.Title);
            if (request.Summary is not null) ValidateSummary(request.Summary);
            if (request.Items is not null)
            {
                if (request.Items.Select(item => item.UnitId).Distinct().Count() != request.Items.Count) throw ProjectIdentityException.Invalid("A unit can occur only once in a change set.");
                foreach (var item in request.Items) await ValidateItemAsync(tx, projectId, row.BaseBaselineId, item, ct);
                await ExecuteAsync(tx, "DELETE FROM gov.change_item WHERE project_id=@project AND change_set_id=@id;", ct, ("project", projectId), ("id", id));
                foreach (var item in request.Items)
                    await ExecuteAsync(tx, """
                        INSERT INTO gov.change_item(project_id,item_id,change_set_id,unit_id,base_revision_id,proposed_revision_id,action,dependency_group,reason)
                        VALUES(@project,@item,@id,@unit,@base,@proposed,@action,@group,@reason);
                        """, ct, ("project", projectId), ("item", Guid.NewGuid()), ("id", id), ("unit", item.UnitId),
                        ("base", (object?)item.BaseRevisionId ?? DBNull.Value), ("proposed", (object?)item.ProposedRevisionId ?? DBNull.Value),
                        ("action", item.Action), ("group", item.DependencyGroup.Trim()), ("reason", item.Reason.Trim()));
            }
            await ExecuteAsync(tx, """
                UPDATE gov.change_set SET title=COALESCE(@title,title),summary=COALESCE(@summary::jsonb,summary),version=version+1
                WHERE project_id=@project AND change_set_id=@id;
                """, ct, ("project", projectId), ("id", id), ("title", (object?)request.Title?.Trim() ?? DBNull.Value),
                ("summary", (object?)(request.Summary is null ? null : JsonSerializer.Serialize(request.Summary, Json)) ?? DBNull.Value));
            return id;
        }, ct);

    public Task<GovernanceMutation<ChangeSetView>> SubmitChangeSetAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, SubmitChangeSetRequest request, long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, changeSetId, "submitChangeSet", expectedVersion, key, request, requestId, traceId, async (tx, id, row) =>
        {
            if (row.State is not ("draft" or "changes_requested")) throw ProjectIdentityException.Conflict("Only a draft or returned change set can be submitted.");
            if (row.BaseBaselineId != await CurrentBaselineIdAsync(tx, projectId, ct)) throw ProjectIdentityException.Conflict("The base baseline is stale; reopen the change set on the current baseline.");
            ValidateSummary(request.Summary);
            if (request.ImpactIds.Count != request.ImpactIds.Distinct().Count()) throw ProjectIdentityException.Invalid("impactIds must be unique.");
            await ValidateImpactsAsync(tx, projectId, request.ImpactIds, ct);
            var items = await ReadItemsAsync(tx, projectId, id, ct);
            if (items.Count == 0) throw ProjectIdentityException.Invalid("A change set must contain at least one candidate revision or explicit governance action.");
            await ValidateCandidateReferencesAsync(tx, projectId, id, ct);
            var requirements = Requirements(items);
            var seq = checked((row.SubmittedSeq ?? 0) + 1);
            var digest = SubmitDigest(projectId, id, row.BaseBaselineId, seq, request.Summary, request.ImpactIds, items, requirements);
            await ExecuteAsync(tx, """
                UPDATE gov.change_set SET state='submitted',submitted_seq=@seq,submit_digest=@digest,review_requirements=@requirements::jsonb,summary=@summary::jsonb,version=version+1
                WHERE project_id=@project AND change_set_id=@id;
                INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
                VALUES(@project,@event,'ChangeSetSubmitted.v1',@id,@seq,'1.0',@payload::jsonb,'queued');
                """, ct, ("project", projectId), ("id", id), ("seq", seq), ("digest", digest),
                ("requirements", JsonSerializer.Serialize(requirements, Json)), ("summary", JsonSerializer.Serialize(request.Summary, Json)),
                ("event", Guid.NewGuid()), ("payload", JsonSerializer.Serialize(new { projectId, changeSetId = id, submittedSeq = seq, submitDigest = digest }, Json)));
            return id;
        }, ct);

    public Task<GovernanceMutation<ChangeSetView>> ReviewChangeSetAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, ReviewChangeSetRequest request, long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, changeSetId, "reviewChangeSet", expectedVersion, key, request, requestId, traceId, async (tx, id, row) =>
        {
            if (actor.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
            if (row.State is not ("submitted" or "in_review")) throw ProjectIdentityException.Conflict("The submitted revision is not open for review.");
            if (row.SubmittedSeq != request.SubmittedSeq || !FixedEquals(row.SubmitDigest, request.SubmitDigest)) throw ProjectIdentityException.Conflict("The review does not match the current submitted digest.");
            if (actor.PrincipalId == row.CreatedBy) throw ProjectIdentityException.Forbidden();
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000 || request.SlotIds.Count == 0 || request.SlotIds.Distinct(StringComparer.Ordinal).Count() != request.SlotIds.Count) throw ProjectIdentityException.Invalid("A reason and unique slotIds are required.");
            if (request.Decision is not ("approve" or "reject" or "request_changes")) throw ProjectIdentityException.Invalid("The review decision is invalid.");
            var requirements = ParseRequirements(row.ReviewRequirements);
            var selected = request.SlotIds.Select(slot => requirements.SingleOrDefault(item => item.SlotId == slot) ?? throw ProjectIdentityException.Invalid("A requested approval slot does not exist.")).ToArray();
            await AuthorizeChangeSetAsync(actor.PrincipalId, projectId, await ReadChangeSetAsync(tx, projectId, id, ct), ct);
            var roles = await RolesAsync(tx, projectId, actor.PrincipalId, ct);
            foreach (var slot in selected)
            {
                if (!roles.Contains(slot.Role, StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden();
                if (await DecisionExistsAsync(tx, projectId, id, request.SubmittedSeq, slot.SlotId, ct)) throw ProjectIdentityException.Conflict("This approval slot already has a decision for the submitted revision.");
                await ExecuteAsync(tx, """
                    INSERT INTO gov.review_decision(project_id,decision_id,change_set_id,submitted_seq,submit_digest,approval_role,scope,decision,reason,anchor,actor_id)
                    VALUES(@project,@decision,@id,@seq,@digest,@role,@scope::jsonb,@result,@reason,@anchor::jsonb,@actor);
                    """, ct, ("project", projectId), ("decision", Guid.NewGuid()), ("id", id), ("seq", request.SubmittedSeq), ("digest", request.SubmitDigest),
                    ("role", slot.Role), ("scope", JsonSerializer.Serialize(new { slotId = slot.SlotId, units = slot.Scope }, Json)), ("result", request.Decision), ("reason", request.Reason.Trim()),
                    ("anchor", request.Anchor.ValueKind == JsonValueKind.Undefined ? "{}" : request.Anchor.GetRawText()), ("actor", actor.PrincipalId));
            }
            var nextState = request.Decision == "reject" ? "rejected" : request.Decision == "request_changes" ? "changes_requested" :
                await AllApprovedAsync(tx, projectId, id, request.SubmittedSeq, requirements, ct) ? "approved" : "in_review";
            await ExecuteAsync(tx, "UPDATE gov.change_set SET state=@state,version=version+1 WHERE project_id=@project AND change_set_id=@id;", ct, ("project", projectId), ("id", id), ("state", nextState));
            return id;
        }, ct);

    public Task<GovernanceMutation<ChangeSetView>> ReopenChangeSetAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, ReopenChangeSetRequest request, long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, changeSetId, "reopenChangeSet", expectedVersion, key, request, requestId, traceId, async (tx, id, row) =>
        {
            if (row.State is not ("changes_requested" or "rejected" or "approved" or "conflict")) throw ProjectIdentityException.Conflict("This change-set state cannot be reopened.");
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw ProjectIdentityException.Invalid("A bounded reopen reason is required.");
            var newBase = request.NewBaseBaselineId ?? row.BaseBaselineId;
            if (newBase != await CurrentBaselineIdAsync(tx, projectId, ct) || !await SealedBaselineExistsAsync(tx, projectId, newBase, ct)) throw ProjectIdentityException.Conflict("Reopening requires the current sealed baseline.");
            if (newBase != row.BaseBaselineId) await ValidateRebaseAsync(tx, projectId, id, row.BaseBaselineId, newBase, ct);
            await ExecuteAsync(tx, """
                UPDATE gov.change_set SET state='draft',base_baseline_id=@base,submitted_seq=COALESCE(submitted_seq,0)+1,submit_digest=NULL,review_requirements='[]'::jsonb,version=version+1
                WHERE project_id=@project AND change_set_id=@id;
                """, ct, ("project", projectId), ("id", id), ("base", newBase));
            return id;
        }, ct);

    public async Task<GovernanceMutation<BaselineView>> PublishChangeSetAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, PublishChangeSetRequest request, long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key);
        if (request.BaseBaselineId == Guid.Empty || request.SubmitDigest.Length != 64 || request.SubmitDigest.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character))) throw ProjectIdentityException.Invalid("A lowercase SHA-256 submit digest and base baseline are required.");
        var digest = Digest(new { changeSetId, request, expectedVersion });
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, actor.PrincipalId, ct);
        var project = await RequireMemberAsync(tx, projectId, actor.PrincipalId, false, ct);
        var replay = await FindIdempotencyAsync<GovernanceMutation<BaselineView>>(tx, projectId, actor.PrincipalId, "publishChangeSet", key, digest, ct);
        if (replay is not null) { await tx.CommitAsync(ct); return replay; }
        if (project.Version != expectedVersion) throw ProjectIdentityException.CasMismatch();
        if (!project.Roles.Contains("KnowledgePublisher", StringComparer.Ordinal) || actor.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        var row = await ReadRowAsync(tx, projectId, changeSetId, true, ct);
        if (row.State != "approved" || row.BaseBaselineId != request.BaseBaselineId || row.BaseBaselineId != project.CurrentBaselineId || !FixedEquals(row.SubmitDigest, request.SubmitDigest)) throw ProjectIdentityException.Conflict("The change set is not approved for the current base and submitted digest.");
        var items = await ReadItemsAsync(tx, projectId, changeSetId, ct);
        var requirements = ParseRequirements(row.ReviewRequirements);
        if (!await AllApprovedAsync(tx, projectId, changeSetId, row.SubmittedSeq!.Value, requirements, ct)) throw ProjectIdentityException.Conflict("Required approval slots are incomplete or stale.");
        await ValidateCandidateReferencesAsync(tx, projectId, changeSetId, ct);
        await EnsureReviewAuthorityCurrentAsync(tx, projectId, changeSetId, row.SubmittedSeq.Value, ct);
        var current = await ReadBaselineAsync(tx, projectId, project.CurrentBaselineId, ct);
        var baselineId = Guid.NewGuid(); var number = checked(current.Number + 1); var createdAt = DateTimeOffset.UtcNow;
        var entryMap = await RevisionMapAsync(tx, projectId, current.BaselineId, ct);
        foreach (var item in items)
        {
            if (item.Action is "add" or "revise") entryMap[item.UnitId] = item.After!;
            else if (item.Action == "retire") entryMap.Remove(item.UnitId);
        }
        var entries = entryMap.OrderBy(entry => entry.Key).Select(entry => new { unitId = entry.Key, revisionId = entry.Value.RevisionId }).ToArray();
        var baselineDigest = CanonicalJson.Sha256(JsonSerializer.SerializeToElement(new { projectId, parentBaselineId = current.BaselineId, number, entries }, Json));
        await ExecuteAsync(tx, """
            INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,parent_baseline_id,change_set_id,registry_version,digest,sealed,created_by,created_at)
            VALUES(@project,@baseline,@number,@parent,@changeSet,@registry,@digest,false,@actor,@createdAt);
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
            SELECT @project,@baseline,unit_id,revision_id FROM gov.baseline_entry
            WHERE project_id=@project AND baseline_id=@parent
              AND unit_id NOT IN (SELECT unit_id FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet);
            UPDATE core.revision SET sealed=true WHERE project_id=@project AND NOT sealed AND revision_id IN
              (SELECT proposed_revision_id FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet AND proposed_revision_id IS NOT NULL);
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
            SELECT project_id,@baseline,unit_id,proposed_revision_id FROM gov.change_item
            WHERE project_id=@project AND change_set_id=@changeSet AND action IN ('add','revise');
            """, ct, ("project", projectId), ("baseline", baselineId), ("number", number), ("parent", current.BaselineId), ("changeSet", changeSetId),
            ("registry", current.RegistryVersion), ("digest", baselineDigest), ("actor", actor.PrincipalId), ("createdAt", createdAt));
        await ExecuteAsync(tx, """
            UPDATE gov.baseline SET sealed=true WHERE project_id=@project AND baseline_id=@baseline;
            UPDATE identity.project SET current_baseline_id=@baseline,version=version+1 WHERE project_id=@project;
            UPDATE gov.change_set SET state='published',version=version+1 WHERE project_id=@project AND change_set_id=@changeSet;
            INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
            VALUES(@project,@event,'BaselinePublished.v1',@baseline,@number,'1.0',@payload::jsonb,'queued');
            """, ct, ("project", projectId), ("baseline", baselineId), ("number", number), ("digest", baselineDigest), ("changeSet", changeSetId), ("event", Guid.NewGuid()),
            ("payload", JsonSerializer.Serialize(new { projectId, baselineId, number, changeSetId, baselineDigest }, Json)));
        var baseline = new BaselineView(baselineId, number, current.BaselineId, changeSetId, current.RegistryVersion, baselineDigest, createdAt);
        await BaselineReviewInvalidation.RecordAsync(tx, projectId, baselineId, number, changeSetId, createdAt, ct);
        var result = new GovernanceMutation<BaselineView>(baseline, checked(project.Version + 1));
        await SaveResultAsync(tx, projectId, actor.PrincipalId, "publishChangeSet", key, digest, result, requestId, traceId, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<BaselinePage> ListBaselinesAsync(Guid principalId, Guid projectId, string? cursor, int limit, CancellationToken ct)
    {
        long? after = DecodeLongCursor(cursor);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, principalId, ct); await RequireMemberAsync(tx, projectId, principalId, false, ct);
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT baseline_id,baseline_no,parent_baseline_id,change_set_id,registry_version,digest,created_at FROM gov.baseline WHERE project_id=@project AND sealed AND (@after IS NULL OR baseline_no<@after) ORDER BY baseline_no DESC LIMIT @limit;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, (object?)after ?? DBNull.Value); command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<BaselineView>(); await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) items.Add(ReadBaseline(reader));
        string? next = items.Count > limit ? EncodeLongCursor(items[limit - 1].Number) : null; if (items.Count > limit) items.RemoveAt(limit);
        return new(items, next);
    }

    public async Task<IReadOnlyList<DiffItemView>> DiffBaselineAsync(Guid principalId, Guid projectId, Guid baselineId, Guid against, CancellationToken ct)
    {
        var beforeContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Historical, ReadPurpose.Change, baselineId, null, null), ct);
        var afterContext = await access.CreateReadContextAsync(principalId, projectId, new(ReadMode.Historical, ReadPurpose.Change, against, null, null), ct);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, principalId, ct); await RequireMemberAsync(tx, projectId, principalId, false, ct);
        if (!await SealedBaselineExistsAsync(tx, projectId, baselineId, ct) || !await SealedBaselineExistsAsync(tx, projectId, against, ct)) throw ProjectIdentityException.NotFound();
        var before = await RevisionMapAsync(tx, projectId, baselineId, ct); var after = await RevisionMapAsync(tx, projectId, against, ct);
        var result = new List<DiffItemView>();
        foreach (var unit in before.Keys.Union(after.Keys).Order())
        {
            before.TryGetValue(unit, out var oldRevision); after.TryGetValue(unit, out var newRevision);
            if (oldRevision is not null) await access.AuthorizeReadAsync(beforeContext, unit, "read_history", oldRevision.RevisionId, ct);
            if (newRevision is not null) await access.AuthorizeReadAsync(afterContext, unit, "read_history", newRevision.RevisionId, ct);
            if (oldRevision?.RevisionId == newRevision?.RevisionId) continue;
            var action = oldRevision is null ? "add" : newRevision is null ? "retire" : "revise";
            result.Add(new(unit, action, oldRevision, newRevision, action == "add" ? "Added in target baseline" : action == "retire" ? "Removed in target baseline" : "Revision changed"));
        }
        return result;
    }

    public Task<GovernanceMutation<ChangeSetView>> PlanBaselineRestoreAsync(AtlasPrincipal actor, Guid projectId, Guid baselineId, RestoreBaselineRequest request, string key, string requestId, string? traceId, CancellationToken ct) =>
        MutateChangeSetAsync(actor, projectId, null, "planBaselineRestore", null, key, new { baselineId, request }, requestId, traceId, async (tx, id, row) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw ProjectIdentityException.Invalid("A bounded restore reason is required.");
            var oldContext = await access.CreateReadContextAsync(actor.PrincipalId, projectId, new(ReadMode.Historical, ReadPurpose.Change, baselineId, null, null), ct);
            var currentContext = await access.CreateReadContextAsync(actor.PrincipalId, projectId, new(ReadMode.Published, ReadPurpose.Change, request.CurrentBaselineId, null, null), ct);
            if (request.CurrentBaselineId != await CurrentBaselineIdAsync(tx, projectId, ct) || !await SealedBaselineExistsAsync(tx, projectId, baselineId, ct)) throw ProjectIdentityException.Conflict("Restore requires an existing historical baseline and the current base.");
            var old = await RevisionMapAsync(tx, projectId, baselineId, ct); var current = await RevisionMapAsync(tx, projectId, request.CurrentBaselineId, ct);
            var changeSetId = Guid.NewGuid();
            await ExecuteAsync(tx, "INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,summary,created_by) VALUES(@project,@id,@base,@title,'draft',@summary::jsonb,@actor);", ct,
                ("project", projectId), ("id", changeSetId), ("base", request.CurrentBaselineId), ("title", $"Restore baseline: {request.Reason.Trim()}"), ("summary", "[]"), ("actor", actor.PrincipalId));
            foreach (var unit in old.Keys.Union(current.Keys).Order())
            {
                old.TryGetValue(unit, out var before); current.TryGetValue(unit, out var now);
                if (before is not null) await access.AuthorizeReadAsync(oldContext, unit, "read_history", before.RevisionId, ct);
                if (now is not null) await access.AuthorizeReadAsync(currentContext, unit, "read", now.RevisionId, ct);
                if (before?.RevisionId == now?.RevisionId) continue;
                var action = before is null ? "retire" : now is null ? "add" : "revise";
                await ExecuteAsync(tx, """
                    INSERT INTO gov.change_item(project_id,item_id,change_set_id,unit_id,base_revision_id,proposed_revision_id,action,dependency_group,reason)
                    VALUES(@project,@item,@id,@unit,@base,@proposed,@action,'restore',@reason);
                    """, ct, ("project", projectId), ("item", Guid.NewGuid()), ("id", changeSetId), ("unit", unit), ("base", (object?)now?.RevisionId ?? DBNull.Value),
                    ("proposed", (object?)before?.RevisionId ?? DBNull.Value), ("action", action), ("reason", request.Reason.Trim()));
            }
            return changeSetId;
        }, ct);

    public async Task<GovernanceMutation<ChangeSetView>> SubmitProposalAsync(AtlasPrincipal actor, Guid projectId, SubmitProposalRequest request, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateTitle(request.Title); ValidateKey(key);
        if (request.BaseBaselineId == Guid.Empty || request.Revisions.Count == 0 || request.ImpactIds.Distinct().Count() != request.ImpactIds.Count) throw ProjectIdentityException.Invalid("A base, title, unique impacts, and at least one revision are required.");
        var proposal = await knowledge.CreateProposalBatchAsync(actor, projectId, request.BaseBaselineId, request.Title, request.TaskId, request.Revisions, request.ImpactIds, key, requestId, traceId, ct);
        var view = await GetChangeSetAsync(actor.PrincipalId, projectId, proposal.ChangeSetId, ct);
        return new(view, proposal.ProjectVersion);
    }

    private async Task<GovernanceMutation<ChangeSetView>> MutateChangeSetAsync<TRequest>(AtlasPrincipal actor, Guid projectId, Guid? requestedId, string operation, long? expectedVersion, string key, TRequest request, string requestId, string? traceId,
        Func<NpgsqlTransaction, Guid, ChangeRow, Task<Guid>> action, CancellationToken ct)
    {
        ValidateKey(key); if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        var digest = Digest(new { projectId, requestedId, request, expectedVersion });
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, actor.PrincipalId, ct);
        var project = await RequireMemberAsync(tx, projectId, actor.PrincipalId, operation != "reviewChangeSet", ct);
        var replay = await FindIdempotencyAsync<GovernanceMutation<ChangeSetView>>(tx, projectId, actor.PrincipalId, operation, key, digest, ct);
        if (replay is not null) { await tx.CommitAsync(ct); return replay; }
        if (expectedVersion.HasValue && project.Version != expectedVersion.Value) throw ProjectIdentityException.CasMismatch();
        var id = requestedId ?? Guid.Empty;
        var row = requestedId.HasValue ? await ReadRowAsync(tx, projectId, id, true, ct) : ChangeRow.Empty;
        if (requestedId.HasValue && operation != "reviewChangeSet" && row.CreatedBy != actor.PrincipalId && !project.Roles.Contains("ProjectAdmin", StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden();
        id = await action(tx, id, row);
        await ExecuteAsync(tx, "UPDATE identity.project SET version=version+1 WHERE project_id=@project;", ct, ("project", projectId));
        var result = new GovernanceMutation<ChangeSetView>(await ReadChangeSetAsync(tx, projectId, id, ct), checked(project.Version + 1));
        await SaveResultAsync(tx, projectId, actor.PrincipalId, operation, key, digest, result, requestId, traceId, ct);
        await tx.CommitAsync(ct); return result;
    }

    private static async Task ValidateItemAsync(NpgsqlTransaction tx, Guid projectId, Guid baseBaseline, ChangeItemInput item, CancellationToken ct)
    {
        if (!Actions.Contains(item.Action) || string.IsNullOrWhiteSpace(item.DependencyGroup) || item.DependencyGroup.Length > 200 || string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 4000) throw ProjectIdentityException.Invalid("Change item action, dependency group, and reason are invalid.");
        var baseRevision = await BaselineRevisionAsync(tx, projectId, baseBaseline, item.UnitId, ct);
        if (item.Action == "add")
        {
            if (baseRevision.HasValue || item.BaseRevisionId.HasValue || !item.ProposedRevisionId.HasValue) throw ProjectIdentityException.Invalid("An add requires a proposed revision and no base revision.");
        }
        else
        {
            if (!baseRevision.HasValue || item.BaseRevisionId != baseRevision) throw ProjectIdentityException.Invalid("The item baseRevisionId must match the change-set base baseline.");
            if (item.Action == "revise" && !item.ProposedRevisionId.HasValue || item.Action != "revise" && item.ProposedRevisionId.HasValue) throw ProjectIdentityException.Invalid("The proposed revision does not match the item action.");
        }
        if (item.ProposedRevisionId.HasValue && !await RevisionBelongsAsync(tx, projectId, item.UnitId, item.ProposedRevisionId.Value, ct)) throw ProjectIdentityException.Invalid("The proposed revision must belong to the target unit in this project.");
    }

    private static async Task ValidateCandidateReferencesAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
              SELECT 1 FROM gov.change_item ci LEFT JOIN core.revision_ref rr ON rr.project_id=ci.project_id AND rr.source_revision_id=ci.proposed_revision_id AND rr.required
              LEFT JOIN core.revision target ON target.project_id=ci.project_id AND target.unit_id=rr.target_unit_id AND target.revision_id=rr.target_revision_id
              WHERE ci.project_id=@project AND ci.change_set_id=@changeSet AND rr.ref_id IS NOT NULL AND target.revision_id IS NULL);
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId);
        if (await command.ExecuteScalarAsync(ct) is true) throw ProjectIdentityException.Invalid("A required candidate reference is missing or not exact.");
    }

    private static async Task ValidateRebaseAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, Guid oldBase, Guid newBase, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
              SELECT 1 FROM gov.change_item ci
              JOIN gov.baseline_entry old ON old.project_id=ci.project_id AND old.baseline_id=@old AND old.unit_id=ci.unit_id AND old.revision_id=ci.base_revision_id
              JOIN gov.baseline_entry current ON current.project_id=ci.project_id AND current.baseline_id=@new AND current.unit_id=ci.unit_id
              WHERE ci.project_id=@project AND ci.change_set_id=@changeSet AND current.revision_id<>old.revision_id);
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId); command.Parameters.AddWithValue("old", oldBase); command.Parameters.AddWithValue("new", newBase);
        if (await command.ExecuteScalarAsync(ct) is true) throw ProjectIdentityException.Conflict("A rebased unit changed after the original base; resolve the conflict in a new candidate revision.");
        await ExecuteAsync(tx, """
            UPDATE gov.change_item ci SET base_revision_id=current.revision_id
            FROM gov.baseline_entry current
            WHERE ci.project_id=@project AND ci.change_set_id=@changeSet AND current.project_id=ci.project_id AND current.baseline_id=@new AND current.unit_id=ci.unit_id AND ci.action<>'add';
            """, ct, ("project", projectId), ("changeSet", changeSetId), ("new", newBase));
    }

    private static List<ApprovalRequirement> Requirements(IReadOnlyList<ChangeItemView> items) => items.GroupBy(item => item.DependencyGroup, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
    {
        var critical = group.Any(item => item.Action == "retire");
        return new ApprovalRequirement($"business:{group.Key}", "BusinessOwner", group.Select(item => item.UnitId.ToString()).Order(StringComparer.Ordinal).ToArray(), critical ? "high" : "medium");
    }).ToList();

    private static string SubmitDigest(Guid projectId, Guid changeSetId, Guid baseBaselineId, long seq, IReadOnlyList<JsonElement> summary, IReadOnlyList<Guid> impacts, IReadOnlyList<ChangeItemView> items, IReadOnlyList<ApprovalRequirement> requirements) =>
        CanonicalJson.Sha256(JsonSerializer.SerializeToElement(new
        {
            projectId, changeSetId, baseBaselineId, submittedSeq = seq, summary, impactIds = impacts.Order().ToArray(), rolesPolicyVersion = "governance-approval-v1",
            items = items.OrderBy(item => item.UnitId).Select(item => new { item.UnitId, item.Action, item.BaseRevisionId, item.ProposedRevisionId, item.DependencyGroup, item.Reason, beforeHash = item.Before?.ContentSha256, afterHash = item.After?.ContentSha256 }),
            evidenceDigest = CanonicalJson.Sha256(JsonSerializer.SerializeToElement(items.OrderBy(item => item.UnitId).Select(item => new { item.UnitId, before = item.Before?.ContentSha256, after = item.After?.ContentSha256 }).ToArray(), Json)), requirements
        }, Json));

    private static async Task<ChangeSetView> ReadChangeSetAsync(NpgsqlTransaction tx, Guid projectId, Guid id, CancellationToken ct)
    {
        var row = await ReadRowAsync(tx, projectId, id, false, ct); var items = await ReadItemsAsync(tx, projectId, id, ct);
        var requirements = ParseRequirements(row.ReviewRequirements); var decisions = await ReadDecisionsAsync(tx, projectId, id, row.SubmittedSeq, ct);
        var approvals = requirements.Select(requirement => new ApprovalSlotView(requirement.SlotId, requirement.Role, requirement.Scope, requirement.Risk,
            decisions.Any(decision => decision.SlotId == requirement.SlotId && decision.Decision == "reject") ? "rejected" :
            decisions.Any(decision => decision.SlotId == requirement.SlotId && decision.Decision == "approve") ? "approved" : "pending")).ToArray();
        var unknowns = new List<JsonElement>();
        foreach (var item in items.Where(item => item.After is not null))
        {
            await using var command = tx.CreateCommand(); command.CommandText = "SELECT COALESCE(payload->'unknowns','[]'::jsonb) FROM core.revision WHERE project_id=@project AND revision_id=@revision;";
            command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("revision", item.After!.RevisionId);
            if (await command.ExecuteScalarAsync(ct) is string json && JsonDocument.Parse(json).RootElement.ValueKind == JsonValueKind.Array) unknowns.AddRange(JsonDocument.Parse(json).RootElement.EnumerateArray().Select(value => value.Clone()));
        }
        return new(id, row.BaseBaselineId, row.Title, row.Version, row.State, row.SubmittedSeq, row.SubmitDigest, items, approvals, Elements(row.Summary), unknowns, row.PublishedBaselineId);
    }

    private static async Task<IReadOnlyList<ChangeItemView>> ReadItemsAsync(NpgsqlTransaction tx, Guid projectId, Guid id, CancellationToken ct)
    {
        var raw = new List<(Guid Unit, string Action, Guid? Base, Guid? Proposed, string Group, string Reason)>();
        await using (var command = tx.CreateCommand())
        {
            command.CommandText = "SELECT unit_id,action,base_revision_id,proposed_revision_id,dependency_group,reason FROM gov.change_item WHERE project_id=@project AND change_set_id=@id ORDER BY unit_id;";
            command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) raw.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetString(4), reader.GetString(5)));
        }
        var result = new List<ChangeItemView>();
        foreach (var item in raw) result.Add(new(item.Unit, item.Action, item.Base, item.Proposed, item.Group, item.Reason,
            item.Base.HasValue ? await ReadRevisionRefAsync(tx, projectId, item.Base.Value, ct) : null, item.Proposed.HasValue ? await ReadRevisionRefAsync(tx, projectId, item.Proposed.Value, ct) : null));
        return result;
    }

    private static async Task<RevisionRefView?> ReadRevisionRefAsync(NpgsqlTransaction tx, Guid projectId, Guid revisionId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT unit_id,revision_id,rev_no,COALESCE(payload->>'title',''),content_sha256 FROM core.revision WHERE project_id=@project AND revision_id=@revision;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("revision", revisionId);
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    private static async Task<ChangeRow> ReadRowAsync(NpgsqlTransaction tx, Guid projectId, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT cs.base_baseline_id,cs.title,cs.state,cs.submitted_seq,cs.submit_digest,cs.review_requirements,cs.summary,cs.created_by,cs.version,b.baseline_id FROM gov.change_set cs LEFT JOIN gov.baseline b ON b.project_id=cs.project_id AND b.change_set_id=cs.change_set_id WHERE cs.project_id=@project AND cs.change_set_id=@id" + (forUpdate ? " FOR UPDATE OF cs;" : ";");
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetGuid(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetGuid(9));
    }

    private static async Task<IReadOnlyList<ReviewDecision>> ReadDecisionsAsync(NpgsqlTransaction tx, Guid projectId, Guid id, long? seq, CancellationToken ct)
    {
        if (!seq.HasValue) return [];
        var result = new List<ReviewDecision>(); await using var command = tx.CreateCommand(); command.CommandText = "SELECT approval_role,scope,decision,actor_id FROM gov.review_decision WHERE project_id=@project AND change_set_id=@id AND submitted_seq=@seq;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("seq", seq.Value);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3)));
        return result;
    }

    private static async Task<bool> AllApprovedAsync(NpgsqlTransaction tx, Guid projectId, Guid id, long seq, IReadOnlyList<ApprovalRequirement> requirements, CancellationToken ct)
    {
        if (requirements.Count == 0) return false;
        var decisions = await ReadDecisionsAsync(tx, projectId, id, seq, ct);
        return requirements.All(requirement => decisions.Any(decision => decision.SlotId == requirement.SlotId && decision.Decision == "approve"));
    }

    private static async Task EnsureReviewAuthorityCurrentAsync(NpgsqlTransaction tx, Guid projectId, Guid id, long seq, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();         command.CommandText = """
            SELECT NOT EXISTS (
              SELECT 1 FROM gov.review_decision rd
              WHERE rd.project_id=@project AND rd.change_set_id=@id AND rd.submitted_seq=@seq AND rd.decision='approve'
                AND NOT EXISTS (
                  SELECT 1 FROM identity.membership m JOIN identity.principal p ON p.principal_id=m.principal_id
                  WHERE m.project_id=rd.project_id AND m.principal_id=rd.actor_id AND m.active
                    AND p.active AND p.kind='human' AND rd.approval_role=ANY(m.roles)));
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("seq", seq);
        if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Conflict("An approver's authority has changed; a fresh review is required.");
    }

    private static async Task<bool> DecisionExistsAsync(NpgsqlTransaction tx, Guid projectId, Guid id, long seq, string slotId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM gov.review_decision WHERE project_id=@project AND change_set_id=@id AND submitted_seq=@seq AND scope->>'slotId'=@slot);" ;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("seq", seq); command.Parameters.AddWithValue("slot", slotId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task ValidateImpactsAsync(NpgsqlTransaction tx, Guid projectId, IReadOnlyList<Guid> impacts, CancellationToken ct)
    {
        foreach (var impact in impacts)
        {
            await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM collab.impact WHERE project_id=@project AND impact_id=@impact AND close_state IN ('closed','accepted'));" ;
            command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("impact", impact);
            if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("Every impactId must identify a resolved impact in this project.");
        }
    }

    private static async Task<Dictionary<Guid, RevisionRefView>> RevisionMapAsync(NpgsqlTransaction tx, Guid projectId, Guid baseline, CancellationToken ct)
    {
        var result = new Dictionary<Guid, RevisionRefView>(); await using var command = tx.CreateCommand();
        command.CommandText = "SELECT r.unit_id,r.revision_id,r.rev_no,COALESCE(r.payload->>'title',''),r.content_sha256 FROM gov.baseline_entry be JOIN core.revision r ON r.project_id=be.project_id AND r.revision_id=be.revision_id WHERE be.project_id=@project AND be.baseline_id=@baseline;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("baseline", baseline);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result[reader.GetGuid(0)] = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)); return result;
    }

    private static async Task<IReadOnlyList<object>> BaselineEntriesAsync(NpgsqlTransaction tx, Guid projectId, Guid baseline, CancellationToken ct)
    {
        var result = new List<object>(); await using var command = tx.CreateCommand(); command.CommandText = "SELECT unit_id,revision_id FROM gov.baseline_entry WHERE project_id=@project AND baseline_id=@baseline ORDER BY unit_id;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("baseline", baseline); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new { unitId = reader.GetGuid(0), revisionId = reader.GetGuid(1) }); return result;
    }

    private static async Task<ProjectContext> RequireMemberAsync(NpgsqlTransaction tx, Guid projectId, Guid principal, bool propose, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT p.version,p.current_baseline_id,m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active WHERE p.project_id=@project AND p.archived_at IS NULL FOR UPDATE OF p;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principal);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var result = new ProjectContext(reader.GetInt64(0), reader.GetGuid(1), reader.GetFieldValue<string[]>(2)); await reader.CloseAsync();
        if (propose && !result.Roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden(); return result;
    }

    private static async Task<string[]> RolesAsync(NpgsqlTransaction tx, Guid projectId, Guid principal, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT roles FROM identity.membership WHERE project_id=@project AND principal_id=@principal AND active;"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principal); return await command.ExecuteScalarAsync(ct) as string[] ?? throw ProjectIdentityException.Forbidden(); }

    private static async Task<Guid> CurrentBaselineIdAsync(NpgsqlTransaction tx, Guid projectId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT current_baseline_id FROM identity.project WHERE project_id=@project;"; command.Parameters.AddWithValue("project", projectId); return await command.ExecuteScalarAsync(ct) as Guid? ?? throw ProjectIdentityException.NotFound(); }
    private static async Task<bool> SealedBaselineExistsAsync(NpgsqlTransaction tx, Guid projectId, Guid baseline, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM gov.baseline WHERE project_id=@project AND baseline_id=@baseline AND sealed);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("baseline", baseline); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<Guid?> BaselineRevisionAsync(NpgsqlTransaction tx, Guid projectId, Guid baseline, Guid unit, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT revision_id FROM gov.baseline_entry WHERE project_id=@project AND baseline_id=@baseline AND unit_id=@unit;"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("unit", unit); return await command.ExecuteScalarAsync(ct) as Guid?; }
    private static async Task<bool> RevisionBelongsAsync(NpgsqlTransaction tx, Guid projectId, Guid unit, Guid revision, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM core.revision WHERE project_id=@project AND unit_id=@unit AND revision_id=@revision);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("revision", revision); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<BaselineView> ReadBaselineAsync(NpgsqlTransaction tx, Guid projectId, Guid baseline, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT baseline_id,baseline_no,parent_baseline_id,change_set_id,registry_version,digest,created_at FROM gov.baseline WHERE project_id=@project AND baseline_id=@baseline AND sealed;"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("baseline", baseline); await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadBaseline(reader) : throw ProjectIdentityException.NotFound(); }
    private static BaselineView ReadBaseline(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetString(4), reader.GetString(5), new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));

    private static void RequireEditable(ChangeRow row) { if (row.State is not ("draft" or "changes_requested")) throw ProjectIdentityException.Conflict("A submitted or published change set is immutable; reopen it before editing."); }
    private static void ValidateTitle(string title) { if (string.IsNullOrWhiteSpace(title) || title.Length > 500) throw ProjectIdentityException.Invalid("A title with 1-500 characters is required."); }
    private static void ValidateSummary(IReadOnlyList<JsonElement> summary) { if (summary.Count > 1000 || summary.Any(item => item.ValueKind != JsonValueKind.Object)) throw ProjectIdentityException.Invalid("Summary must contain at most 1000 content block objects."); }
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static List<ApprovalRequirement> ParseRequirements(string json) => JsonSerializer.Deserialize<List<ApprovalRequirement>>(json, Json) ?? [];
    private static List<JsonElement> Elements(string json) => JsonDocument.Parse(string.IsNullOrEmpty(json) || json == "{}" ? "[]" : json).RootElement is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(item => item.Clone()).ToList() : [];
    private static string Digest<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToElement(value, Json));
    private static bool FixedEquals(string? left, string? right) => left is not null && right is not null && left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static async Task<T?> FindIdempotencyAsync<T>(NpgsqlTransaction tx, Guid projectId, Guid actor, string operation, string key, string digest, CancellationToken ct) where T : class
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@actor AND operation_id=@operation AND idempotency_key=@key;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null;
        if (!FixedEquals(reader.GetString(0), digest)) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request."); return JsonSerializer.Deserialize<T>(reader.GetString(1), Json);
    }

    private static async Task SaveResultAsync<T>(NpgsqlTransaction tx, Guid projectId, Guid actor, string operation, string key, string digest, T result, string requestId, string? traceId, CancellationToken ct)
    {
        await ExecuteAsync(tx, """
            INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@actor,@operation,@key,@digest,@result::jsonb);
            INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@actor,@operation,@resource::jsonb,'OK',@request,@trace);
            """, ct, ("project", projectId), ("actor", actor), ("operation", operation), ("key", key), ("digest", digest),
            ("result", JsonSerializer.Serialize(result, Json)), ("resource", JsonSerializer.Serialize(result, Json)), ("request", requestId), ("trace", (object?)traceId ?? DBNull.Value));
    }

    private static async Task ExecuteAsync(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    { await using var command = tx.CreateCommand(); command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); await command.ExecuteNonQueryAsync(ct); }
    private static async Task SetContextAsync(NpgsqlTransaction tx, Guid projectId, Guid principal, CancellationToken ct) => await ExecuteAsync(tx, "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);", ct, ("project", projectId.ToString()), ("principal", principal.ToString()));
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for governance operations."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static string EncodeGuidCursor(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? DecodeGuidCursor(string? cursor) { if (string.IsNullOrEmpty(cursor)) return null; try { return new Guid(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4))); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }
    private static string EncodeLongCursor(long value) => Convert.ToBase64String(BitConverter.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static long? DecodeLongCursor(string? cursor) { if (string.IsNullOrEmpty(cursor)) return null; try { var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4)); return bytes.Length == 8 ? BitConverter.ToInt64(bytes) : throw new FormatException(); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }

    private sealed record ChangeRow(Guid BaseBaselineId, string Title, string State, long? SubmittedSeq, string? SubmitDigest, string ReviewRequirements, string Summary, Guid CreatedBy, long Version, Guid? PublishedBaselineId)
    { public static readonly ChangeRow Empty = new(Guid.Empty, "", "draft", null, null, "[]", "[]", Guid.Empty, 0, null); }
    private sealed record ProjectContext(long Version, Guid CurrentBaselineId, string[] Roles);
    private sealed record ApprovalRequirement(string SlotId, string Role, IReadOnlyList<string> Scope, string Risk);
    private sealed record ReviewDecision(string ApprovalRole, string Scope, string Decision, Guid ActorId)
    { [System.Text.Json.Serialization.JsonIgnore] public string SlotId => JsonDocument.Parse(Scope).RootElement.TryGetProperty("slotId", out var slot) ? slot.GetString()! : ""; }
}
