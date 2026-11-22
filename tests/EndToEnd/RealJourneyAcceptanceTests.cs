using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.EndToEnd.Tests;

public sealed class RealJourneyAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [Fact]
    public void AT_T43_Synthetic_material_and_missing_evidence_are_explicitly_blocked()
    {
        var input = Case("synthetic");
        input = input with { Results = input.Results.Select(item => item.Id == "AT-P01" ? item with { Status = AcceptanceStatus.Missing, Evidence = [] } : item).ToArray() };
        var report = RealJourneyAcceptance.Evaluate(input, new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("blocked", report.State);
        Assert.Contains(report.Blockers, item => item.Code == "real_business");
        Assert.Equal(23, report.Results.Count);
        Assert.Contains(report.Blockers, item => item is { Code: "journey_missing", Subject: "AT-P01" });
    }

    [Fact]
    public void AT_T43_Complete_attributable_case_passes_validation_without_claiming_real_world_execution()
    {
        var report = RealJourneyAcceptance.Evaluate(Case("authorized_real_business"));

        Assert.Equal("satisfied", report.State);
        Assert.Empty(report.Blockers);
        Assert.Equal(23, report.Results.Count);
    }

    [Fact]
    public void AT_T43_All_fourteen_cognitive_and_nine_journey_results_are_required()
    {
        var original = Case("authorized_real_business");
        var input = original with { Results = original.Results.Skip(1).Take(original.Results.Count - 2).ToArray() };

        var report = RealJourneyAcceptance.Evaluate(input);

        Assert.Equal("blocked", report.State);
        Assert.Contains(report.Blockers, item => item is { Code: "missing_result", Subject: "AT-P01" });
        Assert.Contains(report.Blockers, item => item is { Code: "missing_result", Subject: "J9" });
        Assert.Equal(RealJourneyAcceptance.RequiredIds, RealJourneyAcceptance.RequiredIds.Distinct().ToArray());
    }

    [Fact]
    public void AT_T43_Agent_cannot_create_its_own_answer_or_impersonate_human_approval()
    {
        var original = Case("authorized_real_business");
        var input = original with
        {
            ReferenceAnswer = original.ReferenceAnswer with { AuthorKind = "tested_agent", AgentStartedAt = original.ReferenceAnswer.CreatedAt },
            HumanDecisions = [original.HumanDecisions[0] with { ActorKind = "delegated_ai", Decision = "approved" }]
        };

        var report = RealJourneyAcceptance.Evaluate(input);

        Assert.Contains(report.Blockers, item => item.Code == "independent_reference");
        Assert.Contains(report.Blockers, item => item.Code == "blind_evaluation");
        Assert.Contains(report.Blockers, item => item.Code == "human_decision");
    }

    [Fact]
    public void AT_T43_Real_delivery_requires_human_knowledge_approval_and_second_self_change()
    {
        var original = Case("authorized_real_business");
        var input = original with
        {
            Delivery = original.Delivery with { FinalCommitSha = "future", PullRequest = Evidence("https://example.invalid/pr/1") },
            SelfProject = original.SelfProject with { NextTaskId = original.SelfProject.PreviousTaskId }
        };

        var report = RealJourneyAcceptance.Evaluate(input);

        Assert.Contains(report.Blockers, item => item.Code == "final_commit");
        Assert.Contains(report.Blockers, item => item.Code == "evidence_uri");
        Assert.Contains(report.Blockers, item => item.Code == "self_reuse");
    }

    [RealJourneyFact]
    public async Task AT_T43_Live_case_verifies_the_deployed_product_and_emits_an_auditable_report()
    {
        var path = Environment.GetEnvironmentVariable("ATLAS_T43_CASE")!;
        var input = JsonSerializer.Deserialize<RealAcceptanceCase>(await File.ReadAllTextAsync(path), JsonOptions)
            ?? throw new InvalidOperationException("The acceptance case is empty.");
        var blockers = RealJourneyAcceptance.Evaluate(input).Blockers.ToList();
        blockers.AddRange(await LiveAcceptanceApi.VerifyAsync(input,
            Environment.GetEnvironmentVariable("ATLAS_E2E_BASE_URL")!,
            Environment.GetEnvironmentVariable("ATLAS_E2E_ACCESS_TOKEN")!, CancellationToken.None));
        var report = RealJourneyAcceptance.Evaluate(input) with
        {
            State = blockers.Count == 0 ? "satisfied" : "blocked",
            Blockers = blockers
        };
        var output = Environment.GetEnvironmentVariable("ATLAS_T43_REPORT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        }
        Assert.True(blockers.Count == 0, string.Join(Environment.NewLine, blockers.Select(item => $"{item.Subject}: {item.Message}")));
    }

    private static RealAcceptanceCase Case(string mode)
    {
        var referenceTime = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var results = RealJourneyAcceptance.RequiredIds.Select(id => new JourneyResult(id, AcceptanceStatus.Satisfied,
            "Automated fixture deliberately used only to exercise evidence validation.", [Evidence("https://evidence.atlas.test/" + id)], ["PD-38"])).ToList();
        var decision = new HumanDecision("scope", "test-human", "human", "approved", ["PD-38"], Evidence("https://evidence.atlas.test/scope"));
        return new("1.0", "AT-T43-VALIDATOR-FIXTURE", mode, "test",
            Evidence("https://evidence.atlas.test/authorization"), [Evidence("https://evidence.atlas.test/source")],
            new IndependentReference("reference", "independent-reviewer", "human", referenceTime, referenceTime.AddMinutes(1), Evidence("https://evidence.atlas.test/reference")),
            results, [decision],
            new ModelEvaluation("test-provider", "test-model", "1", "run-1", referenceTime, referenceTime.AddSeconds(1), 10, 10, 0.1m, "USD", Evidence("https://evidence.atlas.test/model")),
            new PilotComparison(Trial(), Trial(), Evidence("https://evidence.atlas.test/comparison")),
            new SelfProjectEvidence("BusinessAtlas-Self", "T38", "T43", Evidence("https://evidence.atlas.test/self-base"),
                Evidence("https://evidence.atlas.test/self-change"), Evidence("https://evidence.atlas.test/self-context"), Evidence("https://evidence.atlas.test/self-publication")),
            new DeliveryEvidence("https://dev.azure.com/atlas/project/_git/repo/pullrequest/1", new string('a', 40),
                Evidence("https://evidence.atlas.test/pr"), Evidence("https://evidence.atlas.test/ci"), Evidence("https://evidence.atlas.test/approval"),
                Evidence("https://evidence.atlas.test/baseline"), Evidence("https://evidence.atlas.test/deploy"), Evidence("https://evidence.atlas.test/retry")), []);
    }

    private static TrialObservation Trial() => new(1, 1, 0, 0, 0, 0, 1, 1, 0.1m, "USD", Evidence("https://evidence.atlas.test/trial"));
    private static EvidenceRef Evidence(string uri) => new(uri, new string('b', 64), new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero), "automated-test-actor", "test");
}

public sealed class RealJourneyFactAttribute : FactAttribute
{
    public RealJourneyFactAttribute()
    {
        var missing = new[] { "ATLAS_T43_CASE", "ATLAS_E2E_BASE_URL", "ATLAS_E2E_ACCESS_TOKEN" }
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
        if (missing.Length > 0)
            Skip = "Real T43 evidence is not configured; missing " + string.Join(", ", missing) + ". This is an explicit acceptance blocker, not a synthetic pass.";
    }
}
