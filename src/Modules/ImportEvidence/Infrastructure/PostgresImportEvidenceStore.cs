using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ImportEvidence.Application;
using ImportEvidence.Blob;
using ImportEvidence.Contracts;
using ImportEvidence.Sources;
using KnowledgeCore.Application;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace ImportEvidence.Infrastructure;

public sealed class PostgresImportEvidenceStore(string connectionString, IPrivateBlobStorage blobs, IContentScanner scanner,
    PostgresAccessApplicabilityStore access, PostgresKnowledgeStore knowledge)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<UploadView> UploadSourceAsync(AtlasPrincipal actor, Guid projectId, string fileName, string contentType,
        string sourceKind, string? expectedHash, Stream file, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); ValidateFile(fileName, contentType, sourceKind, 100 * 1024 * 1024);
        await using (var auth = await OpenAsync(ct))
        await using (var authTx = await auth.BeginTransactionAsync(ct))
        {
        await SetContextAsync(authTx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(authTx, projectId, actor.PrincipalId, ct);
        }
        var stored = await blobs.StoreAsync(projectId, file, 100 * 1024 * 1024, ct);
        var digest = Hash(key + ":" + (expectedHash ?? stored.Sha256));
        if (expectedHash is not null && (!IsHash(expectedHash) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(stored.Sha256))))
            throw ProjectIdentityException.Invalid("The supplied sha256 does not match the received bytes.");
        await using var content = await blobs.OpenReadAsync(projectId, stored.StorageKey, ct);
        var scan = await scanner.ScanAsync(content, fileName, ct);
        var uploadId = Guid.NewGuid(); var blobId = Guid.NewGuid(); var state = scan.Status == ScanStatus.Clean ? "ready" : "rejected";
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, "uploadSource", key, digest, ct);
        if (replay is not null) { await tx.CommitAsync(ct); return replay.Value.Deserialize<UploadView>(Json)!; }
        blobId = await InsertBlobAsync(tx, projectId, blobId, stored, scan.Status.ToString().ToLowerInvariant(), actor.PrincipalId, ct);
        Guid? revisionId = null; Guid? changeSetId = null;
        if (scan.Status == ScanStatus.Clean)
        {
            changeSetId = Guid.NewGuid(); revisionId = Guid.NewGuid();
            await InsertChangeSetAsync(tx, projectId, changeSetId.Value, $"Source: {Path.GetFileName(fileName)}", actor.PrincipalId, ct);
            var body = Obj(new { sourceKind, locator = new { kind = "file", path = Path.GetFileName(fileName), sha256 = stored.Sha256 }, provenance = new { uploadId, originalName = Path.GetFileName(fileName), contentType, capturedBy = actor.PrincipalId } });
            var input = new RevisionInput(uploadId, Path.GetFileName(fileName), "Source", "1.0", null, "observed", "unverified", Obj(new { }), body, Arr(), Arr(), Arr(), JsonSerializer.SerializeToElement(new[] { "source" }));
            await InsertRevisionAsync(tx, projectId, uploadId, uploadId, revisionId.Value, 1, null, input, actor.PrincipalId, stored.Sha256, ct);
            await LinkSourceAsync(tx, projectId, revisionId.Value, blobId, ct);
            await AddChangeItemAsync(tx, projectId, changeSetId.Value, uploadId, null, revisionId.Value, "add", "Imported source draft", ct);
        }
        await using var command = tx.CreateCommand();
        command.CommandText = """
            INSERT INTO intake.upload(project_id,upload_id,blob_id,original_name,content_type,expires_at,state,created_by)
            VALUES(@project,@upload,@blob,@name,@type,now()+interval '7 days',@state,@actor);
            INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref)
            VALUES(@project,@actor,'uploadSource',@key,@digest,@result::jsonb);
            INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id,safe_metadata)
            VALUES(@project,@actor,'uploadSource',@resource::jsonb,@resultCode,@request,@trace,@scan::jsonb);
            """;
        var view = new UploadView(uploadId, blobId, state, stored.Sha256, stored.ByteSize, 1);
        Add(command, "project", projectId); Add(command, "upload", uploadId); Add(command, "blob", blobId); Add(command, "name", Path.GetFileName(fileName)); Add(command, "type", contentType); Add(command, "state", state); Add(command, "actor", actor.PrincipalId); Add(command, "key", key); Add(command, "digest", digest); Add(command, "result", JsonSerializer.Serialize(view, Json), NpgsqlDbType.Jsonb); Add(command, "resource", JsonSerializer.Serialize(new { uploadId, blobId, revisionId, changeSetId }, Json), NpgsqlDbType.Jsonb); Add(command, "resultCode", scan.Status == ScanStatus.Clean ? "OK" : "REJECTED"); Add(command, "request", requestId); Add(command, "trace", (object?)traceId ?? DBNull.Value); Add(command, "scan", JsonSerializer.Serialize(new { status = scan.Status.ToString().ToLowerInvariant(), reason = scan.Reason }, Json), NpgsqlDbType.Jsonb);
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return view;
    }

    public async Task<UploadView> GetUploadAsync(Guid principalId, Guid projectId, Guid uploadId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, principalId, ct); await RequireProposerAsync(tx, projectId, principalId, ct);
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT u.upload_id,u.blob_id,u.state,b.sha256,b.byte_size,u.version FROM intake.upload u LEFT JOIN intake.blob b ON b.project_id=u.project_id AND b.blob_id=u.blob_id WHERE u.project_id=@project AND u.upload_id=@upload"; Add(command, "project", projectId); Add(command, "upload", uploadId);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        return new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5));
    }

    public async Task<RevisionView> GetSourceAsync(ResolvedReadContext context, Guid sourceId, CancellationToken ct)
    { var source = await knowledge.GetUnitAsync(context, sourceId, ct); if (source.Kind != "Source") throw ProjectIdentityException.NotFound(); return source; }

    public async Task<SourceContent> ReadSourceContentAsync(ResolvedReadContext context, Guid sourceId, CancellationToken ct)
    {
        var source = await GetSourceAsync(context, sourceId, ct);
        // Reading a source summary never implies permission to retrieve its bytes.
        // Reauthorize the explicit source action on every download, including old URLs.
        await access.AuthorizeReadAsync(context, sourceId, "read_source", source.RevisionId, ct);
        Guid project; Guid blob; string key; string hash; long size;
        await using (var connection = await OpenAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
        await SetContextAsync(tx, context.ProjectId, context.PrincipalId, ct);
        await using (var command = tx.CreateCommand())
        {
            command.CommandText = "SELECT b.project_id,b.blob_id,b.storage_key,b.sha256,b.byte_size FROM intake.source_blob_link l JOIN intake.blob b ON b.project_id=l.project_id AND b.blob_id=l.blob_id WHERE l.project_id=@project AND l.source_revision_id=@revision AND b.scan_status='clean'"; Add(command, "project", context.ProjectId); Add(command, "revision", source.RevisionId);
            await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); project = reader.GetGuid(0); blob = reader.GetGuid(1); key = reader.GetString(2); hash = reader.GetString(3); size = reader.GetInt64(4);
        }
        }
        await access.EnsureCurrentAsync(context, ct); var stream = await blobs.OpenReadAsync(project, key, ct);
        var name = source.Body.TryGetProperty("locator", out var locator) && locator.TryGetProperty("path", out var path) ? Path.GetFileName(path.GetString()) : source.Title;
        return new(stream, string.IsNullOrWhiteSpace(name) ? "source.bin" : name, "application/octet-stream", size, hash);
    }

    public async Task<ImportBatchView> UploadPackageAsync(AtlasPrincipal actor, Guid projectId, string fileName, string contentType,
        Guid baseBaselineId, Stream file, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); ValidateFile(fileName, contentType, "package", 100 * 1024 * 1024);
        await using (var auth = await OpenAsync(ct))
        await using (var authTx = await auth.BeginTransactionAsync(ct))
        {
            await SetContextAsync(authTx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(authTx, projectId, actor.PrincipalId, ct);
        }
        var stored = await blobs.StoreAsync(projectId, file, 100 * 1024 * 1024, ct);
        var digest = Hash(key + ":" + baseBaselineId + ":" + stored.Sha256);
        await using (var content = await blobs.OpenReadAsync(projectId, stored.StorageKey, ct))
        { var scan = await scanner.ScanAsync(content, fileName, ct); if (scan.Status == ScanStatus.Unavailable) throw new ProjectIdentityException("DEPENDENCY_UNAVAILABLE", scan.Reason!, 503); if (scan.Status == ScanStatus.Rejected) throw ProjectIdentityException.Invalid(scan.Reason!); }
        NormalizedPackage package; await using (var content = await blobs.OpenReadAsync(projectId, stored.StorageKey, ct)) package = await AkpPackageReader.ReadAsync(content, fileName, ct);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        await RequireCurrentBaselineAsync(tx, projectId, baseBaselineId, ct);
        var persistedReplay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, "uploadAkp", key, digest, ct);
        if (persistedReplay is not null) { await tx.CommitAsync(ct); return persistedReplay.Value.Deserialize<ImportBatchView>(Json)!; }
        Guid? existingImport = null;
        await using (var check = tx.CreateCommand())
        {
            check.CommandText = "SELECT package_sha256,import_id FROM intake.import_batch WHERE project_id=@project AND package_id=@package"; Add(check, "project", projectId); Add(check, "package", package.PackageId);
            await using var reader = await check.ExecuteReaderAsync(ct); if (await reader.ReadAsync(ct)) { if (reader.GetString(0) != stored.Sha256) throw ProjectIdentityException.Conflict("packageId already exists with different content."); existingImport = reader.GetGuid(1); }
        }
        if (existingImport.HasValue) { var existing = await ReadBatchAsync(tx, projectId, existingImport.Value, ct); await tx.CommitAsync(ct); return existing; }
        var importId = Guid.NewGuid(); var blobId = await InsertBlobAsync(tx, projectId, Guid.NewGuid(), stored, "clean", actor.PrincipalId, ct);
        var batch = new ImportBatchView(importId, package.PackageId, baseBaselineId, "preview_ready", 1, [], null, package.Unknowns);
        await using var insert = tx.CreateCommand(); insert.CommandText = "INSERT INTO intake.import_batch(project_id,import_id,package_id,format_version,package_sha256,source_namespace,blob_id,base_baseline_id,state,summary,created_by) VALUES(@project,@import,@package,@format,@hash,@namespace,@blob,@baseline,'preview_ready',@summary::jsonb,@actor)"; Add(insert, "project", projectId); Add(insert, "import", importId); Add(insert, "package", package.PackageId); Add(insert, "format", package.FormatVersion); Add(insert, "hash", stored.Sha256); Add(insert, "namespace", package.Namespace); Add(insert, "blob", blobId); Add(insert, "baseline", baseBaselineId); Add(insert, "summary", JsonSerializer.Serialize(new { unknowns = package.Unknowns, originalName = Path.GetFileName(fileName), contentType }, Json), NpgsqlDbType.Jsonb); Add(insert, "actor", actor.PrincipalId); await insert.ExecuteNonQueryAsync(ct);
        foreach (var entry in package.Entries) await InsertEntryAsync(tx, projectId, importId, entry, await DiffAsync(tx, projectId, baseBaselineId, package.Namespace, entry, ct), ct);
        batch = await ReadBatchAsync(tx, projectId, importId, ct);
        await RecordIdempotencyAsync(tx, projectId, actor.PrincipalId, "uploadAkp", key, digest, batch, requestId, traceId, ct); await tx.CommitAsync(ct); return batch;
    }

    public async Task<ImportBatchPage> ListImportsAsync(Guid principalId, Guid projectId, string? cursor, int limit, CancellationToken ct)
    {
        var after = Decode(cursor); var items = new List<ImportBatchView>();
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, principalId, ct); await RequireProposerAsync(tx, projectId, principalId, ct);
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT import_id FROM intake.import_batch WHERE project_id=@project ORDER BY import_id LIMIT 1000"; Add(command, "project", projectId); var ids = new List<Guid>(); await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        foreach (var id in ids.Where(id => !after.HasValue || id.CompareTo(after.Value) > 0).Take(limit + 1)) items.Add(await ReadBatchAsync(tx, projectId, id, ct));
        string? next = null; if (items.Count > limit) { next = Encode(items[limit - 1].ImportId); items.RemoveAt(limit); } return new(items, next);
    }

    public async Task<ImportBatchView> PreviewAsync(Guid principalId, Guid projectId, Guid importId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, principalId, ct); await RequireProposerAsync(tx, projectId, principalId, ct);
        var state = await LoadBatchStateAsync(tx, projectId, importId, true, ct); if (state.State is "staged" or "discarded") return await ReadBatchAsync(tx, projectId, importId, ct);
        if (state.CurrentBaseline != state.BaseBaseline)
        {
            var package = await ReadPackageAsync(tx, projectId, state.BlobId, ct);
            foreach (var entry in package.Entries)
            {
                var diff = await DiffAsync(tx, projectId, state.CurrentBaseline, package.Namespace, entry, ct);
                await using var update = tx.CreateCommand(); update.CommandText = "UPDATE intake.import_entry SET diff=@diff::jsonb,decision=NULL,reason=NULL,decided_by=NULL,unit_id=@unit,version=version+1 WHERE project_id=@project AND import_id=@import AND external_type=@type AND external_id=@external"; Add(update, "diff", diff, NpgsqlDbType.Jsonb); Add(update, "unit", (object?)FindUnit(diff) ?? DBNull.Value); Add(update, "project", projectId); Add(update, "import", importId); Add(update, "type", entry.ExternalType); Add(update, "external", entry.ExternalId); await update.ExecuteNonQueryAsync(ct);
            }
            await using var baseline = tx.CreateCommand(); baseline.CommandText = "UPDATE intake.import_batch SET base_baseline_id=@baseline,state='preview_ready',version=version+1 WHERE project_id=@project AND import_id=@import"; Add(baseline, "baseline", state.CurrentBaseline); Add(baseline, "project", projectId); Add(baseline, "import", importId); await baseline.ExecuteNonQueryAsync(ct);
        }
        var result = await ReadBatchAsync(tx, projectId, importId, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ImportBatchView> DecideAsync(AtlasPrincipal actor, Guid projectId, Guid importId, ImportDecisionsRequest request, long expected, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); if (request.Decisions.Count == 0 || request.Decisions.Select(d => d.EntryId).Distinct().Count() != request.Decisions.Count) throw ProjectIdentityException.Invalid("Unique import decisions are required.");
        var digest = Hash(JsonSerializer.Serialize(new { expected, request }, Json));
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, "decideImport", key, digest, ct); if (replay is not null) { await tx.CommitAsync(ct); return replay.Value.Deserialize<ImportBatchView>(Json)!; }
        var state = await LoadBatchStateAsync(tx, projectId, importId, true, ct); if (state.Version != expected) throw ProjectIdentityException.CasMismatch(); if (state.CurrentBaseline != state.BaseBaseline) throw ProjectIdentityException.Conflict("The baseline changed; regenerate the preview before decisions."); if (state.State is not ("preview_ready" or "decided")) throw ProjectIdentityException.Conflict("This import cannot accept decisions.");
        var currentEntries = await ReadStoredEntriesAsync(tx, projectId, importId, ct);
        foreach (var decision in request.Decisions)
        {
            if (decision.Decision is not ("reuse_existing" or "new_version" or "new_identity" or "reject" or "needs_evidence") || string.IsNullOrWhiteSpace(decision.Reason)) throw ProjectIdentityException.Invalid("A valid decision and reason are required for every entry.");
            var current = currentEntries.SingleOrDefault(e => e.EntryId == decision.EntryId) ?? throw ProjectIdentityException.Invalid("A decision entry does not belong to this import.");
            var status = current.Diff.GetProperty("status").GetString();
            if (decision.Decision == "reuse_existing" && status is "new" or "invalid" || decision.Decision == "new_version" && status is "new" or "duplicate")
                throw ProjectIdentityException.Invalid($"Decision '{decision.Decision}' is not valid for a {status} entry.");
            await using var command = tx.CreateCommand(); command.CommandText = "UPDATE intake.import_entry SET decision=@decision,reason=@reason,decided_by=@actor,unit_id=COALESCE(@unit,unit_id),version=version+1 WHERE project_id=@project AND import_id=@import AND entry_id=@entry"; Add(command, "decision", decision.Decision); Add(command, "reason", decision.Reason); Add(command, "actor", actor.PrincipalId); Add(command, "unit", (object?)decision.UnitId ?? DBNull.Value); Add(command, "project", projectId); Add(command, "import", importId); Add(command, "entry", decision.EntryId); if (await command.ExecuteNonQueryAsync(ct) != 1) throw ProjectIdentityException.Invalid("A decision entry does not belong to this import.");
        }
        await using var batchUpdate = tx.CreateCommand(); batchUpdate.CommandText = "UPDATE intake.import_batch SET state='decided',version=version+1 WHERE project_id=@project AND import_id=@import"; Add(batchUpdate, "project", projectId); Add(batchUpdate, "import", importId); await batchUpdate.ExecuteNonQueryAsync(ct);
        var result = await ReadBatchAsync(tx, projectId, importId, ct); await RecordIdempotencyAsync(tx, projectId, actor.PrincipalId, "decideImport", key, digest, result, requestId, traceId, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ChangeSetView> StageAsync(AtlasPrincipal actor, Guid projectId, Guid importId, Guid? requestedChangeSet, long expected, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); var digest = Hash(JsonSerializer.Serialize(new { expected, requestedChangeSet }, Json));
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, "stageImport", key, digest, ct); if (replay is not null) { await tx.CommitAsync(ct); return replay.Value.Deserialize<ChangeSetView>(Json)!; }
        var state = await LoadBatchStateAsync(tx, projectId, importId, true, ct); if (state.Version != expected) throw ProjectIdentityException.CasMismatch(); if (state.CurrentBaseline != state.BaseBaseline) throw ProjectIdentityException.Conflict("The baseline changed; regenerate the preview before staging."); if (state.State is not ("preview_ready" or "decided")) throw ProjectIdentityException.Conflict("Only an unstaged import can be staged.");
        var package = await ReadPackageAsync(tx, projectId, state.BlobId, ct); var storedEntries = await ReadStoredEntriesAsync(tx, projectId, importId, ct);
        if (storedEntries.Any(e => e.Diff.GetProperty("status").GetString() == "invalid")) throw ProjectIdentityException.Conflict("Invalid entries block staging.");
        var changeSetId = requestedChangeSet ?? Guid.NewGuid();
        if (requestedChangeSet.HasValue) await RequireOpenChangeSetAsync(tx, projectId, changeSetId, state.BaseBaseline, ct); else await InsertChangeSetAsync(tx, projectId, changeSetId, $"Import: {package.PackageId}", actor.PrincipalId, ct);
        var selected = new Dictionary<string, (StoredEntry Stored, NormalizedEntry Entry, Guid Unit, Guid? BaseRevision)>();
        foreach (var stored in storedEntries)
        {
            var entry = package.Entries.Single(e => e.ExternalType == stored.ExternalType && e.ExternalId == stored.ExternalId); var status = stored.Diff.GetProperty("status").GetString(); var decision = stored.Decision;
            if (decision == "reject") continue;
            if (decision == "needs_evidence") throw ProjectIdentityException.Conflict("Unresolved evidence decisions block staging.");
            if (entry.Kind == "Issue" && entry.Body.TryGetProperty("issueKind", out var issueKind) && issueKind.GetString() == "conflict" && decision is null)
                throw ProjectIdentityException.Conflict("Imported conflict issues need an explicit human decision before staging.");
            if (status == "conflict" && decision is null) throw ProjectIdentityException.Conflict("All conflicts need a decision before staging.");
            if (status == "duplicate" || decision == "reuse_existing") { if (stored.UnitId is null) throw ProjectIdentityException.Conflict("Cannot reuse an entry without an existing unit."); continue; }
            Guid unit = decision == "new_identity" ? Guid.NewGuid() : stored.UnitId ?? Guid.NewGuid();
            Guid? baseRevision = FindUnit(stored.Diff) is null ? null : ReadGuid(stored.Diff, "baseRevisionId");
            selected[Key(entry.ExternalType, entry.ExternalId)] = (stored, entry, unit, baseRevision);
        }
        var revisions = selected.ToDictionary(pair => pair.Key, pair => (RevisionId: Guid.NewGuid(), pair.Value.Unit));
        foreach (var pair in selected)
        {
            var value = pair.Value; var evidence = EvidenceFor(value.Entry, package.Entries, revisions, storedEntries);
            var body = ResolveExternalReferences(value.Entry.Body, revisions, storedEntries);
            var input = new RevisionInput(value.Unit, value.Entry.Title, value.Entry.Kind, "1.0", null, value.Entry.FactKind, value.Entry.Verification,
                value.Entry.Applicability, body, Arr(), evidence, Arr(), value.Entry.CategoryKeys,
                new(value.Stored.Decision == "new_identity" ? package.Namespace + "/identity/" + value.Stored.EntryId : package.Namespace, value.Entry.ExternalType, value.Entry.ExternalId));
            var hash = EntryHash(value.Entry);
            await InsertRevisionAsync(tx, projectId, value.Unit, value.Unit, revisions[pair.Key].RevisionId,
                value.BaseRevision.HasValue ? await NextRevisionAsync(tx, projectId, value.Unit, ct) : 1, value.BaseRevision, input, actor.PrincipalId, hash, ct);
            if (value.Entry.Kind == "Source") await LinkSourceAsync(tx, projectId, revisions[pair.Key].RevisionId, state.BlobId, ct);
            await AddChangeItemAsync(tx, projectId, changeSetId, value.Unit, value.BaseRevision, revisions[pair.Key].RevisionId, value.BaseRevision.HasValue ? "revise" : "add", "Imported candidate; requires normal review and publication", ct);
        }
        // Add evidence and semantic references only after every candidate identity/revision exists.
        foreach (var pair in selected)
        {
            var value = pair.Value; var revision = revisions[pair.Key].RevisionId; var body = ResolveExternalReferences(value.Entry.Body, revisions, storedEntries);
            await InsertReferencesAsync(tx, projectId, revision, body, ct);
            var evidence = EvidenceFor(value.Entry, package.Entries, revisions, storedEntries); await InsertEvidenceAsync(tx, projectId, revision, evidence, actor.PrincipalId, ct);
        }
        await using var finish = tx.CreateCommand(); finish.CommandText = "UPDATE intake.import_batch SET state='staged',change_set_id=@changeSet,version=version+1 WHERE project_id=@project AND import_id=@import; UPDATE identity.project SET version=version+1 WHERE project_id=@project; INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state) VALUES(@project,@event,'import.staged',@changeSet,1,'1.0',@eventPayload::jsonb,'queued');"; Add(finish, "changeSet", changeSetId); Add(finish, "project", projectId); Add(finish, "import", importId); Add(finish, "event", Guid.NewGuid()); Add(finish, "eventPayload", JsonSerializer.Serialize(new { importId, changeSetId, packageId = package.PackageId, candidateCount = selected.Count }, Json), NpgsqlDbType.Jsonb); await finish.ExecuteNonQueryAsync(ct);
        var result = new ChangeSetView(changeSetId, state.BaseBaseline, $"Import: {package.PackageId}", "draft", 1); await RecordIdempotencyAsync(tx, projectId, actor.PrincipalId, "stageImport", key, digest, result, requestId, traceId, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<ImportBatchView> DiscardAsync(AtlasPrincipal actor, Guid projectId, Guid importId, string reason, long expected, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); if (string.IsNullOrWhiteSpace(reason)) throw ProjectIdentityException.Invalid("A discard reason is required."); var digest = Hash(JsonSerializer.Serialize(new { expected, reason }, Json));
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); await SetContextAsync(tx, projectId, actor.PrincipalId, ct); await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, "discardImport", key, digest, ct); if (replay is not null) { await tx.CommitAsync(ct); return replay.Value.Deserialize<ImportBatchView>(Json)!; }
        var state = await LoadBatchStateAsync(tx, projectId, importId, true, ct); if (state.Version != expected) throw ProjectIdentityException.CasMismatch(); if (state.State == "staged") throw ProjectIdentityException.Conflict("A staged import is draft history; discard its unpublished change set instead of deleting import history.");
        await using var update = tx.CreateCommand(); update.CommandText = "UPDATE intake.import_batch SET state='discarded',summary=summary||@reason::jsonb,version=version+1 WHERE project_id=@project AND import_id=@import"; Add(update, "reason", JsonSerializer.Serialize(new { discardReason = reason }, Json), NpgsqlDbType.Jsonb); Add(update, "project", projectId); Add(update, "import", importId); await update.ExecuteNonQueryAsync(ct);
        var result = await ReadBatchAsync(tx, projectId, importId, ct); await RecordIdempotencyAsync(tx, projectId, actor.PrincipalId, "discardImport", key, digest, result, requestId, traceId, ct); await tx.CommitAsync(ct); return result;
    }

    private async Task<string> DiffAsync(NpgsqlTransaction tx, Guid project, Guid baseline, string ns, NormalizedEntry entry, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT u.unit_id,r.revision_id,r.content_sha256,u.kind FROM core.external_identity x JOIN core.unit u ON u.project_id=x.project_id AND u.unit_id=x.unit_id JOIN gov.baseline_entry b ON b.project_id=@project AND b.baseline_id=@baseline AND b.unit_id=x.unit_id JOIN core.revision r ON r.project_id=b.project_id AND r.unit_id=b.unit_id AND r.revision_id=b.revision_id WHERE x.project_id=@project AND x.source_namespace=@namespace AND x.external_type=@type AND x.external_id=@external"; Add(command, "project", project); Add(command, "baseline", baseline); Add(command, "namespace", ns); Add(command, "type", entry.ExternalType); Add(command, "external", entry.ExternalId);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return JsonSerializer.Serialize(new { status = "new", summary = "New candidate", unitId = (Guid?)null, baseRevisionId = (Guid?)null, normalized = entry }, Json);
        var unit = reader.GetGuid(0); var revision = reader.GetGuid(1); var hash = reader.GetString(2); var kind = reader.GetString(3);
        var status = kind != entry.Kind ? "conflict" : hash == EntryHash(entry) ? "duplicate" : "updated";
        return JsonSerializer.Serialize(new { status, summary = status switch { "duplicate" => "Content-equivalent existing revision", "updated" => "Candidate revision of an existing identity", _ => "Existing identity has a conflicting kind" }, unitId = unit, baseRevisionId = revision, normalized = entry }, Json);
    }
    private async Task<NormalizedPackage> ReadPackageAsync(NpgsqlTransaction tx, Guid project, Guid blobId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT b.storage_key,COALESCE(i.summary->>'originalName','akp.json') FROM intake.blob b JOIN intake.import_batch i ON i.project_id=b.project_id AND i.blob_id=b.blob_id WHERE b.project_id=@project AND b.blob_id=@blob AND b.scan_status='clean'"; Add(command, "project", project); Add(command, "blob", blobId); string key; string name; await using (var reader = await command.ExecuteReaderAsync(ct)) { if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); key = reader.GetString(0); name = reader.GetString(1); } await using var stream = await blobs.OpenReadAsync(project, key, ct); return await AkpPackageReader.ReadAsync(stream, name, ct); }
    private async Task InsertEntryAsync(NpgsqlTransaction tx, Guid project, Guid import, NormalizedEntry entry, string diff, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO intake.import_entry(project_id,entry_id,import_id,external_type,external_id,unit_id,diff) VALUES(@project,@entry,@import,@type,@external,@unit,@diff::jsonb)"; Add(command, "project", project); Add(command, "entry", Guid.NewGuid()); Add(command, "import", import); Add(command, "type", entry.ExternalType); Add(command, "external", entry.ExternalId); Add(command, "unit", (object?)FindUnit(diff) ?? DBNull.Value); Add(command, "diff", diff, NpgsqlDbType.Jsonb); await command.ExecuteNonQueryAsync(ct); }
    private async Task<ImportBatchView> ReadBatchAsync(NpgsqlTransaction tx, Guid project, Guid import, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT package_id,base_baseline_id,state,version,change_set_id,summary FROM intake.import_batch WHERE project_id=@project AND import_id=@import"; Add(command, "project", project); Add(command, "import", import); string package; Guid baseline; string state; long version; Guid? change; JsonElement summary;
        await using (var reader = await command.ExecuteReaderAsync(ct)) { if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); package = reader.GetString(0); baseline = reader.GetGuid(1); state = reader.GetString(2); version = reader.GetInt64(3); change = reader.IsDBNull(4) ? null : reader.GetGuid(4); summary = JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(); }
        var entries = new List<ImportEntryView>(); await using var items = tx.CreateCommand(); items.CommandText = "SELECT entry_id,external_id,external_type,diff,decision,unit_id FROM intake.import_entry WHERE project_id=@project AND import_id=@import ORDER BY external_type,external_id"; Add(items, "project", project); Add(items, "import", import); await using (var reader = await items.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) { var diff = JsonDocument.Parse(reader.GetString(3)).RootElement; entries.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), diff.GetProperty("status").GetString()!, diff.GetProperty("summary").GetString()!, reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5))); }
        var unknowns = summary.TryGetProperty("unknowns", out var array) ? array.Deserialize<List<ImportUnknown>>(Json) ?? [] : []; return new(import, package, baseline, state, version, entries, change, unknowns);
    }
    private sealed record BatchState(Guid BaseBaseline, Guid CurrentBaseline, Guid BlobId, long Version, string State);
    private static async Task<BatchState> LoadBatchStateAsync(NpgsqlTransaction tx, Guid project, Guid import, bool write, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT i.base_baseline_id,p.current_baseline_id,i.blob_id,i.version,i.state FROM intake.import_batch i JOIN identity.project p ON p.project_id=i.project_id WHERE i.project_id=@project AND i.import_id=@import" + (write ? " FOR UPDATE OF i" : ""); Add(command, "project", project); Add(command, "import", import); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound(); return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3), reader.GetString(4)); }
    private sealed record StoredEntry(Guid EntryId, string ExternalType, string ExternalId, JsonElement Diff, string? Decision, Guid? UnitId);
    private static async Task<List<StoredEntry>> ReadStoredEntriesAsync(NpgsqlTransaction tx, Guid project, Guid import, CancellationToken ct)
    { var result = new List<StoredEntry>(); await using var command = tx.CreateCommand(); command.CommandText = "SELECT entry_id,external_type,external_id,diff,decision,unit_id FROM intake.import_entry WHERE project_id=@project AND import_id=@import FOR UPDATE"; Add(command, "project", project); Add(command, "import", import); await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5))); return result; }
    private static JsonElement EvidenceFor(NormalizedEntry entry, IReadOnlyList<NormalizedEntry> all, Dictionary<string, (Guid RevisionId, Guid Unit)> revisions, List<StoredEntry> stored)
    {
        var list = new List<object>(); foreach (var id in entry.SourceIds)
        { var source = all.Single(e => e.ExternalType == "Source" && e.ExternalId == id); var key = Key("Source", id); if (revisions.TryGetValue(key, out var revision)) list.Add(new { source = new { unitId = revision.Unit, revisionId = revision.RevisionId }, locator = new { kind = "importedSource", sourceId = id }, support = "supports", captureKind = "reconstructed" }); else { var existing = stored.Single(e => e.ExternalType == "Source" && e.ExternalId == id); if (existing.UnitId.HasValue && FindUnit(existing.Diff) is not null) list.Add(new { source = new { unitId = existing.UnitId, revisionId = ReadGuid(existing.Diff, "baseRevisionId") }, locator = new { kind = "importedSource", sourceId = id }, support = "supports", captureKind = "reconstructed" }); else throw ProjectIdentityException.Conflict($"Required source '{id}' was rejected or is not visible."); } } return JsonSerializer.SerializeToElement(list);
    }
    private static JsonElement ResolveExternalReferences(JsonElement body, Dictionary<string, (Guid RevisionId, Guid Unit)> revisions, List<StoredEntry> stored)
    {
        using var doc = JsonDocument.Parse(body.GetRawText()); var root = (Dictionary<string, object?>)ToObject(doc.RootElement)!;
        if (root.TryGetValue("subjectExternalId", out var subject)) root["subject"] = ResolveRef("AssertionSubject", subject?.ToString(), revisions, stored);
        if (root.TryGetValue("objectExternalId", out var obj) && obj is not null) root["object"] = ResolveRef("AssertionObject", obj.ToString(), revisions, stored);
        ResolveStructureReferences(root, revisions, stored);
        return JsonSerializer.SerializeToElement(root);
    }
    private static void ResolveStructureReferences(Dictionary<string, object?> value, Dictionary<string, (Guid RevisionId, Guid Unit)> revisions, List<StoredEntry> stored)
    {
        foreach (var key in value.Keys.ToArray())
        {
            if (value[key] is Dictionary<string, object?> child) ResolveStructureReferences(child, revisions, stored);
            else if (value[key] is object?[] items) foreach (var item in items) if (item is Dictionary<string, object?> nested) ResolveStructureReferences(nested, revisions, stored);
            if (value[key] is string external && key is "scenarioId" or "actionId" or "ruleId" or "eventId" or "objectId" or "actionRef")
            {
                var targetKey = key.EndsWith("Id", StringComparison.Ordinal) ? key[..^2] : key[..^3];
                value[targetKey + "Ref"] = ResolveRef(key, external, revisions, stored);
            }
            if (key == "relatedExternalIds" && value[key] is object?[] related)
                value["related"] = related.Select(item => ResolveRef(key, item?.ToString(), revisions, stored)).ToArray();
        }
    }
    private static object ResolveRef(string type, string? id, Dictionary<string, (Guid RevisionId, Guid Unit)> revisions, List<StoredEntry> stored)
    { var match = revisions.FirstOrDefault(p => p.Value.Unit != Guid.Empty && p.Key.EndsWith(":" + id, StringComparison.Ordinal) && !p.Key.StartsWith("Source:", StringComparison.Ordinal)); if (match.Key is not null) return new { unitId = match.Value.Unit, revisionId = match.Value.RevisionId }; var existing = stored.FirstOrDefault(e => e.ExternalId == id && e.ExternalType != "Source"); if (existing?.UnitId is not null) return new { unitId = existing.UnitId, revisionId = ReadGuid(existing.Diff, "baseRevisionId") }; throw ProjectIdentityException.Conflict($"Unresolved imported reference '{id}'."); }
    private static object? ToObject(JsonElement value) => value.ValueKind switch { JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)), JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(), JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetDecimal(), JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static async Task InsertRevisionAsync(NpgsqlTransaction tx, Guid project, Guid unitId, Guid stableUnit, Guid revision, long revNo, Guid? parent, RevisionInput input, Guid actor, string hash, CancellationToken ct)
    {
        if (revNo == 1) { await using var unit = tx.CreateCommand(); unit.CommandText = "INSERT INTO core.unit(project_id,unit_id,kind,owner_id,visibility,created_by) VALUES(@project,@unit,@kind,@actor,0,@actor)"; Add(unit, "project", project); Add(unit, "unit", stableUnit); Add(unit, "kind", input.Kind); Add(unit, "actor", actor); await unit.ExecuteNonQueryAsync(ct); }
        await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,parent_revision_id,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by) VALUES(@project,@revision,@unit,@revNo,@parent,@kind,'1.0',@fact,@verification,@payload::jsonb,@applicability::jsonb,@hash,false,@actor)"; Add(command, "project", project); Add(command, "revision", revision); Add(command, "unit", stableUnit); Add(command, "revNo", revNo); Add(command, "parent", (object?)parent ?? DBNull.Value); Add(command, "kind", input.Kind); Add(command, "fact", input.FactKind); Add(command, "verification", input.Verification); Add(command, "payload", Payload(input), NpgsqlDbType.Jsonb); Add(command, "applicability", input.Applicability.GetRawText(), NpgsqlDbType.Jsonb); Add(command, "hash", hash); Add(command, "actor", actor); await command.ExecuteNonQueryAsync(ct);
        if (input.ExternalId is { } external) { await using var map = tx.CreateCommand(); map.CommandText = "INSERT INTO core.external_identity(project_id,mapping_id,source_namespace,external_type,external_id,unit_id) VALUES(@project,@mapping,@namespace,@type,@external,@unit)"; Add(map, "project", project); Add(map, "mapping", Guid.NewGuid()); Add(map, "namespace", external.Namespace); Add(map, "type", external.ExternalType); Add(map, "external", external.Id); Add(map, "unit", stableUnit); await map.ExecuteNonQueryAsync(ct); }
    }
    private static async Task InsertReferencesAsync(NpgsqlTransaction tx, Guid project, Guid sourceRevision, JsonElement body, CancellationToken ct)
    { var refs = new Dictionary<string, (Guid Unit, Guid Revision)>(); Collect(body, "$.body", refs); foreach (var reference in refs) { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO core.revision_ref(project_id,ref_id,source_revision_id,ref_key,ref_kind,target_unit_id,target_revision_id,required) VALUES(@project,@id,@source,@key,'semantic',@unit,@revision,true)"; Add(command, "project", project); Add(command, "id", Guid.NewGuid()); Add(command, "source", sourceRevision); Add(command, "key", reference.Key); Add(command, "unit", reference.Value.Unit); Add(command, "revision", reference.Value.Revision); await command.ExecuteNonQueryAsync(ct); } }
    private static void Collect(JsonElement value, string path, Dictionary<string, (Guid, Guid)> refs)
    { if (value.ValueKind == JsonValueKind.Object) { if (value.TryGetProperty("unitId", out var unit) && value.TryGetProperty("revisionId", out var revision) && Guid.TryParse(unit.GetString(), out var u) && Guid.TryParse(revision.GetString(), out var r)) refs[path] = (u, r); else foreach (var p in value.EnumerateObject()) Collect(p.Value, path + "." + p.Name, refs); } else if (value.ValueKind == JsonValueKind.Array) { var i = 0; foreach (var item in value.EnumerateArray()) Collect(item, path + "[" + i++ + "]", refs); } }
    private static async Task InsertEvidenceAsync(NpgsqlTransaction tx, Guid project, Guid revision, JsonElement evidence, Guid actor, CancellationToken ct)
    { foreach (var item in evidence.EnumerateArray()) { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO core.evidence_link(project_id,evidence_id,fact_revision_id,source_revision_id,locator,support,capture_kind,created_by) VALUES(@project,@id,@revision,@source,@locator::jsonb,@support,@capture,@actor)"; Add(command, "project", project); Add(command, "id", Guid.NewGuid()); Add(command, "revision", revision); Add(command, "source", item.GetProperty("source").GetProperty("revisionId").GetGuid()); Add(command, "locator", item.GetProperty("locator").GetRawText(), NpgsqlDbType.Jsonb); Add(command, "support", "supports"); Add(command, "capture", "reconstructed"); Add(command, "actor", actor); await command.ExecuteNonQueryAsync(ct); } }
    private static async Task AddChangeItemAsync(NpgsqlTransaction tx, Guid project, Guid changeSet, Guid unit, Guid? parent, Guid revision, string action, string reason, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO gov.change_item(project_id,item_id,change_set_id,unit_id,base_revision_id,proposed_revision_id,action,dependency_group,reason) VALUES(@project,@item,@changeSet,@unit,@parent,@revision,@action,'import',@reason)"; Add(command, "project", project); Add(command, "item", Guid.NewGuid()); Add(command, "changeSet", changeSet); Add(command, "unit", unit); Add(command, "parent", (object?)parent ?? DBNull.Value); Add(command, "revision", revision); Add(command, "action", action); Add(command, "reason", reason); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<Guid> InsertBlobAsync(NpgsqlTransaction tx, Guid project, Guid id, StoredBlob stored, string scan, Guid actor, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO intake.blob(project_id,blob_id,sha256,byte_size,storage_key,scan_status,created_by) VALUES(@project,@id,@hash,@size,@key,@scan,@actor) ON CONFLICT(project_id,sha256) DO UPDATE SET sha256=EXCLUDED.sha256 RETURNING blob_id"; Add(command, "project", project); Add(command, "id", id); Add(command, "hash", stored.Sha256); Add(command, "size", stored.ByteSize); Add(command, "key", stored.StorageKey); Add(command, "scan", scan); Add(command, "actor", actor); return (Guid)(await command.ExecuteScalarAsync(ct))!; }
    private static async Task LinkSourceAsync(NpgsqlTransaction tx, Guid project, Guid revision, Guid blob, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO intake.source_blob_link(project_id,source_revision_id,blob_id) VALUES(@project,@revision,@blob) ON CONFLICT DO NOTHING"; Add(command, "project", project); Add(command, "revision", revision); Add(command, "blob", blob); await command.ExecuteNonQueryAsync(ct); }
    private static async Task InsertChangeSetAsync(NpgsqlTransaction tx, Guid project, Guid id, string title, Guid actor, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,created_by) SELECT @project,@id,current_baseline_id,@title,'draft',@actor FROM identity.project WHERE project_id=@project"; Add(command, "project", project); Add(command, "id", id); Add(command, "title", title); Add(command, "actor", actor); if (await command.ExecuteNonQueryAsync(ct) != 1) throw ProjectIdentityException.NotFound(); }
    private static async Task RequireOpenChangeSetAsync(NpgsqlTransaction tx, Guid project, Guid id, Guid baseline, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM gov.change_set WHERE project_id=@project AND change_set_id=@id AND base_baseline_id=@baseline AND state IN('draft','changes_requested'))"; Add(command, "project", project); Add(command, "id", id); Add(command, "baseline", baseline); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("The target change set is not an editable draft on the import baseline."); }
    private static async Task<long> NextRevisionAsync(NpgsqlTransaction tx, Guid project, Guid unit, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT max(rev_no)+1 FROM core.revision WHERE project_id=@project AND unit_id=@unit"; Add(command, "project", project); Add(command, "unit", unit); return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }
    private static async Task RequireCurrentBaselineAsync(NpgsqlTransaction tx, Guid project, Guid baseline, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT current_baseline_id=@baseline FROM identity.project WHERE project_id=@project"; Add(command, "project", project); Add(command, "baseline", baseline); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Conflict("baseBaselineId must be the project's current baseline."); }
    private static async Task RequireProposerAsync(NpgsqlTransaction tx, Guid project, Guid actor, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM identity.membership WHERE project_id=@project AND principal_id=@actor AND active AND roles&&ARRAY['Contributor','AgentProposer','ProjectAdmin']::text[])"; Add(command, "project", project); Add(command, "actor", actor); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.NotFound(); }
    private static async Task RecordIdempotencyAsync<T>(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, T result, string request, string? trace, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@actor,@operation,@key,@digest,@result::jsonb); INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@actor,@operation,@result::jsonb,'OK',@request,@trace)"; Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key); Add(command, "digest", digest); Add(command, "result", JsonSerializer.Serialize(result, Json), NpgsqlDbType.Jsonb); Add(command, "request", request); Add(command, "trace", (object?)trace ?? DBNull.Value); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<JsonElement?> FindIdempotencyAsync(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@actor AND operation_id=@operation AND idempotency_key=@key"; Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null; if (reader.GetString(0) != digest) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request."); return JsonDocument.Parse(reader.GetString(1)).RootElement.Clone(); }
    private static string EntryHash(NormalizedEntry entry) => CanonicalJson.Sha256(JsonSerializer.SerializeToElement(new { entry.ExternalType, entry.ExternalId, entry.Title, entry.Kind, entry.FactKind, entry.Verification, applicability = entry.Applicability, body = entry.Body, categoryKeys = entry.CategoryKeys }, Json));
    private static string Payload(RevisionInput input) => CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new { title = input.Title, applicability = input.Applicability, body = input.Body, blocks = input.Blocks, evidence = input.Evidence, unknowns = input.Unknowns, categoryKeys = input.CategoryKeys }, Json));
    private static string Key(string type, string id) => type + ":" + id;
    private static Guid? FindUnit(JsonElement diff) => ReadGuid(diff, "unitId");
    private static Guid? FindUnit(string diff) { using var doc = JsonDocument.Parse(diff); return ReadGuid(doc.RootElement, "unitId"); }
    private static Guid? ReadGuid(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id) ? id : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateFile(string name, string type, string kind, long max)
    { if (string.IsNullOrWhiteSpace(name) || name.Length > 500 || string.IsNullOrWhiteSpace(type) || type.Length > 200 || string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || max <= 0) throw ProjectIdentityException.Invalid("A bounded file name, content type, and sourceKind are required."); }
    private static string Encode(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? Decode(string? cursor) { if (string.IsNullOrEmpty(cursor)) return null; try { return new Guid(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4))); } catch { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }
    private static JsonElement Obj<T>(T value) => JsonSerializer.SerializeToElement(value); private static JsonElement Arr() => JsonDocument.Parse("[]").RootElement.Clone();
    private static void Add(NpgsqlCommand command, string name, object value, NpgsqlDbType? type = null) { var parameter = command.Parameters.AddWithValue(name, value); if (type.HasValue) parameter.NpgsqlDbType = type.Value; }
    private static async Task SetContextAsync(NpgsqlTransaction tx, Guid project, Guid principal, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true)"; Add(command, "project", project.ToString()); Add(command, "principal", principal.ToString()); await command.ExecuteNonQueryAsync(ct); }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
}
