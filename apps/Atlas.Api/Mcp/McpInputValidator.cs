using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectIdentity.Application;

namespace Atlas.Api.Mcp;

internal static class McpInputValidator
{
    public static void Validate(JsonElement schema, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        ValidateValue(schema, document.RootElement, schema, "arguments");
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, JsonElement root, string path)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            if (reference.GetString()?.StartsWith("#/", StringComparison.Ordinal) != true) throw Invalid(path, "Unsupported schema reference.");
            var target = root;
            foreach (var part in reference.GetString()![2..].Split('/')) target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
            ValidateValue(target, value, root, path);
        }
        if (schema.TryGetProperty("allOf", out var all)) foreach (var child in all.EnumerateArray()) ValidateValue(child, value, root, path);
        if (schema.TryGetProperty("if", out var condition) && Matches(condition, value, root))
            if (schema.TryGetProperty("then", out var then)) ValidateValue(then, value, root, path);
        if (schema.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(child => Matches(child, value, root))) throw Invalid(path, "No permitted alternative matched.");
        if (schema.TryGetProperty("oneOf", out var one) && one.EnumerateArray().Count(child => Matches(child, value, root)) != 1) throw Invalid(path, "Exactly one permitted alternative must match.");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) throw Invalid(path, "The constant value is required.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value))) throw Invalid(path, "The value is not in the allowed set.");
        if (schema.TryGetProperty("type", out var type)) ValidateType(type.GetString(), value, path);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            if (schema.TryGetProperty("required", out var required)) foreach (var name in required.EnumerateArray())
                if (!value.TryGetProperty(name.GetString()!, out _)) throw Invalid(path, $"Missing required property '{name.GetString()}'.");
            if (properties.ValueKind == JsonValueKind.Object) foreach (var property in properties.EnumerateObject())
                if (value.TryGetProperty(property.Name, out var child)) ValidateValue(property.Value, child, root, $"{path}.{property.Name}");
            if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                foreach (var property in value.EnumerateObject().Where(property => properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(property.Name, out _)))
                {
                    if (additional.ValueKind == JsonValueKind.False) throw Invalid(path, $"Unexpected property '{property.Name}'.");
                    if (additional.ValueKind == JsonValueKind.Object) ValidateValue(additional, property.Value, root, $"{path}.{property.Name}");
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minItems) && value.GetArrayLength() < minItems.GetInt32()) throw Invalid(path, "Too few items.");
            if (schema.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32()) throw Invalid(path, "Too many items.");
            if (schema.TryGetProperty("items", out var items)) for (var index = 0; index < value.GetArrayLength(); index++) ValidateValue(items, value[index], root, $"{path}[{index}]");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32()) throw Invalid(path, "The string is too short.");
            if (schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32()) throw Invalid(path, "The string is too long.");
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Invalid(path, "The string format is invalid.");
            if (schema.TryGetProperty("format", out var format)) ValidateFormat(format.GetString(), text, path);
        }
        if (value.ValueKind is JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var minimum) && value.GetDecimal() < minimum.GetDecimal()) throw Invalid(path, "The number is below the minimum.");
            if (schema.TryGetProperty("maximum", out var maximum) && value.GetDecimal() > maximum.GetDecimal()) throw Invalid(path, "The number is above the maximum.");
        }
    }

    private static bool Matches(JsonElement schema, JsonElement value, JsonElement root)
    { try { ValidateValue(schema, value, root, "arguments"); return true; } catch (ProjectIdentityException) { return false; } }
    private static void ValidateType(string? type, JsonElement value, string path)
    {
        var valid = type switch
        {
            null => true, "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "number" => value.ValueKind == JsonValueKind.Number,
            "null" => value.ValueKind == JsonValueKind.Null, _ => true
        };
        if (!valid) throw Invalid(path, $"Expected JSON type '{type}'.");
    }
    private static void ValidateFormat(string? format, string text, string path)
    {
        var valid = format switch { "uuid" => Guid.TryParse(text, out _), "date-time" => DateTimeOffset.TryParse(text, out _), _ => true };
        if (!valid) throw Invalid(path, $"Expected format '{format}'.");
    }
    private static ProjectIdentityException Invalid(string path, string message) => ProjectIdentityException.Invalid($"Invalid {path}: {message}");
}
