namespace ProjectIdentity.Application;

public sealed class ProjectIdentityException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static ProjectIdentityException NotFound() =>
        new("NOT_FOUND", "The requested project or membership was not found.", 404);

    public static ProjectIdentityException Forbidden() =>
        new("FORBIDDEN", "The current principal cannot perform this operation.", 403);

    public static ProjectIdentityException Conflict(string message) =>
        new("CONFLICT", message, 409);

    public static ProjectIdentityException CasMismatch() =>
        new("CAS_MISMATCH", "The project policy version has changed; reload and retry.", 412);

    public static ProjectIdentityException Invalid(string message) =>
        new("VALIDATION_FAILED", message, 422);
}
