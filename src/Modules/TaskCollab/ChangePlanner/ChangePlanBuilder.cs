using System.Text.Json;
using AccessApplicability.Contracts;
using TaskCollab.Contracts;
using TaskCollab.Investigation;

namespace TaskCollab.ChangePlanner;

/// <summary>Builds the auditable plan returned by CS-09 without changing approved knowledge.</summary>
public static class ChangePlanBuilder
{
    public static ChangePlan Build(Guid taskId, string classification, IReadOnlyList<TargetChange> targets,
        IReadOnlyList<RevisionRef> preserve, IReadOnlyList<ImpactView> impacts, IReadOnlyList<InvestigationCoverage> investigations)
    {
        var dependencyGroups = impacts.GroupBy(impact => impact.AssignedTaskId ?? taskId).Select(group => new ChangeDependencyGroup(
            group.Key,
            group.Select(impact => impact.ImpactId).ToArray(),
            group.Select(impact => impact.Disposition).Distinct().Order().ToArray(),
            group.Count() > 1 ? "atomic" : "single",
            group.Select(impact => impact.Reason).Distinct().ToArray())).ToArray();

        var businessDecisions = targets.Where(target => classification == "business_change" || target.Action is "revise" or "retire")
            .Select(target => new ChangeDecision(target.Description, "BusinessOwner", target.Reason,
                "Approval is established only by the executable context; this plan never grants approval.")).ToArray();

        var revalidations = impacts.Where(impact => impact.Disposition is "required" or "no_change" || impact.InspectionState == "stale")
            .Select(impact => new ChangeRevalidation(impact.ImpactId, impact.Target, impact.Disposition,
                impact.InspectionState == "stale" ? "Repeat inspection against current source before closure." : "Verify target behavior and preserved constraints with execution evidence.")).ToArray();

        var recommendations = DuplicateImplementationPaths(impacts).Select(group => new EngineeringRecommendation(
            "consolidate_duplicate_path",
            $"Multiple investigated impacts resolve to {group.Key}. Prefer one owned implementation path where behavior is equivalent.",
            "proposed",
            ["Confirm projection, null handling, authorization, ordering/pagination, errors, compatibility and side effects before removal."],
            group.Select(item => item.ImpactId).ToArray())).ToArray();

        return new(targets, preserve, dependencyGroups, businessDecisions, revalidations, recommendations, investigations);
    }

    private static IEnumerable<IGrouping<string, ImpactView>> DuplicateImplementationPaths(IReadOnlyList<ImpactView> impacts) => impacts
        .Where(impact => impact.InvestigationId.HasValue && impact.InspectionState is "inspected" or "verified")
        .SelectMany(impact => impact.Evidence.Select(evidence => (impact, evidence)))
        .Where(item => item.evidence.TryGetProperty("locator", out var locator) && locator.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
        .GroupBy(item => item.evidence.GetProperty("locator").GetProperty("path").GetString()!, item => item.impact)
        .Where(group => group.Select(item => item.ImpactId).Distinct().Count() > 1);
}

public sealed record ChangePlan(
    IReadOnlyList<TargetChange> TargetChanges,
    IReadOnlyList<RevisionRef> Preserve,
    IReadOnlyList<ChangeDependencyGroup> DependencyGroups,
    IReadOnlyList<ChangeDecision> BusinessDecisions,
    IReadOnlyList<ChangeRevalidation> Revalidations,
    IReadOnlyList<EngineeringRecommendation> EngineeringRecommendations,
    IReadOnlyList<InvestigationCoverage> InvestigationCoverage);

public sealed record ChangeDependencyGroup(Guid OwnerTaskId, IReadOnlyList<Guid> ImpactIds, IReadOnlyList<string> Dispositions, string Coordination, IReadOnlyList<string> Reasons);
public sealed record ChangeDecision(string Subject, string RequiredRole, string Reason, string Status);
public sealed record ChangeRevalidation(Guid ImpactId, UnitRef? Target, string Disposition, string RequiredEvidence);
public sealed record EngineeringRecommendation(string Kind, string Recommendation, string Status, IReadOnlyList<string> Preconditions, IReadOnlyList<Guid> ImpactIds);
public sealed record InvestigationCoverage(Guid InvestigationId, string CaptureKind, string Coverage, IReadOnlyList<string> Exclusions, bool Executed)
{
    public static InvestigationCoverage From(Guid id, string captureKind, string coverage, IReadOnlyList<string> exclusions) =>
        new(id, captureKind, coverage, exclusions, InvestigationEvidence.IsExecuted(captureKind));
}
