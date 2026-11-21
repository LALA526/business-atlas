using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using ProjectIdentity.Application;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.Search;

public sealed class ReadingProjectionService(PostgresKnowledgeStore knowledge, PostgresReadSearchService search)
{
    public async Task<ReadingView> BuildAsync(ResolvedReadContext context, Guid rootUnitId, string? profile, string? cursor, CancellationToken ct)
    {
        if (profile is not null && (profile.Length == 0 || profile.Length > 100)) throw ProjectIdentityException.Invalid("The reading profile is invalid.");
        var offset = DecodeOffset(cursor);
        var root = await search.ProjectAsync(context, await knowledge.GetUnitAsync(context, rootUnitId, ct), ct);
        var relations = await knowledge.ListRelationsAsync(context, rootUnitId, "both", null, null, 200, ct);
        var refs = new List<RevisionView> { root };
        refs.AddRange(relations.Items);
        var unknowns = Unknowns(root).ToList();
        var complete = relations.NextCursor is null;
        if (!complete) unknowns.Add(new("reading.truncated", "Additional authorized relations are available on the next page.", "info", "insufficient", "Follow nextCursor."));
        var sections = refs.Skip(offset).Take(50).Select(item => Section(item)).ToList();
        var consumed = Math.Min(offset + sections.Count, refs.Count);
        var next = consumed < refs.Count ? Convert.ToBase64String(BitConverter.GetBytes(consumed)) : null;
        if (next is not null) complete = false;
        return new(new(root.UnitId, root.RevisionId), root.Title, Purpose(context.Purpose), sections, context, unknowns, complete ? "complete" : "partial", next);
    }

    public async Task<ReadingView> OverviewAsync(ResolvedReadContext context, CancellationToken ct)
    {
        var page = await knowledge.ListUnitsAsync(context, null, null, null, 200, ct);
        var items = new List<RevisionView>();
        foreach (var item in page.Items) items.Add(await search.ProjectAsync(context, item, ct));
        var unknowns = items.SelectMany(Unknowns).ToList();
        if (page.NextCursor is not null) unknowns.Add(new("overview.truncated", "The overview is bounded; use search or a reading plan for additional units.", "info", "insufficient"));
        var sections = items.GroupBy(item => item.Kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReadingSection(group.Key, group.Key, group.SelectMany(item => Section(item).Blocks).ToArray())).ToArray();
        return new(null, "Project knowledge overview", Purpose(context.Purpose), sections, context, unknowns, page.NextCursor is null ? "complete" : "partial");
    }

    public async Task<CoverageResult> CoverageAsync(ResolvedReadContext context, Guid rootUnitId, CancellationToken ct)
    {
        var root = await search.ProjectAsync(context, await knowledge.GetUnitAsync(context, rootUnitId, ct), ct);
        var unknowns = Unknowns(root).ToList();
        var checks = new List<Check>
        {
            new("content.purpose", Has(root.Body, "purpose") || root.Blocks.GetArrayLength() > 0 ? "pass" : "fail", true, "A human-readable purpose or content block is required.", []),
            new("content.applicability", root.Applicability.EnumerateObject().Any() ? "pass" : "unknown", true, "Applicability must be explicit before coverage can be considered known.", []),
            new("content.unknowns", unknowns.Count == 0 ? "pass" : "unknown", false, unknowns.Count == 0 ? "No declared essential unknowns." : "Declared unknowns remain open.", [])
        };
        return new(checks, unknowns);
    }

    private static ReadingSection Section(RevisionView revision)
    {
        var blocks = revision.Blocks.ValueKind == JsonValueKind.Array ? revision.Blocks.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
        return new(revision.UnitId.ToString(), revision.Title, blocks);
    }
    private static IEnumerable<Unknown> Unknowns(RevisionView revision)
    {
        if (revision.Unknowns.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in revision.Unknowns.EnumerateArray())
        {
            var key = Text(value, "key") ?? "unknown"; var description = Text(value, "description") ?? "An essential fact is unresolved.";
            yield return new(key, description, Text(value, "severity") ?? "warning", Text(value, "reason") ?? "missing", Text(value, "nextAction"));
        }
    }
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Has(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined && (property.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(property.GetString()));
    private static string Purpose(ReadPurpose purpose) => purpose.ToString().ToLowerInvariant();
    private static int DecodeOffset(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try { var bytes = Convert.FromBase64String(cursor); if (bytes.Length != 4) throw new FormatException(); var value = BitConverter.ToInt32(bytes); if (value < 0) throw new FormatException(); return value; }
        catch (FormatException) { throw ProjectIdentityException.Invalid("The reading cursor is invalid."); }
    }
}
