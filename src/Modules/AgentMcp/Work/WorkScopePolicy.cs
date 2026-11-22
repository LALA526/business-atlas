using System.Text.Json;
using AgentMcp.Contracts;
using ProjectIdentity.Application;

namespace AgentMcp.Work;

public static class WorkScopePolicy
{
    private static readonly HashSet<string> Kinds = ["investigate", "review", "implement", "verify"];
    private static readonly HashSet<string> Capabilities = ["read_file", "search", "git", "test", "document"];

    public static int ValidateCreate(CreateWorkRequest request)
    {
        if (request.TaskId == Guid.Empty || request.ContextId == Guid.Empty || !Kinds.Contains(request.Kind) ||
            request.Scope.ValueKind != JsonValueKind.Object || request.Budget.ValueKind != JsonValueKind.Object ||
            request.Deadline.HasValue && request.Deadline <= DateTimeOffset.UtcNow)
            throw ProjectIdentityException.Invalid("A valid task, executable context, work kind, scope, budget and future deadline are required.");
        RejectSecrets(request.Scope); RejectSecrets(request.Budget);
        var capabilities = StringArray(request.Scope, "tools", true);
        if (capabilities.Any(tool => !Capabilities.Contains(tool))) throw ProjectIdentityException.Invalid("Only bounded repository read, search, git, test and document capabilities may be requested.");
        if (request.Kind == "investigate")
        {
            var repo = String(request.Scope, "repo");
            var commit = String(request.Scope, "commitSha");
            var directories = StringArray(request.Scope, "allowedDirectories", true);
            if (string.IsNullOrWhiteSpace(repo) || repo.Length > 500 || commit is null || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
                throw ProjectIdentityException.Invalid("Repository investigation requires an exact repository and lowercase 40-character commit.");
            foreach (var path in directories) ValidateRelativePath(path);
        }
        return MaxAttempts(request.Budget);
    }

    public static void ValidateCapabilities(CreateWorkRequest request, IReadOnlyList<string> claimed)
    {
        if (claimed.Count == 0 || claimed.Count > 20 || claimed.Any(string.IsNullOrWhiteSpace) || claimed.Distinct(StringComparer.Ordinal).Count() != claimed.Count)
            throw ProjectIdentityException.Invalid("Executor capabilities must be a non-empty distinct list.");
        var required = StringArray(request.Scope, "tools", true);
        if (!required.All(claimed.Contains)) throw ProjectIdentityException.Forbidden("The executor does not provide every capability required by this work request.");
    }

    public static int MaxAttempts(JsonElement budget)
    {
        if (!budget.TryGetProperty("maxAttempts", out var value)) return 3;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var attempts) || attempts is < 1 or > 10)
            throw ProjectIdentityException.Invalid("budget.maxAttempts must be between 1 and 10.");
        return attempts;
    }

    private static void RejectSecrets(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("environment", StringComparison.OrdinalIgnoreCase))
                    throw ProjectIdentityException.Invalid("Work scope and budget must not contain credentials or process environment.");
                RejectSecrets(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectSecrets(item);
    }

    private static void ValidateRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000 || normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) || normalized == ".." || normalized.Contains('\0'))
            throw ProjectIdentityException.Invalid("Repository paths must be bounded relative paths without traversal.");
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string[] StringArray(JsonElement value, string name, bool required)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            if (required) throw ProjectIdentityException.Invalid($"scope.{name} is required.");
            return [];
        }
        if (property.ValueKind != JsonValueKind.Array) throw ProjectIdentityException.Invalid($"scope.{name} must be an array.");
        var result = property.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        if (result.Length == 0 || result.Length > 50 || result.Any(string.IsNullOrWhiteSpace)) throw ProjectIdentityException.Invalid($"scope.{name} must contain bounded non-empty strings.");
        return result!;
    }
}
