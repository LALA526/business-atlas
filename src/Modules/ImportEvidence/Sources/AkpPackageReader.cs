using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImportEvidence.Contracts;
using KnowledgeCore.Application;
using ProjectIdentity.Application;

namespace ImportEvidence.Sources;

public static class AkpPackageReader
{
    private static readonly HashSet<string> Kinds = ["ProjectProfile","DomainTerm","DomainConcept","BusinessObject","BusinessAttribute","Requirement","BusinessRule","Rule","Flow","Transition","Assertion","Design","Component","Api","API","Implementation","Observation","BusinessCase","Source","DataField","Domain","Capability","Feature","Actor","Action","Event","Behavior","State","Constraint","Decision","System","Module","DataEntity","Operation","Environment","Deployment","Test","OperationalKnowledge","EngineeringStandard","QualityRequirement","Document","Metric","Milestone","Permission","Risk","TechnicalDebt","Configuration","Exception","Compensation","Scenario"];
    public static async Task<NormalizedPackage> ReadAsync(Stream stream, string fileName, CancellationToken ct)
    {
        JsonDocument document;
        if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase) || stream.ReadByte() == 0x50)
        { stream.Position = 0; document = await ReadZipAsync(stream, ct); }
        else { stream.Position = 0; document = await ParseJsonAsync(stream, ct); }
        using (document) return Normalize(document.RootElement);
    }

    private static async Task<JsonDocument> ParseJsonAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var counted = new CountingStream(stream);
            var doc = await JsonDocument.ParseAsync(counted, new JsonDocumentOptions { MaxDepth = 48, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }, ct);
            if (counted.Fields > 100000) throw ProjectIdentityException.Invalid("The package contains too many JSON fields.");
            return doc;
        }
        catch (JsonException exception) { throw ProjectIdentityException.Invalid("The package is not valid bounded JSON: " + exception.Message); }
    }
    private static async Task<JsonDocument> ReadZipAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > 10000) throw ProjectIdentityException.Invalid("The ZIP contains too many entries.");
            long total = 0; ZipArchiveEntry? selected = null;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Any(part => part == "..") || name.Contains('\0') || (entry.ExternalAttributes & 0xA0000000) == 0xA0000000)
                    throw ProjectIdentityException.Invalid("ZIP path traversal and symbolic links are not accepted.");
                if (name.EndsWith('/')) continue;
                if (Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".gz" or ".tar" or ".7z" or ".rar") throw ProjectIdentityException.Invalid("Nested archives are not accepted.");
                if (Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".dll" or ".com" or ".bat" or ".cmd" or ".sh" or ".ps1" or ".so" or ".dylib") throw ProjectIdentityException.Invalid("Executable ZIP entries are not accepted.");
                total = checked(total + entry.Length);
                if (total > 500 * 1024 * 1024 || entry.Length > 50 * 1024 * 1024 || entry.Length > 0 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 100)
                    throw ProjectIdentityException.Invalid("ZIP expansion or compression-ratio limits were exceeded.");
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && (name.Equals("akp.json", StringComparison.OrdinalIgnoreCase) || selected is null)) selected = entry;
            }
            if (selected is null) throw ProjectIdentityException.Invalid("The ZIP does not contain an AKP JSON document.");
            await using var content = selected.Open(); return await ParseJsonAsync(content, ct);
        }
        catch (InvalidDataException exception) { throw ProjectIdentityException.Invalid("The ZIP is invalid: " + exception.Message); }
    }

    private static NormalizedPackage Normalize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("manifest", out var manifest)) throw ProjectIdentityException.Invalid("Only a declared AKP package with a manifest is supported.");
        var version = Required(manifest, "formatVersion");
        if (version != "AKP/0.1") throw ProjectIdentityException.Invalid($"Unsupported package format '{version}'. Use the AKP/0.1 adapter.");
        foreach (var property in new[] { "entities", "assertions", "businessStructures", "technicalStructures", "sources", "unknownsAndConflicts", "importProvenance" })
            if (!root.TryGetProperty(property, out _)) throw ProjectIdentityException.Invalid($"AKP/0.1 requires '{property}'.");
        var packageId = Required(manifest, "packageId"); var sourceProject = Required(manifest, "sourceProjectId");
        if (packageId.Length > 200 || sourceProject.Length > 500 || Property(manifest, "declaredScope").ValueKind != JsonValueKind.Array) throw ProjectIdentityException.Invalid("The manifest identity or declared scope is invalid.");
        var entries = new List<NormalizedEntry>(); var unknowns = new List<ImportUnknown>();
        foreach (var source in Array(root, "sources"))
        {
            var id = Required(source, "sourceId"); var title = Required(source, "path");
            var locator = new { kind = "importedSource", path = title, uri = Optional(source, "uri"), revision = Required(source, "revision"), lineRange = Property(source, "lineRange"), section = Optional(source, "section"), anchor = Optional(source, "anchor"), contentHash = Optional(source, "contentHash") };
            var body = Obj(new { sourceKind = Required(source, "sourceType"), locator, provenance = new { packageId, captureMethod = Required(source, "captureMethod"), excerpt = Optional(source, "excerpt"), importedAs = "privatePackageBlob" } });
            entries.Add(new("Source", id, title, "Source", "observed", "unverified", Property(source, "applicableContext", "{}"), body, Arr(), source.Clone(), []));
        }
        var sourceIds = entries.Select(e => e.ExternalId).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in Array(root, "entities"))
        {
            var id = Required(entity, "stableExternalId"); var kind = MapKind(Required(entity, "typeId"));
            if (!Kinds.Contains(kind)) throw ProjectIdentityException.Invalid($"Unsupported entity type '{kind}'.");
            var refs = Strings(entity, "sourceIds"); ValidateSourceRefs(refs, sourceIds, id);
            var (fact, verification) = Verification(Optional(entity, "verificationStatus"));
            var body = Obj(new { description = Optional(entity, "description"), properties = Property(entity, "properties"), originalTypeId = Required(entity, "typeId"), extra = Property(entity, "extra"), importProvenance = new { packageId, stableExternalId = id } });
            entries.Add(new(kind, id, Required(entity, "name"), kind, fact, verification, Property(entity, "applicableContext"), body, Property(entity, "categoryAssignments", "[]"), entity.Clone(), refs));
        }
        foreach (var assertion in Array(root, "assertions"))
        {
            var id = Required(assertion, "stableExternalId"); var subject = Required(assertion, "subject"); var objectId = Optional(assertion, "object");
            var (fact, verification) = Verification(Optional(assertion, "verificationStatus"));
            var body = Obj(new { subjectExternalId = subject, objectExternalId = objectId, predicate = Required(assertion, "predicate"), value = Property(assertion, "value"), qualifiers = Property(assertion, "qualifiers"), explanation = Optional(assertion, "explanation"), importProvenance = new { packageId, stableExternalId = id } });
            var refs = Array(assertion, "sourceRefs").Select(item => Required(item, "sourceId")).ToArray(); ValidateSourceRefs(refs, sourceIds, id);
            entries.Add(new("Assertion", id, id, "Assertion", fact, verification, Obj(new { }), body, Arr(), assertion.Clone(), refs));
        }
        AddStructures(root, "businessStructures", packageId, entries);
        AddStructures(root, "technicalStructures", packageId, entries);
        foreach (var item in Array(root, "unknownsAndConflicts"))
            unknowns.Add(new("IMPORTED_" + Required(item, "kind").ToUpperInvariant().Replace('-', '_'), Required(item, "title"), Optional(item, "id")));
        if (entries.GroupBy(e => (e.ExternalType, e.ExternalId)).Any(g => g.Count() > 1)) throw ProjectIdentityException.Invalid("stableExternalId values must be unique within their external type.");
        return new(packageId, version, "akp:" + sourceProject, entries, unknowns);
    }
    private static void AddStructures(JsonElement root, string property, string packageId, List<NormalizedEntry> entries)
    {
        if (Property(root, property).ValueKind != JsonValueKind.Object) return;
        foreach (var section in Property(root, property).EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Array) continue;
            var index = 0; foreach (var value in section.Value.EnumerateArray())
            {
                var stable = value.ValueKind == JsonValueKind.Object ? Optional(value, "stableExternalId") : null;
                stable ??= property + ":" + section.Name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index + ":" + value.GetRawText())))[..16].ToLowerInvariant();
                var kind = StructureKind(section.Name); var title = value.ValueKind == JsonValueKind.String ? value.GetString()! : Optional(value, "name") ?? stable;
                var body = Obj(new { importedValue = value, purpose = Optional(value, "purpose"), importProvenance = new { packageId, section = section.Name } });
                entries.Add(new(kind, stable, title, kind, "documented_intent", "unverified", Obj(new { }), body, Arr(), value.Clone(), [])); index++;
            }
        }
    }
    private static string StructureKind(string name) => name switch { "flows" => "Flow", "transitions" => "Transition", "rules" or "constraints" => "BusinessRule", "actions" => "Action", "decisions" => "Decision", "events" => "Event", "systems" => "System", "components" => "Component", "apis" => "Api", "dataEntities" => "DataEntity", "environments" => "Environment", "architectureDecisions" => "Decision", "configurations" => "Configuration", _ => "Document" };
    private static (string Fact, string Verification) Verification(string? value) => value switch { "observed" => ("observed", "unverified"), "documented_intent" => ("documented_intent", "unverified"), "inferred" => ("inferred", "unverified"), "conflicting" => ("assumption", "contradicted"), _ => ("assumption", "unverified") };
    private static string MapKind(string value) => value switch { "Rule" => "BusinessRule", "API" => "Api", _ => value };
    private static void ValidateSourceRefs(IEnumerable<string> refs, HashSet<string> known, string id) { foreach (var source in refs) if (!known.Contains(source)) throw ProjectIdentityException.Invalid($"'{id}' references missing source '{source}'."); }
    private static JsonElement Property(JsonElement value, string name, string? fallback = null) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.Clone() : fallback is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonDocument.Parse(fallback).RootElement.Clone();
    private static string Required(JsonElement value, string name) { var result = Optional(value, name); if (string.IsNullOrWhiteSpace(result) || result.Length > 20000) throw ProjectIdentityException.Invalid($"AKP property '{name}' is required and bounded."); return result; }
    private static string? Optional(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Property(value, name, "[]").ValueKind == JsonValueKind.Array ? Property(value, name).EnumerateArray().Select(item => item.Clone()) : throw ProjectIdentityException.Invalid($"AKP property '{name}' must be an array.");
    private static string[] Strings(JsonElement value, string name) => Array(value, name).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw ProjectIdentityException.Invalid($"'{name}' must contain strings.")).ToArray();
    private static JsonElement Obj<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Arr() => JsonDocument.Parse("[]").RootElement.Clone();
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Fields { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Fields += buffer.Skip(offset).Take(read).Count(b => b == (byte)'"'); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var read = await inner.ReadAsync(buffer, ct); Fields += buffer.Span[..read].ToArray().Count(b => b == (byte)'"'); return read; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
