using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using PlatformOps.Jobs;
using ProjectIdentity.Application;
using ReadSearchExplain.Contracts;
using ReadSearchExplain.Search;

namespace ReadSearchExplain.Export;

public sealed class PostgresExportService(
    string connectionString,
    PostgresKnowledgeStore knowledge,
    PostgresAccessApplicabilityStore access,
    PostgresReadSearchService search,
    IJobQueue jobs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JobInfo> CreateAsync(ResolvedReadContext context, CreateExportRequest request, string key, CancellationToken ct)
    {
        if (key.Length is < 8 or > 128 || request.RootUnitIds.Count is 0 or > 200 || request.RootUnitIds.Distinct().Count() != request.RootUnitIds.Count)
            throw ProjectIdentityException.Invalid("An idempotency key and 1-200 unique root units are required.");
        if (request.Format is not ("markdown" or "json" or "akp" or "csv")) throw ProjectIdentityException.Invalid("The export format is invalid.");
        var exportContext = context with { Purpose = ReadPurpose.Export };
        foreach (var id in request.RootUnitIds) await access.AuthorizeReadAsync(exportContext, id, "export", null, ct);
        var existing = await FindIdempotencyAsync(exportContext, key, ct);
        if (existing.HasValue) return await jobs.GetAsync(context.ProjectId, context.PrincipalId, existing.Value, ct);
        var job = await jobs.EnqueueAsync(context.ProjectId, context.PrincipalId, new("knowledge.export", JsonSerializer.SerializeToElement(new { request.ReadContext, request.Format, request.RootUnitIds }, Json)), cancellationToken: ct);
        await RecordIdempotencyAsync(exportContext, key, job.JobId, ct);
        return job;
    }

    public async Task<ExportArtifact> GenerateAsync(Guid projectId, Guid principalId, JsonElement input, CancellationToken ct)
    {
        var request = input.Deserialize<CreateExportRequest>(Json) ?? throw new JobExecutionException("INVALID_EXPORT", "Export input is invalid.");
        var read = input.GetProperty("readContext").Deserialize<ReadContextRequest>(Json)!;
        var format = input.GetProperty("format").GetString()!;
        var ids = input.GetProperty("rootUnitIds").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        var context = await access.CreateReadContextAsync(principalId, projectId, read with { Purpose = ReadPurpose.Export }, ct);
        var revisions = new List<RevisionView>();
        foreach (var id in ids)
        {
            await access.AuthorizeReadAsync(context, id, "export", null, ct);
            var root = await search.ProjectAsync(context, await knowledge.GetUnitAsync(context, id, ct), ct);
            revisions.Add(root);
            var relations = await knowledge.ListRelationsAsync(context, id, "both", null, null, 200, ct);
            foreach (var relation in relations.Items) revisions.Add(await search.ProjectAsync(context, relation, ct));
        }
        revisions = revisions.GroupBy(item => item.RevisionId).Select(group => group.First()).ToList();
        var uploadId = Guid.NewGuid(); var name = $"atlas-export-{uploadId:N}.{Extension(format)}";
        var bytes = Serialize(format, revisions, context, uploadId);
        var artifact = new ExportArtifact(uploadId, name, ContentType(format), bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), revisions.Select(item => new UnitRef(item.UnitId, item.RevisionId)).ToArray(), context);
        await SaveAsync(artifact, ct);
        return artifact;
    }

    public async Task<ExportArtifact> DownloadAsync(Guid principalId, Guid projectId, Guid uploadId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var set = tx.CreateCommand(); set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        set.Parameters.AddWithValue("project", projectId.ToString()); set.Parameters.AddWithValue("principal", principalId.ToString()); await set.ExecuteNonQueryAsync(ct);
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT file_name,content_type,content,sha256,included,context FROM read_model.export WHERE project_id=@project AND upload_id=@id AND requester_id=@principal;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", uploadId); command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var name = reader.GetString(0); var contentType = reader.GetString(1); var content = reader.GetFieldValue<byte[]>(2); var hash = reader.GetString(3);
        var included = JsonSerializer.Deserialize<UnitRef[]>(reader.GetString(4), Json)!; var context = JsonSerializer.Deserialize<ResolvedReadContext>(reader.GetString(5), Json)!;
        await reader.CloseAsync();
        foreach (var item in included)
        {
            await access.AuthorizeReadAsync(context, item.UnitId, "export", item.RevisionId, ct);
            if (context.Mode == ReadMode.Historical) await access.AuthorizeReadAsync(context, item.UnitId, "read_history", item.RevisionId, ct);
        }
        await access.EnsureCurrentAsync(context, ct);
        return new(uploadId, name, contentType, content, hash, included, context);
    }

    private async Task SaveAsync(ExportArtifact artifact, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var set = tx.CreateCommand(); set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        set.Parameters.AddWithValue("project", artifact.Context.ProjectId.ToString()); set.Parameters.AddWithValue("principal", artifact.Context.PrincipalId.ToString()); await set.ExecuteNonQueryAsync(ct);
        await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO read_model.export(project_id,upload_id,requester_id,file_name,content_type,content,sha256,included,context) VALUES(@project,@id,@principal,@name,@type,@content,@hash,@included::jsonb,@context::jsonb);";
        command.Parameters.AddWithValue("project", artifact.Context.ProjectId); command.Parameters.AddWithValue("id", artifact.UploadId); command.Parameters.AddWithValue("principal", artifact.Context.PrincipalId);
        command.Parameters.AddWithValue("name", artifact.FileName); command.Parameters.AddWithValue("type", artifact.ContentType); command.Parameters.AddWithValue("content", artifact.Content); command.Parameters.AddWithValue("hash", artifact.Sha256);
        command.Parameters.Add(new NpgsqlParameter("included", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(artifact.Included, Json) });
        command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(artifact.Context, Json) }); await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task<Guid?> FindIdempotencyAsync(ResolvedReadContext context, string key, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using var set = tx.CreateCommand(); set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);"; set.Parameters.AddWithValue("project", context.ProjectId.ToString()); set.Parameters.AddWithValue("principal", context.PrincipalId.ToString()); await set.ExecuteNonQueryAsync(ct);
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT result_ref->>'jobId' FROM ops.idempotency WHERE project_id=@project AND principal_id=@principal AND operation_id='createExport' AND idempotency_key=@key;"; command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("principal", context.PrincipalId); command.Parameters.AddWithValue("key", key);
        var value = await command.ExecuteScalarAsync(ct) as string; return Guid.TryParse(value, out var id) ? id : null;
    }
    private async Task RecordIdempotencyAsync(ResolvedReadContext context, string key, Guid jobId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using var set = tx.CreateCommand(); set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);"; set.Parameters.AddWithValue("project", context.ProjectId.ToString()); set.Parameters.AddWithValue("principal", context.PrincipalId.ToString()); await set.ExecuteNonQueryAsync(ct);
        await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@principal,'createExport',@key,@digest,@result::jsonb) ON CONFLICT DO NOTHING;"; command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("principal", context.PrincipalId); command.Parameters.AddWithValue("key", key); command.Parameters.AddWithValue("digest", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()); command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { jobId }, Json) }); await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }

    private static byte[] Serialize(string format, IReadOnlyList<RevisionView> revisions, ResolvedReadContext context, Guid id)
    {
        if (format == "json") return JsonSerializer.SerializeToUtf8Bytes(new { exportId = id, baselineId = context.BaselineId, contextHash = context.ContextHash, revisions }, Json);
        if (format == "markdown") return Encoding.UTF8.GetBytes(string.Join("\n\n", revisions.Select(item => $"# {item.Title}\n\n{item.Body.GetRawText()}")));
        if (format == "csv")
        {
            static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            return Encoding.UTF8.GetBytes("unitId,revisionId,kind,title\n" + string.Join("\n", revisions.Select(item => string.Join(',', item.UnitId, item.RevisionId, Cell(item.Kind), Cell(item.Title)))));
        }
        using var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var manifest = zip.CreateEntry("manifest.json"); using var writer = new StreamWriter(manifest.Open()); writer.Write(JsonSerializer.Serialize(new { format = "akp", version = "0.1", exportId = id, baselineId = context.BaselineId, units = revisions.Select(item => new UnitRef(item.UnitId, item.RevisionId)) }, Json));
            foreach (var revision in revisions) { var entry = zip.CreateEntry($"revisions/{revision.RevisionId:N}.json"); using var content = new StreamWriter(entry.Open()); content.Write(JsonSerializer.Serialize(revision, Json)); }
        }
        return stream.ToArray();
    }
    private static string Extension(string format) => format == "markdown" ? "md" : format;
    private static string ContentType(string format) => format switch { "json" => "application/json", "markdown" => "text/markdown; charset=utf-8", "csv" => "text/csv; charset=utf-8", _ => "application/zip" };
}
