using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using AgentMcp.Contracts;
using AgentMcp.Infrastructure;
using Atlas.Api.Auth;
using BuildingBlocks.Commands;
using ContractDelivery.Contracts;
using ContractDelivery.Infrastructure;
using Governance.Contracts;
using Governance.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlatformOps;
using PlatformOps.Jobs;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using ReadSearchExplain.Contracts;
using ReadSearchExplain.Search;
using TaskCollab.Contracts;
using TaskCollab.Infrastructure;

namespace Atlas.Api.Mcp;

public sealed class AtlasMcpDispatcher(IServiceProvider services, IHttpContextAccessor httpContexts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<CallToolResult> InvokeAsync(AtlasToolDefinition tool, RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var requestId = Activity.Current?.Id ?? httpContexts.HttpContext?.TraceIdentifier ?? System.Guid.NewGuid().ToString("N");
        try
        {
            var user = request.User ?? new ClaimsPrincipal();
            RequireScope(user, tool.RequiredScope);
            var arguments = new Dictionary<string, JsonElement>(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
            McpInputValidator.Validate(tool.InputSchema, arguments);
            var actor = await user.ResolveAtlasPrincipalAsync(services, ct);
            var data = await Dispatch(tool.Name, actor, arguments, requestId, ct);
            var element = JsonSerializer.SerializeToElement(data, Json);
            return new CallToolResult
            {
                StructuredContent = element,
                Content = [new TextContentBlock { Text = element.GetRawText() }]
            };
        }
        catch (Exception error) when (error is ProjectIdentityException or AtlasCommandException or JsonException or UnauthorizedAccessException)
        {
            var (code, message) = error switch
            {
                ProjectIdentityException e => (e.Code, e.Message), AtlasCommandException e => (e.Code, e.Message),
                JsonException e => ("VALIDATION_FAILED", e.Message), _ => ("UNAUTHORIZED", error.Message)
            };
            var element = JsonSerializer.SerializeToElement(new { code, message, requestId }, Json);
            return new CallToolResult { IsError = true, StructuredContent = element, Content = [new TextContentBlock { Text = element.GetRawText() }] };
        }
    }

    private async Task<object> Dispatch(string name, AtlasPrincipal actor, IReadOnlyDictionary<string, JsonElement> a, string requestId, CancellationToken ct)
    {
        var project = name == "projects.list" ? System.Guid.Empty : Guid(a, "projectId");
        var key = Optional(a, "idempotencyKey") ?? string.Empty;
        var expected = IfMatch(a);
        var trace = Activity.Current?.TraceId.ToString();
        var body = Body(a);
        switch (name)
        {
            case "projects.list":
                return Envelope(requestId, await Projects.ListProjectsAsync(actor.PrincipalId, Optional(a, "cursor"), Limit(a), ct), trace);
            case "project.discover":
            {
                var read = await ReadContext(actor.PrincipalId, project, a, ct);
                return Envelope(requestId, await Reading.OverviewAsync(read, ct), trace, read);
            }
            case "vocabulary.resolve":
            {
                var input = Deserialize<VocabularyRequest>(body); var read = await Policy.CreateReadContextAsync(actor.PrincipalId, project, input.ReadContext, ct);
                return Envelope(requestId, await Search.ResolveVocabularyAsync(read, input.Term, input.ContextKey, ct), trace, read);
            }
            case "knowledge.search":
            {
                var read = await ReadContext(actor.PrincipalId, project, a, ct);
                return Envelope(requestId, await Search.SearchAsync(read, Required(a, "q"), Optional(a, "kind"), Optional(a, "cursor"), Limit(a), ct), trace, read);
            }
            case "knowledge.read":
            {
                var read = await ReadContext(actor.PrincipalId, project, a, ct);
                return Envelope(requestId, await Knowledge.GetUnitAsync(read, Guid(a, "unitId"), ct), trace, read);
            }
            case "knowledge.explain":
            {
                var input = Deserialize<ReadingPlanRequest>(body); var read = await Policy.CreateReadContextAsync(actor.PrincipalId, project, input.ReadContext, ct);
                return Envelope(requestId, await Reading.BuildAsync(read, input.RootUnitId, input.Profile, input.Cursor, ct), trace, read);
            }
            case "rule.evaluate":
            {
                var input = Deserialize<RuleEvaluationRequest>(body); var read = await Policy.CreateReadContextAsync(actor.PrincipalId, project, input.ReadContext, ct);
                return Envelope(requestId, await Knowledge.EvaluateRuleAsync(read, Guid(a, "unitId"), input.Facts, ct), trace, read);
            }
            case "task.context.build":
                return Envelope(requestId, await Delivery.BuildContextAsync(actor, project, Guid(a, "taskId"), Deserialize<BuildContextRequest>(body), expected, key, requestId, trace, ct), trace);
            case "task.context.get":
                return Envelope(requestId, await Delivery.GetContextAsync(actor.PrincipalId, project, Guid(a, "contextId"), ct), trace);
            case "task.create":
                return Envelope(requestId, await Tasks.CreateTaskAsync(actor, project, Deserialize<CreateTaskRequest>(body), key, requestId, trace, ct), trace);
            case "task.analyze_intent":
                return Envelope(requestId, await Tasks.AnalyzeIntentAsync(actor, project, Guid(a, "taskId"), Deserialize<AnalyzeIntentRequest>(body), expected, key, requestId, trace, ct), trace);
            case "content.review":
            {
                var input = Deserialize<ContentReviewRequest>(body); var read = await Policy.CreateReadContextAsync(actor.PrincipalId, project, input.ReadContext, ct);
                if (input.Revision.UnitId.HasValue) await Access.AuthorizeReadAsync(read, input.Revision.UnitId.Value, "read", null, ct);
                return Envelope(requestId, await Jobs.EnqueueAsync(project, actor.PrincipalId, new("knowledge.content_review", body), cancellationToken: ct), trace, read);
            }
            case "impact.find":
                return Envelope(requestId, await Tasks.FindImpactsAsync(actor, project, Guid(a, "taskId"), Deserialize<FindImpactsRequest>(body), expected, key, requestId, trace, ct), trace);
            case "impact.investigate":
                return Envelope(requestId, await Agents.InvestigateAsync(actor, project, Guid(a, "taskId"), Deserialize<InvestigateImpactRequest>(body), expected, key, requestId, trace, ct), trace);
            case "impact.submit":
                return Envelope(requestId, await Tasks.SubmitInvestigationAsync(actor, project, Guid(a, "taskId"), Deserialize<InvestigationInput>(body), expected, key, requestId, trace, ct), trace);
            case "impact.decide":
                return Envelope(requestId, await Tasks.DecideImpactAsync(actor, project, Guid(a, "taskId"), Guid(a, "impactId"), Deserialize<DecideImpactRequest>(body), expected, key, requestId, trace, ct), trace);
            case "change.plan":
                return Envelope(requestId, await Tasks.PlanChangeAsync(actor, project, Guid(a, "taskId"), Deserialize<PlanChangeRequest>(body), expected, key, requestId, trace, ct), trace);
            case "conformance.check":
                return Envelope(requestId, await Delivery.CheckConformanceAsync(actor.PrincipalId, project, Deserialize<ConformanceCheckRequest>(body), ct), trace);
            case "proposal.create":
                return Envelope(requestId, await Governance.SubmitProposalAsync(actor, project, Deserialize<SubmitProposalRequest>(body), key, requestId, trace, ct), trace);
            case "review.status":
                return Envelope(requestId, await Governance.GetChangeSetAsync(actor.PrincipalId, project, Guid(a, "changeSetId"), ct), trace);
            case "closure.check":
                return Envelope(requestId, await Delivery.CheckClosureAsync(actor.PrincipalId, project, Guid(a, "taskId"), Guid(a, "contextId"), ct), trace);
            case "delivery.list":
                return Envelope(requestId, await Delivery.ListDeliveriesAsync(actor.PrincipalId, project, Optional(a, "cursor"), Limit(a), ct), trace);
            case "delivery.reconcile":
                return Envelope(requestId, await Delivery.ReconcileAsync(actor, project, Deserialize<ReconcileRequest>(body), key, requestId, trace, ct), trace);
            case "feedback.create":
                return Envelope(requestId, await Tasks.CreateActivityAsync(actor, project, "feedback", Deserialize<ActivityInput>(body), key, requestId, trace, ct), trace);
            case "issues.create":
                return Envelope(requestId, await Tasks.CreateActivityAsync(actor, project, "issue", Deserialize<ActivityInput>(body), key, requestId, trace, ct), trace);
            case "runs.start":
                return Envelope(requestId, await Agents.StartRunAsync(actor, project, Deserialize<StartAgentRunRequest>(body), key, requestId, trace, ct), trace);
            case "runs.append_event":
                return Envelope(requestId, await Agents.AppendEventAsync(actor, project, Guid(a, "runId"), Deserialize<RunEventView>(body), key, requestId, trace, ct), trace);
            case "work.list":
                return Envelope(requestId, await Agents.ListWorkAsync(actor.PrincipalId, project, Optional(a, "cursor"), Limit(a), ct), trace);
            case "work.claim":
                return Envelope(requestId, await Agents.ClaimAsync(actor, project, Guid(a, "workId"), Deserialize<ClaimWorkRequest>(body), expected, key, requestId, trace, ct), trace);
            case "work.heartbeat":
                return Envelope(requestId, await Agents.HeartbeatAsync(actor, project, Guid(a, "workId"), Deserialize<HeartbeatWorkRequest>(body), key, requestId, trace, ct), trace);
            case "work.finish":
                return Envelope(requestId, await Agents.FinishAsync(actor, project, Guid(a, "workId"), Deserialize<FinishWorkRequest>(body), key, requestId, trace, ct), trace);
            case "jobs.get":
                return Envelope(requestId, await Jobs.GetAsync(project, actor.PrincipalId, Guid(a, "jobId"), ct), trace);
            case "jobs.result":
                return Envelope(requestId, await Jobs.GetResultAsync(project, actor.PrincipalId, Guid(a, "jobId"), ct), trace);
            case "jobs.cancel":
            {
                var input = Deserialize<JobControlRequest>(body);
                return Envelope(requestId, await Jobs.CancelAsync(project, actor.PrincipalId, Guid(a, "jobId"), expected, input.Reason, key, requestId, trace, ct), trace);
            }
            case "error_patterns.list":
                return Envelope(requestId, await Patterns.ListAsync(project, actor.PrincipalId, Optional(a, "cursor"), Limit(a), Optional(a, "kind"), Optional(a, "path"), ct), trace);
            default: throw ProjectIdentityException.Invalid("Unknown MCP tool.");
        }
    }

    private Task<ResolvedReadContext> ReadContext(Guid principal, Guid project, IReadOnlyDictionary<string, JsonElement> a, CancellationToken ct) =>
        Policy.CreateReadContextAsync(principal, project, new(Enum<ReadMode>(a, "mode", ReadMode.Published), Enum<ReadPurpose>(a, "purpose", ReadPurpose.Understand),
            OptionalGuid(a, "baselineId"), OptionalGuid(a, "changeSetId"), a.TryGetValue("context", out var context) ? context.Deserialize<ApplicableContext>(Json) : null), ct);
    private static object Envelope(string requestId, object data, string? trace, ResolvedReadContext? context = null) => new { code = "OK", requestId, traceId = trace, context, data };
    private static JsonElement Body(IReadOnlyDictionary<string, JsonElement> a) => a.TryGetValue("body", out var body) ? body : default;
    private static T Deserialize<T>(JsonElement element) where T : class => element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
        ? element.Deserialize<T>(Json) ?? throw ProjectIdentityException.Invalid("The tool body is required.") : throw ProjectIdentityException.Invalid("The tool body is required.");
    private static Guid Guid(IReadOnlyDictionary<string, JsonElement> a, string name) => System.Guid.Parse(Required(a, name));
    private static Guid? OptionalGuid(IReadOnlyDictionary<string, JsonElement> a, string name) => a.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null ? System.Guid.Parse(value.GetString()!) : null;
    private static string Required(IReadOnlyDictionary<string, JsonElement> a, string name) => a.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw ProjectIdentityException.Invalid($"{name} is required.");
    private static string? Optional(IReadOnlyDictionary<string, JsonElement> a, string name) => a.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Limit(IReadOnlyDictionary<string, JsonElement> a) => a.TryGetValue("limit", out var value) ? value.GetInt32() : 30;
    private static long IfMatch(IReadOnlyDictionary<string, JsonElement> a) => a.TryGetValue("ifMatch", out var value) && long.TryParse(value.GetString()?.Trim('"'), out var version) && version > 0 ? version : 0;
    private static T Enum<T>(IReadOnlyDictionary<string, JsonElement> a, string name, T fallback) where T : struct, Enum => Optional(a, name) is { } value && System.Enum.TryParse<T>(value, true, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : fallback;
    private static void RequireScope(ClaimsPrincipal user, string required)
    {
        if (user.Identity?.AuthenticationType != AtlasAuthentication.BearerScheme) throw new UnauthorizedAccessException("MCP requires an OAuth bearer token; browser cookies are not accepted.");
        var scopes = user.FindAll("scope").Concat(user.FindAll("scp")).SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains(required, StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden($"The token requires the {required} scope.");
    }
    private PostgresProjectIdentityStore Projects => services.GetRequiredService<PostgresProjectIdentityStore>();
    private PostgresTaskCollabStore Tasks => services.GetRequiredService<PostgresTaskCollabStore>();
    private PostgresAgentMcpStore Agents => services.GetRequiredService<PostgresAgentMcpStore>();
    private PostgresContractDeliveryStore Delivery => services.GetRequiredService<PostgresContractDeliveryStore>();
    private PostgresGovernanceStore Governance => services.GetRequiredService<PostgresGovernanceStore>();
    private PostgresKnowledgeStore Knowledge => services.GetRequiredService<PostgresKnowledgeStore>();
    private PostgresReadSearchService Search => services.GetRequiredService<PostgresReadSearchService>();
    private ReadingProjectionService Reading => services.GetRequiredService<ReadingProjectionService>();
    private PolicyService Policy => services.GetRequiredService<PolicyService>();
    private PostgresAccessApplicabilityStore Access => services.GetRequiredService<PostgresAccessApplicabilityStore>();
    private PostgresJobStore Jobs => services.GetRequiredService<PostgresJobStore>();
    private PostgresErrorPatternStore Patterns => services.GetRequiredService<PostgresErrorPatternStore>();
    private sealed record JobControlRequest(string Reason);
}
