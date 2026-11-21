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

        var evaluated = new List<(JsonElement Row, Truth Truth)>();
        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeCount = 0;
        foreach (var row in rows.Value.EnumerateArray())
        {
            var rowId = String(row, "rowId");
            if (rowId is null || !rowIds.Add(rowId) || !row.TryGetProperty("outcome", out var outcome) || !Object(outcome))
                return NotExecutable(conditions, unknowns, "Every decision row requires a unique rowId and structured outcome.");
            if (rowId is null || !row.TryGetProperty("when", out var expression))
            {
                evaluated.Add((row, Truth.Unknown));
                conditions.Add(new(rowId ?? "unknown", "unknown", "The row has no stable rowId or executable condition."));
                continue;
            }
            var result = EvaluateExpression(expression, facts, 1, ref nodeCount);
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

    private static (Truth Value, string Reason) EvaluateExpression(JsonElement expression, JsonElement facts, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes || depth > MaxDepth) return (Truth.Unknown, "The expression exceeds AST v1 resource limits.");
        if (!Object(expression) || String(expression, "op") is not { } op || !Operators.Contains(op)) return (Truth.Unknown, "The expression operator is missing or unsupported.");
        if (op == "all" || op == "any")
        {
            if (Array(expression, "args") is not { } args || args.GetArrayLength() == 0) return (Truth.Unknown, $"{op} requires at least one argument.");
            var values = new List<(Truth Value, string Reason)>();
            foreach (var item in args.EnumerateArray()) values.Add(EvaluateExpression(item, facts, depth + 1, ref nodes));
            if (values.Any(item => item.Value == Truth.Conflict)) return (Truth.Conflict, "An argument contains a conflict.");
            if (op == "all" && values.Any(item => item.Value == Truth.False)) return (Truth.False, "At least one all argument is false.");
            if (op == "any" && values.Any(item => item.Value == Truth.True)) return (Truth.True, "At least one any argument is true.");
            if (values.Any(item => item.Value == Truth.Unknown)) return (Truth.Unknown, "An argument is unknown.");
            return op == "all" ? (Truth.True, "All arguments are true.") : (Truth.False, "All any arguments are false.");
        }
        if (op == "not")
        {
            if (!expression.TryGetProperty("arg", out var arg)) return (Truth.Unknown, "not requires an argument.");
            var value = EvaluateExpression(arg, facts, depth + 1, ref nodes);
            return value.Value switch { Truth.True => (Truth.False, "The argument is true."), Truth.False => (Truth.True, "The argument is false."), _ => value };
        }
        if (op is "exists" or "missing" or "isNull")
        {
            if (!expression.TryGetProperty("operand", out var operand)) return (Truth.Unknown, $"{op} requires an operand.");
            var value = ReadOperand(operand, facts);
            if (value.Error is not null) return (Truth.Unknown, value.Error);
            var result = op == "exists" ? value.Present : op == "missing" ? !value.Present : value.Present && value.Value.ValueKind == JsonValueKind.Null;
            return (result ? Truth.True : Truth.False, $"{op} evaluated deterministically.");
        }

        if (!expression.TryGetProperty("left", out var leftNode) || !expression.TryGetProperty("right", out var rightNode)) return (Truth.Unknown, "A comparison requires left and right operands.");
        var left = ReadOperand(leftNode, facts); var right = ReadOperand(rightNode, facts);
        if (left.Error is not null || right.Error is not null) return (Truth.Unknown, left.Error ?? right.Error!);
        if (!left.Present || !right.Present) return (Truth.Unknown, "A required fact is missing.");
        if (left.Value.ValueKind == JsonValueKind.Null || right.Value.ValueKind == JsonValueKind.Null) return (Truth.Unknown, "Null is not an implicit comparison value.");
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
            if (op.ValueKind != JsonValueKind.Array && other.ValueKind == JsonValueKind.Array)
                return other.EnumerateArray().Any(item => CanonicalJson.Serialize(item) == CanonicalJson.Serialize(op)) ? (0, null) : (1, null);
            return (1, "List comparisons require a scalar left operand and a list right operand.");
        }
        if (type is "decimal" or "integer" || op.ValueKind == JsonValueKind.Number || other.ValueKind == JsonValueKind.Number)
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
        if (type == "date" && (!DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dx) || !DateOnly.TryParseExact(y, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dy))) return (1, "Dates must use ISO yyyy-MM-dd.");
        if (type == "datetime" && (!DateTimeOffset.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtx) || !DateTimeOffset.TryParse(y, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dty))) return (1, "Datetimes must be ISO 8601 values with an explicit offset.");
        var stringComparison = comparison == "ordinalIgnoreCase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (string.Compare(x, y, stringComparison), null);
    }

    private static OperandValue ReadOperand(JsonElement node, JsonElement facts)
    {
        if (!Object(node)) return new(false, default, null, "The operand is not an object.");
        if (String(node, "kind") == "literal") return new(true, node.TryGetProperty("value", out var value) ? value.Clone() : default, String(node, "valueType"), null);
        if (String(node, "kind") != "field" || !Guid.TryParse(String(node, "attributeId"), out var id)) return new(false, default, null, "A field operand requires a valid attributeId.");
        if (!facts.TryGetProperty(id.ToString(), out var fact) && !facts.TryGetProperty(id.ToString("D"), out fact)) return new(false, default, null, null);
        return new(true, fact.Clone(), null, null);
    }

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
