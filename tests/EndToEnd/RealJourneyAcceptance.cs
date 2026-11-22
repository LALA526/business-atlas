using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.EndToEnd.Tests;

public enum AcceptanceStatus
{
    Satisfied,
    Missing,
    Contradictory,
    Unverified,
    NotApplicable
}

public sealed record EvidenceRef(string Uri, string Sha256, DateTimeOffset CapturedAt, string ActorId, string ActorKind);
public sealed record JourneyResult(string Id, AcceptanceStatus Status, string Reason, IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<string> RequirementIds, string? NotApplicableDecisionId = null);
public sealed record IndependentReference(string ReferenceId, string AuthorId, string AuthorKind, DateTimeOffset CreatedAt,
    DateTimeOffset AgentStartedAt, EvidenceRef Evidence);
public sealed record HumanDecision(string DecisionId, string ActorId, string ActorKind, string Decision,
    IReadOnlyList<string> Covers, EvidenceRef Evidence);
public sealed record TrialObservation(int ApplicableCases, int CorrectCriticalJudgements, int FalseAffirmations,
    int CriticalOmissions, int FalsePositives, int ExplicitUnknowns, int ReviewRounds, double HumanMinutes,
    decimal? ModelCost, string Currency, EvidenceRef Evidence);
public sealed record PilotComparison(TrialObservation OriginalWorkflow, TrialObservation AtlasWorkflow, EvidenceRef Evidence);
public sealed record ModelEvaluation(string Provider, string Model, string ModelVersion, string RunId,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, long? InputTokens, long? OutputTokens, decimal? Cost,
    string Currency, EvidenceRef Evidence);
public sealed record SelfProjectEvidence(string ProjectId, string PreviousTaskId, string NextTaskId,
    EvidenceRef ApprovedInitialBaseline, EvidenceRef SecondChange, EvidenceRef NextTaskContext, EvidenceRef SecondChangePublication);
public sealed record DeliveryEvidence(string PullRequestUri, string FinalCommitSha, EvidenceRef PullRequest,
    EvidenceRef CiRun, EvidenceRef HumanKnowledgeApproval, EvidenceRef PublishedBaseline, EvidenceRef Deployment,
    EvidenceRef RetryWithoutDuplicate);
public sealed record LiveProbe(string Name, string Path, int ExpectedStatus, string? JsonPointer = null, string? ExpectedValue = null);
public sealed record RealAcceptanceCase(string SchemaVersion, string CaseId, string BusinessMode, string Environment,
    EvidenceRef BusinessAuthorization, IReadOnlyList<EvidenceRef> SourceMaterials, IndependentReference ReferenceAnswer,
    IReadOnlyList<JourneyResult> Results, IReadOnlyList<HumanDecision> HumanDecisions, ModelEvaluation ModelEvaluation,
    PilotComparison PilotComparison, SelfProjectEvidence SelfProject, DeliveryEvidence Delivery,
    IReadOnlyList<LiveProbe> LiveProbes);
public sealed record AcceptanceBlocker(string Code, string Subject, string Message);
public sealed record AcceptanceReport(string CaseId, string State, IReadOnlyList<AcceptanceBlocker> Blockers,
    IReadOnlyDictionary<string, AcceptanceStatus> Results, DateTimeOffset EvaluatedAt);

/// <summary>
/// AT-T43 evidence evaluation. This deliberately has no synthetic success fixture:
/// every real-business claim needs independently produced, attributable evidence.
/// </summary>
public static class RealJourneyAcceptance
{
    public static readonly IReadOnlyList<string> RequiredIds =
        Enumerable.Range(1, 14).Select(i => $"AT-P{i:00}").Concat(Enumerable.Range(1, 9).Select(i => $"J{i}")).ToArray();

    private static readonly HashSet<string> RealBusinessModes = new(["real_cpq", "authorized_real_business"], StringComparer.Ordinal);
    private static readonly HashSet<string> HumanKinds = new(["human"], StringComparer.Ordinal);
    private static readonly HashSet<string> ReferenceKinds = new(["human", "independent_agent"], StringComparer.Ordinal);
    private static readonly HashSet<string> PlaceholderHosts = new(["example.invalid", "example.com", "localhost"], StringComparer.OrdinalIgnoreCase);

    public static AcceptanceReport Evaluate(RealAcceptanceCase input, DateTimeOffset? now = null)
    {
        var blockers = new List<AcceptanceBlocker>();
        void Block(string code, string subject, string message) => blockers.Add(new(code, subject, message));
        if (input.SchemaVersion != "1.0") Block("schema", input.CaseId, "Only acceptance case schema 1.0 is supported.");
        if (!RealBusinessModes.Contains(input.BusinessMode)) Block("real_business", input.CaseId, "Synthetic or demo material cannot satisfy real-business acceptance.");
        if (string.IsNullOrWhiteSpace(input.Environment)) Block("environment", input.CaseId, "The actual execution environment must be identified.");
        CheckEvidence(input.BusinessAuthorization, "business_authorization", blockers);
        if (input.SourceMaterials.Count == 0) Block("real_materials", input.CaseId, "Authorized real source materials are required.");
        foreach (var source in input.SourceMaterials) CheckEvidence(source, "source_material", blockers);

        var reference = input.ReferenceAnswer;
        if (!ReferenceKinds.Contains(reference.AuthorKind)) Block("independent_reference", reference.ReferenceId, "The reference answer must be authored by a human domain reviewer or an explicitly independent agent.");
        if (reference.CreatedAt >= reference.AgentStartedAt) Block("blind_evaluation", reference.ReferenceId, "The independent reference must predate the evaluated agent run.");
        CheckEvidence(reference.Evidence, "independent_reference", blockers);

        var results = input.Results.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var required in RequiredIds)
        {
            if (!results.TryGetValue(required, out var matches))
            {
                Block("missing_result", required, "No journey result was recorded.");
                continue;
            }
            if (matches.Length != 1) Block("duplicate_result", required, "A journey must have exactly one attributable result.");
            var result = matches[0];
            if (string.IsNullOrWhiteSpace(result.Reason)) Block("missing_reason", required, "A reason is required for every status.");
            if (result.RequirementIds.Count == 0) Block("missing_trace", required, "The result must identify the business/product requirements it verifies.");
            if (result.Status == AcceptanceStatus.Satisfied && result.Evidence.Count == 0) Block("missing_evidence", required, "A satisfied result requires evidence.");
            if (result.Status == AcceptanceStatus.NotApplicable && string.IsNullOrWhiteSpace(result.NotApplicableDecisionId)) Block("not_applicable_decision", required, "Not-applicable requires an explicit human scope decision.");
            foreach (var evidence in result.Evidence) CheckEvidence(evidence, required, blockers);
            if (result.Status is AcceptanceStatus.Missing or AcceptanceStatus.Contradictory or AcceptanceStatus.Unverified)
                Block("journey_" + result.Status.ToString().ToLowerInvariant(), required, result.Reason);
        }
        foreach (var unexpected in results.Keys.Except(RequiredIds, StringComparer.Ordinal)) Block("unknown_result", unexpected, "Unknown acceptance identifiers are not allowed.");

        var decisionGroups = input.HumanDecisions.GroupBy(item => item.DecisionId, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in decisionGroups.Where(group => group.Count() != 1))
            Block("duplicate_decision", duplicate.Key, "A human decision ID must be unique.");
        var decisions = decisionGroups.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var decision in input.HumanDecisions)
        {
            if (!HumanKinds.Contains(decision.ActorKind)) Block("human_decision", decision.DecisionId, "Automated or delegated AI actors must not be labelled as human approvers.");
            if (decision.Decision != "approved") Block("human_decision", decision.DecisionId, "Required human scope/business approval has not been given.");
            if (decision.Covers.Count == 0) Block("decision_scope", decision.DecisionId, "The approved scope must be explicit.");
            CheckEvidence(decision.Evidence, decision.DecisionId, blockers);
        }
        foreach (var result in input.Results.Where(item => item.Status == AcceptanceStatus.NotApplicable))
            if (result.NotApplicableDecisionId is null || !decisions.ContainsKey(result.NotApplicableDecisionId))
                Block("not_applicable_decision", result.Id, "The referenced human decision does not exist.");

        var model = input.ModelEvaluation;
        if (string.IsNullOrWhiteSpace(model.Provider) || string.IsNullOrWhiteSpace(model.Model) || string.IsNullOrWhiteSpace(model.ModelVersion) || string.IsNullOrWhiteSpace(model.RunId))
            Block("real_model", model.RunId, "Provider, exact model/version and real run ID are required.");
        if (model.CompletedAt <= model.StartedAt) Block("real_model", model.RunId, "The model completion must follow its start.");
        if (model.InputTokens < 0 || model.OutputTokens < 0 || model.Cost < 0) Block("model_metrics", model.RunId, "Token and cost measurements cannot be negative; use null when unknown.");
        CheckEvidence(model.Evidence, "real_model", blockers);

        CheckTrial(input.PilotComparison.OriginalWorkflow, "original_workflow", blockers);
        CheckTrial(input.PilotComparison.AtlasWorkflow, "atlas_workflow", blockers);
        CheckEvidence(input.PilotComparison.Evidence, "pilot_comparison", blockers);

        var self = input.SelfProject;
        if (self.ProjectId != "BusinessAtlas-Self") Block("self_identity", self.ProjectId, "The authorized Business Atlas Self case must use its actual project identity.");
        if (self.PreviousTaskId == self.NextTaskId) Block("self_reuse", self.NextTaskId, "A later task must demonstrate reuse of the previously approved knowledge.");
        CheckEvidence(self.ApprovedInitialBaseline, "self_initial_baseline", blockers);
        CheckEvidence(self.SecondChange, "self_second_change", blockers);
        CheckEvidence(self.NextTaskContext, "self_next_task_context", blockers);
        CheckEvidence(self.SecondChangePublication, "self_second_publication", blockers);

        var delivery = input.Delivery;
        if (!Uri.TryCreate(delivery.PullRequestUri, UriKind.Absolute, out var pullRequest) || pullRequest.Scheme != Uri.UriSchemeHttps)
            Block("real_pull_request", delivery.PullRequestUri, "A real HTTPS pull-request URL is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(delivery.FinalCommitSha, "^[0-9a-f]{40}$"))
            Block("final_commit", delivery.FinalCommitSha, "The merged final commit must be a full lowercase Git SHA; a future candidate SHA is not accepted.");
        foreach (var evidence in new[] { delivery.PullRequest, delivery.CiRun, delivery.HumanKnowledgeApproval, delivery.PublishedBaseline, delivery.Deployment, delivery.RetryWithoutDuplicate })
            CheckEvidence(evidence, "delivery", blockers);

        var status = results.ToDictionary(group => group.Key, group => group.Value[0].Status, StringComparer.Ordinal);
        return new(input.CaseId, blockers.Count == 0 ? "satisfied" : "blocked", blockers, status, now ?? DateTimeOffset.UtcNow);
    }

    private static void CheckTrial(TrialObservation trial, string subject, List<AcceptanceBlocker> blockers)
    {
        if (trial.ApplicableCases <= 0 || trial.CorrectCriticalJudgements > trial.ApplicableCases ||
            new[] { trial.CorrectCriticalJudgements, trial.FalseAffirmations, trial.CriticalOmissions, trial.FalsePositives, trial.ExplicitUnknowns, trial.ReviewRounds }.Any(value => value < 0) ||
            trial.HumanMinutes < 0 || trial.ModelCost < 0)
            blockers.Add(new("pilot_metrics", subject, "Measured counts, durations and costs must be non-negative and correct judgements cannot exceed applicable cases; unknown cost is null."));
        CheckEvidence(trial.Evidence, subject, blockers);
    }

    private static void CheckEvidence(EvidenceRef evidence, string subject, List<AcceptanceBlocker> blockers)
    {
        if (!Uri.TryCreate(evidence.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "urn") || PlaceholderHosts.Contains(uri.Host))
            blockers.Add(new("evidence_uri", subject, "Evidence must use a real HTTPS resource or a URN; placeholder/local URLs are not acceptance evidence."));
        if (!System.Text.RegularExpressions.Regex.IsMatch(evidence.Sha256, "^[0-9a-f]{64}$"))
            blockers.Add(new("evidence_digest", subject, "Evidence must bind the exact captured bytes with a lowercase SHA-256 digest."));
        if (evidence.CapturedAt == default || string.IsNullOrWhiteSpace(evidence.ActorId) || string.IsNullOrWhiteSpace(evidence.ActorKind))
            blockers.Add(new("evidence_attribution", subject, "Capture time, actor ID and actor kind are required."));
    }
}

public static class LiveAcceptanceApi
{
    public static async Task<IReadOnlyList<AcceptanceBlocker>> VerifyAsync(RealAcceptanceCase input, string baseUrl,
        string bearerToken, CancellationToken cancellationToken)
    {
        var blockers = new List<AcceptanceBlocker>();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            return [new("live_base_url", baseUrl, "The live API must use HTTPS (except a loopback development server).")];
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        var probes = new[] { new LiveProbe("readiness", "/health/ready", 200, "/data/status", "ok") }.Concat(input.LiveProbes);
        foreach (var probe in probes)
        {
            try
            {
                if (!probe.Path.StartsWith('/') || probe.Path.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException("Probe paths must be same-origin absolute paths.");
                using var response = await client.GetAsync(probe.Path, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if ((int)response.StatusCode != probe.ExpectedStatus)
                {
                    blockers.Add(new("live_http", probe.Name, $"Expected HTTP {probe.ExpectedStatus}, received {(int)response.StatusCode}."));
                    continue;
                }
                if (probe.JsonPointer is not null && !MatchesPointer(body, probe.JsonPointer, probe.ExpectedValue))
                    blockers.Add(new("live_body", probe.Name, $"Response did not match {probe.JsonPointer}."));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                blockers.Add(new("live_unavailable", probe.Name, error.Message));
            }
        }
        return blockers;
    }

    private static bool MatchesPointer(string body, string pointer, string? expected)
    {
        using var document = JsonDocument.Parse(body);
        var current = document.RootElement;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(key, out var property)) current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength()) current = current[index];
            else return false;
        }
        return expected is null || string.Equals(current.ToString(), expected, StringComparison.Ordinal);
    }
}
