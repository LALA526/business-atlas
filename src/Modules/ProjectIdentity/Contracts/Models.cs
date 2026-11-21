namespace ProjectIdentity.Contracts;

public enum PrincipalKind { Human, Agent, Service }

public sealed record AtlasPrincipal(
    Guid PrincipalId,
    string Issuer,
    string Subject,
    PrincipalKind Kind,
    bool Active);

public sealed record ProjectSummary(
    Guid ProjectId,
    string Slug,
    string DisplayName,
    long Version,
    long PolicyEpoch,
    Guid CurrentBaselineId);

public sealed record ProjectPage(IReadOnlyList<ProjectSummary> Items, string? NextCursor);

public sealed record Membership(
    Guid PrincipalId,
    IReadOnlyList<string> Roles,
    int Clearance,
    bool Active,
    long Version);

public sealed record MembershipPage(IReadOnlyList<Membership> Items, string? NextCursor);

public sealed record CreateProjectCommand(string DisplayName, string Slug, string IdempotencyKey);
public sealed record UpdateProjectCommand(string DisplayName, long ExpectedVersion, string IdempotencyKey);
public sealed record ChangeMembershipCommand(
    Guid PrincipalId,
    IReadOnlyList<string> Roles,
    int Clearance,
    bool Active,
    long ExpectedVersion,
    string IdempotencyKey);

public sealed record AuthorizedReadContext(
    Guid ProjectId,
    Guid PrincipalId,
    Guid BaselineId,
    long PolicyEpoch,
    IReadOnlyList<string> Roles,
    int Clearance);
