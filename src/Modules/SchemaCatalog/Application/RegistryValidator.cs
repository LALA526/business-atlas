using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectIdentity.Application;
using SchemaCatalog.Contracts;

namespace SchemaCatalog.Application;

public static partial class RegistryValidator
{
    private static readonly HashSet<string> Dimensions = BuiltInRegistry.Dimensions.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "forward", "reverse", "scope_only" };

    public static string ValidateCommon(RevisionInput? input, string expectedKind)
    {
        if (input is null) throw ProjectIdentityException.Invalid("revision is required.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 300 || input.Kind != expectedKind)
            throw ProjectIdentityException.Invalid($"revision.title is required and revision.kind must be {expectedKind}.");
        if (string.IsNullOrWhiteSpace(input.SchemaVersion) || input.FactKind != "normative" || input.Verification != "unverified")
            throw ProjectIdentityException.Invalid("Registry proposals require schemaVersion, factKind=normative and verification=unverified.");
        RequireObject(input.Applicability, "applicability");
        RequireObject(input.Body, "body");
        RequireArray(input.Blocks, "blocks"); RequireArray(input.Evidence, "evidence");
        RequireArray(input.Unknowns, "unknowns"); RequireArray(input.CategoryKeys, "categoryKeys");
        var categories = Strings(input.CategoryKeys, "categoryKeys");
        if (categories.Count == 0 || categories.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 128))
            throw ProjectIdentityException.Invalid("categoryKeys must contain stable, non-empty classification keys.");
        return input.SchemaVersion;
    }

    public static TypeDefinition ValidateType(RevisionInput input)
    {
        ValidateCommon(input, input.Kind ?? string.Empty);
        if (input.Kind is not ("TypeDefinition" or "ContentProfile")) throw ProjectIdentityException.Invalid("Only TypeDefinition or ContentProfile proposals are accepted.");
        var body = input.Body;
        Allowed(body, "typeKey", "version", "profile", "schema", "category", "extends");
        var key = Text(body, "typeKey"); var version = Text(body, "version"); var profile = Text(body, "profile");
        var category = Text(body, "category");
        if (!TypeKeyRegex().IsMatch(key) || key.Length > 100) throw ProjectIdentityException.Invalid("typeKey must be a stable PascalCase identifier of at most 100 characters.");
        if (!VersionRegex().IsMatch(version) || input.SchemaVersion != version)
            throw ProjectIdentityException.Invalid("version and schemaVersion must use the same numeric major.minor.patch version.");
        if (profile.Length > 100 || !Dimensions.Contains(category)) throw ProjectIdentityException.Invalid("profile is required and category must be one of the five registry dimensions.");
        if (!body.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            throw ProjectIdentityException.Invalid("schema must be a JSON Schema object.");
        if (schema.TryGetProperty("type", out var schemaType) && schemaType.GetString() != "object")
            throw ProjectIdentityException.Invalid("Registry content schemas must describe an object.");
        if (body.TryGetProperty("extends", out var extends) && (extends.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(extends.GetString())))
            throw ProjectIdentityException.Invalid("extends must identify an existing built-in type.");
        return new TypeDefinition(key, version, profile, category, schema.Clone(),
            body.TryGetProperty("extends", out var parent) ? parent.GetString() : null);
    }

    public static PredicateDefinition ValidatePredicate(RevisionInput input)
    {
        ValidateCommon(input, "PredicateDefinition");
        Allowed(input.Body, "predicate", "fromKinds", "toKinds", "meaning", "impactDirections");
        var key = Text(input.Body, "predicate"); var meaning = Text(input.Body, "meaning");
        if (!PredicateKeyRegex().IsMatch(key) || key.Length > 100) throw ProjectIdentityException.Invalid("predicate must be a stable lowerCamelCase identifier of at most 100 characters.");
        if (meaning.Length > 2000) throw ProjectIdentityException.Invalid("meaning is limited to 2000 characters.");
        var from = PropertyStrings(input.Body, "fromKinds"); var to = PropertyStrings(input.Body, "toKinds");
        var directions = PropertyStrings(input.Body, "impactDirections");
        if (from.Count == 0 || to.Count == 0 || directions.Count == 0 || from.Concat(to).Any(kind => kind != "*" && !TypeKeyRegex().IsMatch(kind)))
            throw ProjectIdentityException.Invalid("fromKinds and toKinds must contain registered type identifiers or '*'.");
        if (directions.Any(direction => !Directions.Contains(direction)) || directions.Distinct(StringComparer.Ordinal).Count() != directions.Count)
            throw ProjectIdentityException.Invalid("impactDirections must contain unique forward, reverse or scope_only values.");
        return new PredicateDefinition(key, from, to, meaning, directions);
    }

    public static void EnsureTypeCompatible(TypeDefinition next, TypeDefinition? previous)
    {
        if (previous is null) return;
        if (CompareVersions(next.Version, previous.Version) <= 0)
            throw ProjectIdentityException.Conflict($"Schema version {next.Version} must be greater than the existing version {previous.Version}.");
        if (next.Profile != previous.Profile || next.Category != previous.Category)
            throw ProjectIdentityException.Conflict("An existing type's content profile and category are stable; create a new typeKey for different semantics.");
        foreach (var issue in SchemaChanges(previous.Schema, next.Schema, "schema"))
            throw ProjectIdentityException.Conflict($"Incompatible schema change at {issue.Path}: {issue.Reason} Existing revisions remain readable; propose an additive migration or a new typeKey.");
    }

    public static void EnsurePredicateUnique(PredicateDefinition next, IEnumerable<PredicateDefinition> existing)
    {
        var normalizedMeaning = NormalizeMeaning(next.Meaning);
        foreach (var item in existing)
        {
            if (item.Key.Equals(next.Key, StringComparison.Ordinal))
                throw ProjectIdentityException.Conflict($"Predicate '{next.Key}' is already registered. Propose a revision of that definition rather than a second identity.");
            if (NormalizeMeaning(item.Meaning) == normalizedMeaning && SameEndpoints(item.FromKinds, next.FromKinds) && SameEndpoints(item.ToKinds, next.ToKinds))
                throw ProjectIdentityException.Conflict($"Predicate '{next.Key}' duplicates the meaning and endpoints of '{item.Key}'. Reuse the registered predicate instead of creating a synonym.");
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var a = left.Split('.').Select(int.Parse).ToArray(); var b = right.Split('.').Select(int.Parse).ToArray();
        for (var index = 0; index < 3; index++) { var comparison = a[index].CompareTo(b[index]); if (comparison != 0) return comparison; }
        return 0;
    }

    public static string NormalizeMeaning(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) result.Append(Rune.ToLowerInvariant(rune).ToString());
        return result.ToString();
    }

    private static IEnumerable<SchemaIssue> SchemaChanges(JsonElement before, JsonElement after, string path)
    {
        if (before.TryGetProperty("properties", out var oldProperties) && oldProperties.ValueKind == JsonValueKind.Object)
        {
            after.TryGetProperty("properties", out var newProperties);
            foreach (var property in oldProperties.EnumerateObject())
            {
                if (newProperties.ValueKind != JsonValueKind.Object || !newProperties.TryGetProperty(property.Name, out var replacement))
                { yield return new($"{path}.properties.{property.Name}", "an existing property cannot be removed."); continue; }
                if (property.Value.TryGetProperty("type", out var oldType) && replacement.TryGetProperty("type", out var newType) && oldType.GetRawText() != newType.GetRawText())
                    yield return new($"{path}.properties.{property.Name}.type", $"the type changed from {oldType.GetRawText()} to {newType.GetRawText()}.");
            }
        }
        var oldRequired = RequiredSet(before); var newRequired = RequiredSet(after);
        foreach (var added in newRequired.Except(oldRequired, StringComparer.Ordinal))
        {
            if (before.TryGetProperty("properties", out var previousProperties) && previousProperties.TryGetProperty(added, out _))
                yield return new($"{path}.required", $"existing optional property '{added}' cannot become required.");
            else if (!after.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(added, out var definition) ||
                     !definition.TryGetProperty("default", out _))
                yield return new($"{path}.required", $"new required property '{added}' needs a default for compatibility with existing content.");
        }
    }

    private static HashSet<string> RequiredSet(JsonElement schema) => schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
        ? required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    private static bool SameEndpoints(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private static void Allowed(JsonElement body, params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var unknown = body.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null) throw ProjectIdentityException.Invalid($"Unknown registry body field '{unknown}'.");
    }

    private static string Text(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw ProjectIdentityException.Invalid($"body.{name} is required.");
        return value.GetString()!;
    }

    private static IReadOnlyList<string> PropertyStrings(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) throw ProjectIdentityException.Invalid($"body.{name} is required.");
        return Strings(value, "body." + name);
    }

    private static IReadOnlyList<string> Strings(JsonElement value, string path)
    {
        RequireArray(value, path);
        var items = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        if (items.Any(item => item is null)) throw ProjectIdentityException.Invalid($"{path} must contain only strings.");
        return items!;
    }

    private static void RequireObject(JsonElement value, string path)
    { if (value.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid($"revision.{path} must be an object."); }
    private static void RequireArray(JsonElement value, string path)
    { if (value.ValueKind != JsonValueKind.Array) throw ProjectIdentityException.Invalid($"revision.{path} must be an array."); }

    private sealed record SchemaIssue(string Path, string Reason);
    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)] private static partial Regex TypeKeyRegex();
    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)] private static partial Regex PredicateKeyRegex();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)] private static partial Regex VersionRegex();
}

public sealed record TypeDefinition(string Key, string Version, string Profile, string Category, JsonElement Schema, string? Extends);
public sealed record PredicateDefinition(string Key, IReadOnlyList<string> FromKinds, IReadOnlyList<string> ToKinds, string Meaning, IReadOnlyList<string> ImpactDirections);
