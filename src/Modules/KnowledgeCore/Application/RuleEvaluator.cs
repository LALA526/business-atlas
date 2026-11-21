using System.Globalization;
using System.Text.Json;
using KnowledgeCore.Contracts;

namespace KnowledgeCore.Application;

/// <summary>Deterministic AST v1 evaluator. It never executes outcomes or external code.</summary>
public static class RuleEvaluator
{
    private const int MaxDepth = 32;
    private const int MaxNodes = 1000;
    private static readonly HashSet<string> Operators = ["exists", "missing", "isNull", "eq", "ne", "gt", "gte", "lt", "lte", "contains", "startsWith", "in", "all", "any", "not"];

    public static RuleEvaluation Evaluate(JsonElement body, JsonElement facts)
    {
        var conditions = new List<RuleCondition>();
        var unknowns = new List<JsonElement>();
        var evidence = new List<JsonElement>();
        if (!Object(facts) || !Object(body) || String(body, "evaluationMode") != "structured")
            return new("not_executable", [], conditions, unknowns, evidence, ["The rule is not an executable structured AST v1 rule."]);

        var rows = Array(body, "rows");
        if (rows is null || rows.Value.GetArrayLength() == 0)
            return new("not_executable", [], conditions, unknowns, evidence, ["The rule has no decision rows."]);
        var policy = String(body, "priorityPolicy") ?? "unique";
        if (policy is not ("unique" or "ordered" or "collect"))
            return NotExecutable(conditions, unknowns, "The priority policy is missing or unsupported.");
        if (policy == "collect" && String(body, "collectOperator") is not ("set_union" or "ordered_list"))
            return NotExecutable(conditions, unknowns, "Collect requires an approved set_union or ordered_list operator.");

        var inputTypes = ReadInputTypes(body);
        var evaluated = new List<(JsonElement Row, Truth Truth)>();
        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeCount = 0;
        foreach (var row in rows.Value.EnumerateArray())
        {
            var rowId = String(row, "rowId");
            if (rowId is null || !rowIds.Add(rowId) || !row.TryGetProperty("outcome", out var outcome) || !Object(outcome))
                return NotExecutable(conditions, unknowns, "Every decision row requires a unique rowId and structured outcome.");
            if (!row.TryGetProperty("when", out var expression))
                return NotExecutable(conditions, unknowns, "Every structured decision row requires an executable condition.");
            if (ValidateExpression(expression, 1, ref nodeCount) is { } validationError)
                return NotExecutable(conditions, unknowns, validationError);
            var result = EvaluateExpression(expression, facts, inputTypes);
            evaluated.Add((row, result.Value));
            conditions.Add(new(rowId, Name(result.Value), result.Reason));
            if (result.Value == Truth.Unknown) unknowns.Add(Unknown(rowId, result.Reason));
        }
        if (evaluated.Any(item => item.Truth == Truth.Conflict))
            return new("conflict", [], conditions, unknowns, evidence, []);

        var matches = evaluated.Where(item => item.Truth == Truth.True).Select(item => item.Row).ToList();
        var hasUnknown = evaluated.Any(item => item.Truth == Truth.Unknown);
        if (policy == "ordered")
        {
            var priorities = new Dictionary<int, string>();
            foreach (var (row, _) in evaluated)
            {
                if (!Integer(row, "priority", out var priority) || !priorities.TryAdd(priority, String(row, "rowId")!))
                    return new("conflict", [], conditions, unknowns, evidence, ["Ordered rows must have unique, explicit priorities."]);
            }
            foreach (var (row, truth) in evaluated.OrderBy(item => item.Row.GetProperty("priority").GetInt32()))
            {
                if (truth == Truth.True) return Matched(row, conditions, unknowns, evidence);
                if (truth == Truth.Unknown) return new("unknown", [], conditions, unknowns, evidence, []);
            }
        }
        else if (policy == "unique")
        {
            if (matches.Count > 1) return new("conflict", [], conditions, unknowns, evidence, ["More than one unique row matched."]);
            if (matches.Count == 1) return hasUnknown ? new("unknown", [], conditions, unknowns, evidence, []) : Matched(matches[0], conditions, unknowns, evidence);
            if (hasUnknown) return new("unknown", [], conditions, unknowns, evidence, []);
        }
        else
        {
            if (hasUnknown) return new("unknown", [], conditions, unknowns, evidence, []);
            if (matches.Count > 0)
            {
                var outcomes = matches.Select(item => item.GetProperty("outcome").Clone()).ToList();
                if (String(body, "collectOperator") == "set_union")
                {
                    var values = matches.SelectMany(item => item.GetProperty("outcome").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                        ? value.EnumerateArray().Select(item => item.Clone()) : [OutcomeValue(item)]).ToList();
                    var distinct = values.GroupBy(CanonicalJson.Sha256).Select(group => group.First()).ToArray();
                    var merged = matches[0].GetProperty("outcome").Clone();
                    outcomes = [JsonDocument.Parse(JsonSerializer.Serialize(new { kind = String(merged, "kind"), description = String(merged, "description"), value = distinct })).RootElement.Clone()];
                }
                foreach (var row in matches) AddEvidence(row, evidence);
                return new("matched", outcomes, conditions, unknowns, evidence, []);
            }
        }

        if (body.TryGetProperty("defaultOutcome", out var defaultOutcome) && defaultOutcome.ValueKind == JsonValueKind.Object)
            return new("defaulted", [defaultOutcome.Clone()], conditions, unknowns, evidence, []);
        return new("not_matched", [], conditions, unknowns, evidence, []);
    }

    private static RuleEvaluation Matched(JsonElement row, List<RuleCondition> conditions, List<JsonElement> unknowns, List<JsonElement> evidence)
    {
        AddEvidence(row, evidence);
        return new("matched", [row.GetProperty("outcome").Clone()], conditions, unknowns, evidence, []);
    }

    private static RuleEvaluation NotExecutable(List<RuleCondition> conditions, List<JsonElement> unknowns, string reason) =>
        new("not_executable", [], conditions, unknowns, [], [reason]);

    private static string? ValidateExpression(JsonElement expression, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes || depth > MaxDepth) return "The expression exceeds AST v1 resource limits.";
        if (!Object(expression) || String(expression, "op") is not { } op || !Operators.Contains(op)) return "The expression operator is missing or unsupported.";
        if (op is "all" or "any")
        {
            if (Array(expression, "args") is not { } args || args.GetArrayLength() == 0) return $"{op} requires at least one argument.";
            foreach (var arg in args.EnumerateArray())
                if (ValidateExpression(arg, depth + 1, ref nodes) is { } error) return error;
            return null;
        }
        if (op == "not")
            return !expression.TryGetProperty("arg", out var arg) ? "not requires an argument." : ValidateExpression(arg, depth + 1, ref nodes);
        if (op is "exists" or "missing" or "isNull")
            return !expression.TryGetProperty("operand", out var operand) ? $"{op} requires an operand." : ValidateOperand(operand);
        if (!expression.TryGetProperty("left", out var left) || !expression.TryGetProperty("right", out var right)) return "A comparison requires left and right operands.";
        if (String(expression, "comparison") is { } comparison && comparison is not ("ordinal" or "ordinalIgnoreCase" or "typed")) return "The comparison policy is unsupported.";
        return ValidateOperand(left) ?? ValidateOperand(right);
    }

    private static string? ValidateOperand(JsonElement operand)
    {
        if (!Object(operand)) return "The operand is not an object.";
        if (String(operand, "kind") == "literal")
        {
            if (String(operand, "valueType") is not ("string" or "decimal" or "integer" or "boolean" or "date" or "datetime" or "enum" or "null" or "list") || !operand.TryGetProperty("value", out _))
                return "A literal operand requires a supported valueType and value.";
            return null;
        }
        return String(operand, "kind") == "field" && Guid.TryParse(String(operand, "attributeId"), out _)
            ? null : "A field operand requires a valid attributeId.";
    }

    private static (Truth Value, string Reason) EvaluateExpression(JsonElement expression, JsonElement facts, IReadOnlyDictionary<Guid, string> inputTypes)
    {
        var op = expression.GetProperty("op").GetString()!;
        if (op == "all" || op == "any")
        {
            var values = new List<(Truth Value, string Reason)>();
            foreach (var item in expression.GetProperty("args").EnumerateArray()) values.Add(EvaluateExpression(item, facts, inputTypes));
            if (values.Any(item => item.Value == Truth.Conflict)) return (Truth.Conflict, "An argument contains a conflict.");
            if (op == "all" && values.Any(item => item.Value == Truth.False)) return (Truth.False, "At least one all argument is false.");
            if (op == "any" && values.Any(item => item.Value == Truth.True)) return (Truth.True, "At least one any argument is true.");
            if (values.Any(item => item.Value == Truth.Unknown)) return (Truth.Unknown, "An argument is unknown.");
            return op == "all" ? (Truth.True, "All arguments are true.") : (Truth.False, "All any arguments are false.");
        }
        if (op == "not")
        {
            if (!expression.TryGetProperty("arg", out var arg)) return (Truth.Unknown, "not requires an argument.");
            var value = EvaluateExpression(arg, facts, inputTypes);
            return value.Value switch { Truth.True => (Truth.False, "The argument is true."), Truth.False => (Truth.True, "The argument is false."), _ => value };
        }
        if (op is "exists" or "missing" or "isNull")
        {
            if (!expression.TryGetProperty("operand", out var operand)) return (Truth.Unknown, $"{op} requires an operand.");
            var value = ReadOperand(operand, facts, inputTypes);
            if (value.Error is not null) return (Truth.Unknown, value.Error);
            var result = op == "exists" ? value.Present : op == "missing" ? !value.Present : value.Present && value.Value.ValueKind == JsonValueKind.Null;
            return (result ? Truth.True : Truth.False, $"{op} evaluated deterministically.");
        }

        if (!expression.TryGetProperty("left", out var leftNode) || !expression.TryGetProperty("right", out var rightNode)) return (Truth.Unknown, "A comparison requires left and right operands.");
        var left = ReadOperand(leftNode, facts, inputTypes); var right = ReadOperand(rightNode, facts, inputTypes);
        if (left.Error is not null || right.Error is not null) return (Truth.Unknown, left.Error ?? right.Error!);
        if (!left.Present || !right.Present) return (Truth.Unknown, "A required fact is missing.");
        if (left.Value.ValueKind == JsonValueKind.Null || right.Value.ValueKind == JsonValueKind.Null)
        {
            if (op is not ("eq" or "ne")) return (Truth.Unknown, "Null only supports explicit equality or isNull comparisons.");
            var bothNull = left.Value.ValueKind == JsonValueKind.Null && right.Value.ValueKind == JsonValueKind.Null;
            var nullAnswer = op == "eq" ? bothNull : !bothNull;
            return (nullAnswer ? Truth.True : Truth.False, "Null equality was evaluated explicitly.");
        }
        if (op != "in" && (left.Value.ValueKind == JsonValueKind.Array || right.Value.ValueKind == JsonValueKind.Array))
            return (Truth.Unknown, "Only the in operator accepts a list operand.");
        var comparison = String(expression, "comparison") ?? "typed";
        var compared = Compare(left, right, comparison);
        if (compared.Error is not null) return (Truth.Unknown, compared.Error);
        if (op is "contains" or "startsWith")
        {
            if (left.Value.ValueKind != JsonValueKind.String || right.Value.ValueKind != JsonValueKind.String) return (Truth.Unknown, $"{op} requires string operands.");
            var ignoreCase = comparison == "ordinalIgnoreCase";
            var text = left.Value.GetString()!; var fragment = right.Value.GetString()!;
            var textAnswer = op == "contains" ? text.Contains(fragment, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) : text.StartsWith(fragment, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return (textAnswer ? Truth.True : Truth.False, $"{op} evaluated deterministically.");
        }
        var answer = op switch
        {
            "eq" => compared.Order == 0, "ne" => compared.Order != 0, "gt" => compared.Order > 0,
            "gte" => compared.Order >= 0, "lt" => compared.Order < 0, "lte" => compared.Order <= 0,
            "contains" => compared.Order == 0, "startsWith" => compared.Order == 0,
            "in" => compared.Order == 0, _ => false
        };
        return (answer ? Truth.True : Truth.False, $"{op} evaluated deterministically.");
    }

    private static (int Order, string? Error) Compare(OperandValue left, OperandValue right, string comparison)
    {
        var type = left.Type ?? right.Type;
        var op = left.Value;
        var other = right.Value;
        if (type == "list" || op.ValueKind == JsonValueKind.Array || other.ValueKind == JsonValueKind.Array)
        {
            if (op.ValueKind == JsonValueKind.Array || other.ValueKind != JsonValueKind.Array)
                return (1, "List comparisons require a scalar left operand and a list right operand.");
            var elementType = left.Type == "list" ? null : left.Type;
            foreach (var item in other.EnumerateArray())
            {
                var membership = Compare(left with { Type = elementType }, new(true, item, elementType, null), comparison);
                if (membership.Error is not null) return membership;
                if (membership.Order == 0) return (0, null);
            }
            return (1, null);
        }
        if (type is "string" or "date" or "datetime" or "enum" && (op.ValueKind != JsonValueKind.String || other.ValueKind != JsonValueKind.String))
            return (1, "String, enum, and temporal operands must use JSON strings.");
        if (type is "decimal" or "integer")
        {
            if (!Decimal(op, out var a) || !Decimal(other, out var b)) return (1, "A decimal operand is invalid; locale-formatted numbers are not accepted.");
            if (type == "integer" && (a != decimal.Truncate(a) || b != decimal.Truncate(b))) return (1, "An integer operand has a fractional value.");
            return (a.CompareTo(b), null);
        }
        if (type == "boolean" || op.ValueKind is JsonValueKind.True or JsonValueKind.False || other.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (op.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || other.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (1, "Boolean operands must be JSON booleans.");
            return (op.GetBoolean().CompareTo(other.GetBoolean()), null);
        }
        if (op.ValueKind != JsonValueKind.String || other.ValueKind != JsonValueKind.String) return (1, "Comparison operands have incompatible types.");
        var x = op.GetString()!; var y = other.GetString()!;
        if (x.Length > 65536 || y.Length > 65536) return (1, "A string operand exceeds the 64KiB limit.");
        if (type == "date")
        {
            if (!DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dx) || !DateOnly.TryParseExact(y, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dy)) return (1, "Dates must use ISO yyyy-MM-dd.");
            return (dx.CompareTo(dy), null);
        }
        if (type == "datetime")
        {
            if (!ExplicitOffset(x) || !ExplicitOffset(y) || !DateTimeOffset.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtx) || !DateTimeOffset.TryParse(y, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dty)) return (1, "Datetimes must be ISO 8601 values with an explicit offset.");
            return (dtx.CompareTo(dty), null);
        }
        var stringComparison = comparison == "ordinalIgnoreCase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (string.Compare(x, y, stringComparison), null);
    }

    private static Dictionary<Guid, string> ReadInputTypes(JsonElement body)
    {
        var result = new Dictionary<Guid, string>();
        if (Array(body, "inputs") is not { } inputs) return result;
        foreach (var input in inputs.EnumerateArray())
        {
            if (!input.TryGetProperty("attribute", out var attribute) || !Guid.TryParse(String(attribute, "unitId"), out var id)) continue;
            if (String(input, "valueType") is { } type) result[id] = type;
        }
        return result;
    }

    private static OperandValue ReadOperand(JsonElement node, JsonElement facts, IReadOnlyDictionary<Guid, string> inputTypes)
    {
        if (!Object(node)) return new(false, default, null, "The operand is not an object.");
        if (String(node, "kind") == "literal")
        {
            var type = String(node, "valueType");
            var value = node.GetProperty("value");
            return LiteralTypeMatches(type, value)
                ? new(true, value.Clone(), type, null)
                : new(true, value.Clone(), type, "The literal value does not match its declared valueType.");
        }
        if (String(node, "kind") != "field" || !Guid.TryParse(String(node, "attributeId"), out var id)) return new(false, default, null, "A field operand requires a valid attributeId.");
        if (!facts.TryGetProperty(id.ToString(), out var fact)) return new(false, default, null, null);
        return new(true, fact.Clone(), inputTypes.TryGetValue(id, out var inputType) ? inputType : null, null);
    }

    private static bool LiteralTypeMatches(string? type, JsonElement value) => type switch
    {
        "string" or "date" or "datetime" or "enum" => value.ValueKind == JsonValueKind.String,
        "decimal" => value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "list" => value.ValueKind == JsonValueKind.Array,
        _ => false
    };

    private static bool ExplicitOffset(string value) => value.EndsWith('Z') || value.EndsWith('z') ||
        value.Length >= 6 && (value[^6] is '+' or '-') && value[^3] == ':';

    private static bool Decimal(JsonElement value, out decimal result) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDecimal(out result),
        JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result),
        _ => SetFalse(out result)
    };
    private static bool SetFalse(out decimal result) { result = 0; return false; }
    private static bool Object(JsonElement value) => value.ValueKind == JsonValueKind.Object;
    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static JsonElement? Array(JsonElement value, string key) => value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.Array ? result : null;
    private static bool Integer(JsonElement value, string key, out int result) { result = 0; return value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out result) && result >= 0; }
    private static string Name(Truth value) => value switch { Truth.True => "true", Truth.False => "false", Truth.Unknown => "unknown", _ => "conflict" };
    private static JsonElement Unknown(string key, string reason) => JsonDocument.Parse(JsonSerializer.Serialize(new { key, description = reason, severity = "blocking", reason = "missing" })).RootElement.Clone();
    private static JsonElement OutcomeValue(JsonElement row) => row.GetProperty("outcome").TryGetProperty("value", out var value) ? value.Clone() : row.GetProperty("outcome").Clone();
    private static void AddEvidence(JsonElement row, List<JsonElement> evidence)
    {
        if (Array(row, "evidenceRefs") is { } refs) evidence.AddRange(refs.EnumerateArray().Select(item => item.Clone()));
    }
    private enum Truth { False, True, Unknown, Conflict }
    private readonly record struct OperandValue(bool Present, JsonElement Value, string? Type, string? Error);
}

public sealed record RuleEvaluation(string Status, IReadOnlyList<JsonElement> Outcomes,
    IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<JsonElement> Unknowns,
    IReadOnlyList<JsonElement> Evidence, IReadOnlyList<string> Errors);
