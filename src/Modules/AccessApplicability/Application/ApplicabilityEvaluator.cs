using System.Globalization;
using System.Text.Json;
using AccessApplicability.Contracts;
using ProjectIdentity.Application;

namespace AccessApplicability.Application;

/// <summary>Deterministic AND-across-dimensions / OR-within-lists applicability evaluation.</summary>
public static class ApplicabilityEvaluator
{
    public static ApplicabilityResult Evaluate(JsonElement applicability, ApplicableContext? supplied)
    {
        if (applicability.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            applicability.ValueKind == JsonValueKind.Object && !applicability.EnumerateObject().Any())
            return new(ApplicabilityStatus.Unknown, ["Applicability metadata is missing."]);
        if (applicability.ValueKind != JsonValueKind.Object)
            throw ProjectIdentityException.Invalid("Applicability must be an object.");

        var reasons = new List<string>();
        var unknown = false;
        var unrestricted = OptionalBoolean(applicability, "unrestricted") ?? false;
        var constrained = false;

        foreach (var dimension in new[]
        {
            MatchDimension(applicability, "contexts", supplied?.ContextKey, "context", reasons, ref constrained),
            MatchDimension(applicability, "organizations", supplied?.Organization, "organization", reasons, ref constrained),
            MatchDimension(applicability, "products", supplied?.Product, "product", reasons, ref constrained),
            MatchDimension(applicability, "environments", supplied?.Environment, "environment", reasons, ref constrained)
        })
        {
            if (dimension == DimensionResult.Mismatch) return new(ApplicabilityStatus.NotApplicable, reasons);
            unknown |= dimension == DimensionResult.Unknown;
        }

        var from = OptionalDate(applicability, "validFrom");
        var to = OptionalDate(applicability, "validTo");
        if (from.HasValue && to.HasValue && from >= to)
            throw ProjectIdentityException.Invalid("Applicability validFrom must precede validTo.");
        if (from.HasValue || to.HasValue)
        {
            constrained = true;
            if (supplied?.AsOf is not { } asOf)
            {
                unknown = true;
                reasons.Add("The asOf time is required to evaluate the validity interval.");
            }
            else if (from.HasValue && asOf < from.Value || to.HasValue && asOf >= to.Value)
            {
                reasons.Add("The supplied asOf time is outside the validity interval.");
                return new(ApplicabilityStatus.NotApplicable, reasons);
            }
        }

        if (applicability.TryGetProperty("expression", out var expression) && expression.ValueKind != JsonValueKind.Null)
        {
            constrained = true;
            var expressionResult = EvaluateExpression(expression, supplied?.Facts, reasons, 0, new NodeBudget());
            if (expressionResult == TriState.False) return new(ApplicabilityStatus.NotApplicable, reasons);
            if (expressionResult == TriState.Unknown) unknown = true;
        }

        if (unknown) return new(ApplicabilityStatus.Unknown, reasons);
        if (constrained) return new(ApplicabilityStatus.Applicable, reasons);
        return unrestricted
            ? new(ApplicabilityStatus.Applicable, ["The definition explicitly declares unrestricted applicability."])
            : new(ApplicabilityStatus.Unknown, ["No applicability constraints or explicit unrestricted declaration were supplied."]);
    }

    /// <summary>Combines mutually exclusive specifications without hiding an actual conflict.</summary>
    public static ApplicabilityResult Combine(IEnumerable<ApplicabilityResult> specifications)
    {
        var items = specifications.ToArray();
        if (items.Any(item => item.Status == ApplicabilityStatus.Conflict))
            return new(ApplicabilityStatus.Conflict, items.SelectMany(item => item.Reasons).Distinct().ToArray());
        var applicable = items.Where(item => item.Status == ApplicabilityStatus.Applicable).ToArray();
        if (applicable.Length > 1)
            return new(ApplicabilityStatus.Conflict, ["Multiple mutually exclusive applicability specifications match the same scenario."]);
        if (applicable.Length == 1) return applicable[0];
        if (items.Any(item => item.Status == ApplicabilityStatus.Unknown))
            return new(ApplicabilityStatus.Unknown, items.Where(item => item.Status == ApplicabilityStatus.Unknown).SelectMany(item => item.Reasons).Distinct().ToArray());
        return new(ApplicabilityStatus.NotApplicable, items.SelectMany(item => item.Reasons).Distinct().ToArray());
    }

    private static DimensionResult MatchDimension(JsonElement source, string property, string? actual, string label,
        List<string> reasons, ref bool constrained)
    {
        if (!source.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null) return DimensionResult.Match;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0 || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
            throw ProjectIdentityException.Invalid($"Applicability {property} must be a non-empty string array.");
        constrained = true;
        if (string.IsNullOrWhiteSpace(actual))
        {
            reasons.Add($"The {label} is required to determine applicability.");
            return DimensionResult.Unknown;
        }
        if (values.EnumerateArray().Any(value => string.Equals(value.GetString(), actual, StringComparison.Ordinal))) return DimensionResult.Match;
        reasons.Add($"The supplied {label} does not match this definition.");
        return DimensionResult.Mismatch;
    }

    private static TriState EvaluateExpression(JsonElement expression, IReadOnlyDictionary<string, JsonElement>? facts,
        List<string> reasons, int depth, NodeBudget budget)
    {
        if (++depth > 32 || ++budget.Count > 1000 || expression.ValueKind != JsonValueKind.Object)
            throw ProjectIdentityException.Invalid("The applicability expression exceeds its depth/node limit or is not an object.");
        var op = RequiredString(expression, "op");
        switch (op)
        {
            case "all":
            case "any":
            {
                if (!expression.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() == 0)
                    throw ProjectIdentityException.Invalid($"{op} requires a non-empty args array.");
                var sawUnknown = false;
                foreach (var arg in args.EnumerateArray())
                {
                    var value = EvaluateExpression(arg, facts, reasons, depth, budget);
                    if (op == "all" && value == TriState.False) return TriState.False;
                    if (op == "any" && value == TriState.True) return TriState.True;
                    sawUnknown |= value == TriState.Unknown;
                }
                if (sawUnknown) return TriState.Unknown;
                return op == "all" ? TriState.True : TriState.False;
            }
            case "not":
            {
                if (!expression.TryGetProperty("arg", out var arg)) throw ProjectIdentityException.Invalid("not requires arg.");
                return EvaluateExpression(arg, facts, reasons, depth, budget) switch
                {
                    TriState.True => TriState.False, TriState.False => TriState.True, _ => TriState.Unknown
                };
            }
            case "exists":
            case "missing":
            case "isNull":
            {
                var operand = RequiredProperty(expression, "operand");
                var present = TryOperand(operand, facts, out var value, out var reason);
                if (op == "exists") return present ? TriState.True : TriState.False;
                if (op == "missing") return present ? TriState.False : TriState.True;
                if (!present) { reasons.Add(reason ?? "A required fact is missing."); return TriState.Unknown; }
                return value.ValueKind == JsonValueKind.Null ? TriState.True : TriState.False;
            }
            case "eq": case "ne": case "gt": case "gte": case "lt": case "lte":
            case "contains": case "startsWith": case "in":
                return Compare(expression, op, facts, reasons);
            default:
                throw ProjectIdentityException.Invalid($"Unsupported applicability operator: {op}.");
        }
    }

    private static TriState Compare(JsonElement expression, string op, IReadOnlyDictionary<string, JsonElement>? facts, List<string> reasons)
    {
        var hasLeft = TryOperand(RequiredProperty(expression, "left"), facts, out var left, out var leftReason);
        var hasRight = TryOperand(RequiredProperty(expression, "right"), facts, out var right, out var rightReason);
        if (!hasLeft || !hasRight)
        {
            reasons.Add(leftReason ?? rightReason ?? "A comparison input is missing.");
            return TriState.Unknown;
        }
        if (left.ValueKind == JsonValueKind.Null || right.ValueKind == JsonValueKind.Null)
        {
            if (op == "eq") return BothNullOrOne(left, right) ? TriState.True : TriState.False;
            if (op == "ne") return BothNullOrOne(left, right) ? TriState.False : TriState.True;
            reasons.Add("Null cannot be used in an ordered or membership comparison.");
            return TriState.Unknown;
        }
        var comparison = RequiredString(expression, "comparison");
        try
        {
            var ordering = CompareValues(left, right, comparison);
            return op switch
            {
                "eq" => ordering == 0 ? TriState.True : TriState.False,
                "ne" => ordering != 0 ? TriState.True : TriState.False,
                "gt" => ordering > 0 ? TriState.True : TriState.False,
                "gte" => ordering >= 0 ? TriState.True : TriState.False,
                "lt" => ordering < 0 ? TriState.True : TriState.False,
                "lte" => ordering <= 0 ? TriState.True : TriState.False,
                "contains" => Contains(left, right, comparison) ? TriState.True : TriState.False,
                "startsWith" => StartsWith(left, right, comparison) ? TriState.True : TriState.False,
                "in" => In(left, right, comparison) ? TriState.True : TriState.False,
                _ => TriState.Unknown
            };
        }
        catch (InvalidOperationException exception)
        {
            reasons.Add(exception.Message);
            return TriState.Unknown;
        }
    }

    private static bool BothNullOrOne(JsonElement left, JsonElement right) =>
        left.ValueKind == JsonValueKind.Null && right.ValueKind == JsonValueKind.Null;

    private static int CompareValues(JsonElement left, JsonElement right, string comparison)
    {
        if (left.ValueKind == JsonValueKind.Number || right.ValueKind == JsonValueKind.Number)
        {
            if (!TryDecimal(left, out var l) || !TryDecimal(right, out var r)) throw new InvalidOperationException("Comparison values have incompatible numeric types.");
            return decimal.Compare(l, r);
        }
        if (left.ValueKind is JsonValueKind.True or JsonValueKind.False || right.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (left.ValueKind != right.ValueKind) throw new InvalidOperationException("Comparison values have incompatible boolean types.");
            return (left.GetBoolean() ? 1 : 0).CompareTo(right.GetBoolean() ? 1 : 0);
        }
        if (left.ValueKind != JsonValueKind.String || right.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Comparison values have incompatible types.");
        var lText = left.GetString()!;
        var rText = right.GetString()!;
        if (comparison == "typed" && DateTimeOffset.TryParse(lText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ld) &&
            DateTimeOffset.TryParse(rText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rd)) return ld.CompareTo(rd);
        return string.Compare(lText, rText, comparison == "ordinalIgnoreCase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool Contains(JsonElement left, JsonElement right, string comparison)
    {
        if (left.ValueKind == JsonValueKind.Array) return left.EnumerateArray().Any(item => CompareValues(item, right, comparison) == 0);
        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return left.GetString()!.Contains(right.GetString()!, comparison == "ordinalIgnoreCase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        throw new InvalidOperationException("contains requires a string or list left operand.");
    }

    private static bool StartsWith(JsonElement left, JsonElement right, string comparison) =>
        left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String
            ? left.GetString()!.StartsWith(right.GetString()!, comparison == "ordinalIgnoreCase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            : throw new InvalidOperationException("startsWith requires string operands.");

    private static bool In(JsonElement left, JsonElement right, string comparison)
    {
        if (right.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("in requires a list right operand.");
        return right.EnumerateArray().Any(item => CompareValues(left, item, comparison) == 0);
    }

    private static bool TryOperand(JsonElement operand, IReadOnlyDictionary<string, JsonElement>? facts, out JsonElement value, out string? reason)
    {
        value = default; reason = null;
        if (operand.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("An expression operand must be an object.");
        return RequiredString(operand, "kind") switch
        {
            "literal" => TryLiteral(operand, out value),
            "field" => TryField(operand, facts, out value, out reason),
            _ => throw ProjectIdentityException.Invalid("An expression operand kind must be field or literal.")
        };
    }

    private static bool TryLiteral(JsonElement operand, out JsonElement value)
    {
        if (!operand.TryGetProperty("value", out value)) throw ProjectIdentityException.Invalid("A literal operand requires value.");
        _ = RequiredString(operand, "valueType");
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 65536)
            throw ProjectIdentityException.Invalid("An applicability string exceeds 64 KiB.");
        return true;
    }

    private static bool TryField(JsonElement operand, IReadOnlyDictionary<string, JsonElement>? facts, out JsonElement value, out string? reason)
    {
        var id = RequiredString(operand, "attributeId");
        if (!Guid.TryParse(id, out _)) throw ProjectIdentityException.Invalid("A field operand requires a UUID attributeId.");
        if (facts is not null && (facts.TryGetValue(id, out value) || facts.TryGetValue(Guid.Parse(id).ToString(), out value)))
        { reason = null; return true; }
        value = default; reason = $"Required fact {id} was not supplied."; return false;
    }

    private static bool TryDecimal(JsonElement value, out decimal result)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out result);
        if (value.ValueKind == JsonValueKind.String) return decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        result = default; return false;
    }

    private static JsonElement RequiredProperty(JsonElement source, string name) => source.TryGetProperty(name, out var value)
        ? value : throw ProjectIdentityException.Invalid($"Expression property {name} is required.");
    private static string RequiredString(JsonElement source, string name) => RequiredProperty(source, name).ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(RequiredProperty(source, name).GetString()) ? RequiredProperty(source, name).GetString()! :
        throw ProjectIdentityException.Invalid($"Expression property {name} must be a non-empty string.");
    private static bool? OptionalBoolean(JsonElement source, string name) => !source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() :
        throw ProjectIdentityException.Invalid($"Applicability {name} must be boolean.");
    private static DateTimeOffset? OptionalDate(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            throw ProjectIdentityException.Invalid($"Applicability {name} must be an RFC 3339 timestamp.");
        return result;
    }

    private enum TriState { False, True, Unknown }
    private enum DimensionResult { Match, Mismatch, Unknown }
    private sealed class NodeBudget { public int Count; }
}
