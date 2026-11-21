using System.Text.Json;
using ImportEvidence.Contracts;
using ProjectIdentity.Application;

namespace ImportEvidence.Sources;

/// <summary>Native T1 candidate input. It carries revisions and references, not approvals.</summary>
internal static class AtlasProposalReader
{
    public const string FormatVersion = "AtlasProposal/1.0";
    private static readonly HashSet<string> FactKinds = ["observed", "documented_intent", "inferred", "assumption"];

    public static NormalizedPackage Normalize(JsonElement root)
    {
        var packageId = Text(root, "packageId", true)!;
        var sourceNamespace = Text(root, "sourceNamespace", true)!;
        if (packageId.Length > 200 || sourceNamespace.Length > 500) throw ProjectIdentityException.Invalid("Native package identity is too long.");
        if (!root.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array)
            throw ProjectIdentityException.Invalid("AtlasProposal/1.0 requires an entries array.");

        var entries = new List<NormalizedEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("Every native proposal entry must be an object.");
            var externalId = Text(item, "externalId", true)!;
            var externalType = Text(item, "externalType", true)!;
            var title = Text(item, "title", true)!;
            var kind = Text(item, "kind", true)!;
            var factKind = Text(item, "factKind", true)!;
            var verification = Text(item, "verification", true)!;
            if (!FactKinds.Contains(factKind) || verification is not ("unverified" or "contradicted"))
                throw ProjectIdentityException.Invalid($"Native entry '{externalId}' has an unsupported fact or verification level; imported candidates cannot arrive approved.");
            var applicability = Object(item, "applicability");
            var body = Object(item, "body");
            var categories = Strings(item, "categoryKeys");
            var sources = Strings(item, "sourceIds");
            entries.Add(new(externalType, externalId, title, kind, factKind, verification, applicability, body,
                categories, item.Clone(), AsStrings(sources)));
        }
        if (entries.GroupBy(e => (e.ExternalType, e.ExternalId)).Any(g => g.Count() > 1))
            throw ProjectIdentityException.Invalid("Native proposal external identities must be unique within their type.");

        var knownSources = entries.Where(e => e.ExternalType == "Source").Select(e => e.ExternalId).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var source in entry.SourceIds)
                if (!knownSources.Contains(source))
                    throw ProjectIdentityException.Invalid($"Native entry '{entry.ExternalId}' references missing source '{source}'.");

        var unknowns = new List<ImportUnknown>();
        if (root.TryGetProperty("unknowns", out var unknownArray))
        {
            if (unknownArray.ValueKind != JsonValueKind.Array) throw ProjectIdentityException.Invalid("Native unknowns must be an array.");
            foreach (var item in unknownArray.EnumerateArray())
                unknowns.Add(new(Text(item, "code", true)!, Text(item, "message", true)!, Text(item, "externalId", false)));
        }
        return new(packageId, FormatVersion, sourceNamespace, entries, unknowns);
    }

    private static JsonElement Object(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var result) || result.ValueKind != JsonValueKind.Object)
            throw ProjectIdentityException.Invalid($"Native property '{name}' must be an object.");
        return result.Clone();
    }

    private static JsonElement Strings(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var result)) return JsonSerializer.SerializeToElement(Array.Empty<string>());
        if (result.ValueKind != JsonValueKind.Array || result.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            throw ProjectIdentityException.Invalid($"Native property '{name}' must be an array of strings.");
        return result.Clone();
    }

    private static IReadOnlyList<string> AsStrings(JsonElement value) => value.EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static string? Text(JsonElement value, string name, bool required)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 20000) return text;
        }
        if (required) throw ProjectIdentityException.Invalid($"Native property '{name}' is required and bounded.");
        return null;
    }
}
