using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeCore.Contracts;
using ProjectIdentity.Application;

namespace KnowledgeCore.Vocabulary;

/// <summary>
/// Domain language remains independent of technical field names. Search normalization never
/// rewrites the approved business text; renaming a term retains its stable unit identity and
/// records the former display name as an alias in the new revision.
/// </summary>
public static class VocabularySemantics
{
    private static readonly HashSet<string> VocabularyKinds =
        ["DomainTerm", "DomainConcept", "BusinessObject", "BusinessAttribute"];

    public static bool IsVocabulary(string kind) => VocabularyKinds.Contains(kind);

    public static string NormalizeForLookup(string value) =>
        value.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();

    public static void Validate(RevisionInput input)
    {
        if (!IsVocabulary(input.Kind)) return;
        OptionalText(input.Body, "definition");
        OptionalText(input.Body, "contextKey");
        OptionalText(input.Body, "example", false);
        OptionalText(input.Body, "counterexample", false);
        OptionalText(input.Body, "unit", false);
        OptionalText(input.Body, "valueDomain", false);
        OptionalText(input.Body, "businessSemantics", false);
        if (input.Body.TryGetProperty("aliases", out var aliases))
        {
            if (aliases.ValueKind != JsonValueKind.Array)
                throw ProjectIdentityException.Invalid("Vocabulary aliases must be an array of non-empty names.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(alias.GetString()) || alias.GetString()!.Length > 500)
                    throw ProjectIdentityException.Invalid("Vocabulary aliases must be non-empty names of at most 500 characters.");
                if (!unique.Add(NormalizeForLookup(alias.GetString()!)))
                    throw ProjectIdentityException.Invalid("Vocabulary aliases must be unique after Unicode lookup normalization.");
            }
        }
    }

    public static RevisionInput RetainFormerNameAsAlias(RevisionInput input, RevisionView previous)
    {
        if (!IsVocabulary(input.Kind) || NormalizeForLookup(previous.Title) == NormalizeForLookup(input.Title)) return input;
        var body = JsonNode.Parse(input.Body.GetRawText())?.AsObject()
            ?? throw ProjectIdentityException.Invalid("Vocabulary body must be an object.");
        var aliases = body["aliases"] as JsonArray ?? new JsonArray();
        var names = aliases.Select(node => node?.GetValue<string>()).Where(value => value is not null).Cast<string>().ToList();
        if (!names.Contains(previous.Title) && names.All(alias => NormalizeForLookup(alias) != NormalizeForLookup(previous.Title)))
            aliases.Add(previous.Title);
        body["aliases"] = aliases;
        return input with { Body = JsonSerializer.SerializeToElement(body) };
    }

    private static void OptionalText(JsonElement body, string name, bool nonEmpty = true)
    {
        if (!body.TryGetProperty(name, out var value)) return;
        if (value.ValueKind != JsonValueKind.String || nonEmpty && string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()?.Length > 16000)
            throw ProjectIdentityException.Invalid($"Vocabulary body.{name} must be {(nonEmpty ? "a non-empty" : "a")} string of at most 16000 characters.");
    }
}
