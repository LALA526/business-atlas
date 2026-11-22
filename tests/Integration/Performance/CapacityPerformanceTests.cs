using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlatformOps.Jobs;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using ReadSearchExplain.Search;

namespace Atlas.Integration.Tests.Performance;

public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATLAS_T42_PERFORMANCE") != "1") Skip = "Set ATLAS_T42_PERFORMANCE=1 for the real PostgreSQL performance run.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"))) Skip = "ATLAS_TEST_CONNECTIONSTRING is required.";
    }
}

public sealed class CapacityPerformanceTests
{
    [PerformanceFact]
    public async Task AT_T42_Real_PostgreSQL_capacity_latency_concurrency_and_Mac_resources()
    {
        var full = Environment.GetEnvironmentVariable("ATLAS_T42_PERFORMANCE_FULL") == "1";
        var units = Setting("ATLAS_T42_UNITS", full ? 10_000 : 1_000);
        var relations = Setting("ATLAS_T42_RELATIONS", full ? 100_000 : 2_000);
        var baselines = Setting("ATLAS_T42_BASELINES", full ? 100 : 3);
        var samples = Setting("ATLAS_T42_SAMPLES", full ? 40 : 15);
        if (full && (units < 10_000 || relations < 100_000 || baselines < 100)) throw new InvalidOperationException("The full T42 target cannot be reduced.");
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var reuseDatabase = Environment.GetEnvironmentVariable("ATLAS_T42_REUSE_DATABASE");
        if (reuseDatabase is not null && !Regex.IsMatch(reuseDatabase, "^atlas_t42_perf_[0-9a-f]{32}$"))
            throw new InvalidOperationException("The reusable performance database name is invalid.");
        var prepareOnly = Environment.GetEnvironmentVariable("ATLAS_T42_PREPARE_ONLY") == "1";
        if (prepareOnly && reuseDatabase is not null) throw new InvalidOperationException("Prepare-only and reuse modes are mutually exclusive.");
        var database = reuseDatabase ?? $"atlas_t42_perf_{Guid.NewGuid():N}";
        if (reuseDatabase is null)
        {
            await using var connection = new NpgsqlConnection(administrator.ConnectionString);
            await connection.OpenAsync(); await Execute(connection, $"CREATE DATABASE {database}");
        }
        administrator.Database = database;
        try
        {
            var before = HostResources.Capture();
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            if (reuseDatabase is null)
            {
                await using var context = new BuildingBlocks.Persistence.AtlasDbContext(options);
                await context.Database.MigrateAsync();
            }
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app", MaxPoolSize = 40 }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime); var access = new PostgresAccessApplicabilityStore(runtime);
            var knowledge = new PostgresKnowledgeStore(runtime, access); var search = new PostgresReadSearchService(runtime, knowledge, access, "t42-performance-cursor-key");
            var reading = new ReadingProjectionService(knowledge, search);
            var owner = await projects.ResolvePrincipalAsync("https://identity.test/performance", "t42-performance-human", PrincipalKind.Human, CancellationToken.None);
            ProjectSummary project;
            if (reuseDatabase is null)
            {
                project = await projects.CreateProjectAsync(owner, new("T42 performance", $"t42-perf-{Guid.NewGuid():N}", "t42-performance-create"), "t42-r1", null, CancellationToken.None);
                await Seed(administrator.ConnectionString, project.ProjectId, project.CurrentBaselineId, owner.PrincipalId, units, relations, baselines);
            }
            else
            {
                var projectId = await Scalar<Guid>(administrator.ConnectionString, "SELECT project_id FROM identity.project ORDER BY created_at LIMIT 1");
                project = await projects.GetProjectAsync(owner.PrincipalId, projectId, CancellationToken.None);
            }
            if (prepareOnly)
            {
                Console.WriteLine($"AT-T42 full fixture prepared for a separate measurement run: {database}");
                return;
            }
            var humanContext = await access.CreateReadContextAsync(owner.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            var agents = new List<ResolvedReadContext>();
            for (var index = 0; index < 2; index++)
            {
                var agent = await projects.ResolvePrincipalAsync(Issuer(humanContext), $"t42-performance-agent-{index}", PrincipalKind.Service, CancellationToken.None);
                await AddAgent(administrator.ConnectionString, project.ProjectId, agent.PrincipalId);
                agents.Add(await access.CreateReadContextAsync(agent.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None));
            }
            var root = await Scalar<Guid>(administrator.ConnectionString, "SELECT unit_id FROM core.unit WHERE project_id=@project AND kind='BusinessRule' ORDER BY unit_id LIMIT 1", project.ProjectId);
            var cold = new Dictionary<string, double>
            {
                ["ordinaryQuery"] = await Measure(() => knowledge.ListUnitsAsync(humanContext, null, null, null, 30, CancellationToken.None)),
                ["search"] = await Measure(() => search.SearchAsync(humanContext, "Performance term 42", null, null, 30, CancellationToken.None)),
                ["boundedReading"] = await Measure(() => reading.BuildAsync(humanContext, root, null, null, CancellationToken.None))
            };
            var ordinary = await Samples(samples, () => knowledge.ListUnitsAsync(humanContext, null, null, null, 30, CancellationToken.None));
            var searches = await Samples(samples, () => search.SearchAsync(humanContext, "Performance term 42", null, null, 30, CancellationToken.None));
            var readings = await Samples(samples, () => reading.BuildAsync(humanContext, root, null, null, CancellationToken.None));
            var contexts = Enumerable.Range(0, 5).Select(_ => humanContext).Concat(agents).ToArray();
            var concurrentErrors = new List<Exception>();
            var concurrent = await Samples(10, async () =>
            {
                try
                {
                    var work = contexts.Select(async (context, index) =>
                    {
                        if (index % 2 == 0) await knowledge.ListUnitsAsync(context, null, null, null, 30, CancellationToken.None);
                        else await search.SearchAsync(context, "Performance term 42", null, null, 30, CancellationToken.None);
                    });
                    await Task.WhenAll(work);
                }
                catch (Exception error) { lock (concurrentErrors) concurrentErrors.Add(error); throw; }
            });
            var jobOptions = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(runtime).Options;
            var submission = await Measure(async () =>
            {
                await using var context = new BuildingBlocks.Persistence.AtlasDbContext(jobOptions);
                await new PostgresJobStore(context).EnqueueAsync(project.ProjectId, owner.PrincipalId,
                    new EnqueueJobCommand("test.t42.performance", JsonSerializer.SerializeToElement(new { synthetic = true })));
            });
            var storage = await Storage(administrator.ConnectionString);
            var after = HostResources.Capture();
            var report = new
            {
                format = "business-atlas-t42-performance-v1", measuredAt = DateTimeOffset.UtcNow, target = full ? "full" : "development-smoke",
                syntheticFixture = true, host = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                dataset = new { knowledgeUnits = units, relations, baselines }, samples, coldMilliseconds = cold,
                p95Milliseconds = new { ordinaryQuery = Percentile(ordinary, .95), search = Percentile(searches, .95), boundedReading = Percentile(readings, .95), concurrentFiveHumansTwoAgents = Percentile(concurrent, .95) },
                longTaskSubmissionMilliseconds = submission, storage,
                resources = new { before, after, testProcessPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                    scope = "Real PostgreSQL plus in-process application services; not a measurement of separately deployed API/Worker containers" },
                thresholdsEnforced = full
            };
            var directory = Path.Combine(FindRoot(), "artifacts/performance"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"t42-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine($"AT-T42 performance report: {path}");
            Console.WriteLine(JsonSerializer.Serialize(report.p95Milliseconds));
            Assert.Empty(concurrentErrors);
            if (full)
            {
                Assert.True(report.p95Milliseconds.ordinaryQuery <= 700, $"Ordinary query p95 was {report.p95Milliseconds.ordinaryQuery:F1} ms");
                Assert.True(report.p95Milliseconds.search <= 1000, $"Search p95 was {report.p95Milliseconds.search:F1} ms");
                Assert.True(report.p95Milliseconds.boundedReading <= 1500, $"Reading p95 was {report.p95Milliseconds.boundedReading:F1} ms");
                Assert.True(submission <= 2000, $"Long-task submission was {submission:F1} ms");
                if (after.AvailableMemoryBytes.HasValue) Assert.True(after.AvailableMemoryBytes >= 1_500_000_000, "Less than 1.5 GB host memory remains available");
                Assert.True(after.SwapUsedBytes == before.SwapUsedBytes || after.SwapUsedBytes - before.SwapUsedBytes < 64 * 1024 * 1024, "Sustained swap growth exceeded 64 MiB");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            if (!prepareOnly)
            {
                await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
                await Execute(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            }
        }
    }

    private static async Task Seed(string connection, Guid project, Guid baseline, Guid actor, int units, int relations, int baselines)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "SET session_replication_role=replica");
        await Execute(db, """
            INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by)
            SELECT @project,md5('t42-unit-'||@project||'-'||i)::uuid,'BusinessRule',@actor,@actor FROM generate_series(1,@units) i;
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,content_sha256,sealed,created_by)
            SELECT @project,md5('t42-revision-'||@project||'-'||i)::uuid,md5('t42-unit-'||@project||'-'||i)::uuid,1,'BusinessRule','1.0','normative','verified',
              jsonb_build_object('title','Performance term '||(i%100),'body',jsonb_build_object('definition','Authorized synthetic capacity fixture '||i)),repeat(md5(i::text),2),true,@actor FROM generate_series(1,@units) i;
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
            SELECT @project,@baseline,md5('t42-unit-'||@project||'-'||i)::uuid,md5('t42-revision-'||@project||'-'||i)::uuid FROM generate_series(1,@units) i;
            INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by)
            SELECT @project,md5('t42-relation-unit-'||@project||'-'||i)::uuid,'Relation',@actor,@actor FROM generate_series(1,@relations) i;
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,content_sha256,sealed,created_by)
            SELECT @project,md5('t42-relation-revision-'||@project||'-'||i)::uuid,md5('t42-relation-unit-'||@project||'-'||i)::uuid,1,'Relation','1.0','normative','verified',
              jsonb_build_object('title','Relation '||i,'body',jsonb_build_object('from',jsonb_build_object('unitId',md5('t42-unit-'||@project||'-'||(i%@units+1))::uuid),'to',jsonb_build_object('unitId',md5('t42-unit-'||@project||'-'||((i*17)%@units+1))::uuid))),repeat(md5('r'||i),2),true,@actor FROM generate_series(1,@relations) i;
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
            SELECT @project,@baseline,md5('t42-relation-unit-'||@project||'-'||i)::uuid,md5('t42-relation-revision-'||@project||'-'||i)::uuid FROM generate_series(1,@relations) i;
            INSERT INTO core.relation_index(project_id,relation_revision_id,predicate,from_unit_id,to_unit_id)
            SELECT @project,md5('t42-relation-revision-'||@project||'-'||i)::uuid,'references',md5('t42-unit-'||@project||'-'||(i%@units+1))::uuid,md5('t42-unit-'||@project||'-'||((i*17)%@units+1))::uuid FROM generate_series(1,@relations) i;
            INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,parent_baseline_id,registry_version,digest,sealed,created_by)
            SELECT @project,md5('t42-baseline-'||@project||'-'||b)::uuid,b,@baseline,'atlas.performance-v1',repeat(md5('b'||b),2),true,@actor FROM generate_series(1,@baselines-1) b;
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
            SELECT be.project_id,b.baseline_id,be.unit_id,be.revision_id FROM gov.baseline b JOIN gov.baseline_entry be ON be.project_id=b.project_id AND be.baseline_id=@baseline WHERE b.project_id=@project AND b.baseline_id<>@baseline;
            CREATE INDEX t42_perf_relation_from ON core.relation_index(project_id,from_unit_id);
            CREATE INDEX t42_perf_relation_to ON core.relation_index(project_id,to_unit_id);
            ANALYZE;
            """, ("project", project), ("baseline", baseline), ("actor", actor), ("units", units), ("relations", relations), ("baselines", baselines));
    }

    private static async Task<object> Storage(string connection)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database()),(SELECT COALESCE(sum(pg_total_relation_size(c.oid)),0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('core','gov'))", db);
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        return new { databaseBytes = reader.GetInt64(0), knowledgeSchemaBytes = reader.GetInt64(1) };
    }
    private static async Task AddAgent(string connection, Guid project, Guid principal)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await Execute(db, "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,ARRAY['AgentProposer'])", ("project", project), ("principal", principal)); }
    private static string Issuer(ResolvedReadContext context) => "https://identity.test/performance";
    private static int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    private static async Task<double> Measure(Func<Task> action) { var watch = Stopwatch.StartNew(); await action(); return watch.Elapsed.TotalMilliseconds; }
    private static async Task<double> Measure<T>(Func<Task<T>> action) { var watch = Stopwatch.StartNew(); await action(); return watch.Elapsed.TotalMilliseconds; }
    private static async Task<List<double>> Samples(int count, Func<Task> action) { var values = new List<double>(); for (var index = 0; index < count; index++) values.Add(await Measure(action)); return values; }
    private static async Task<List<double>> Samples<T>(int count, Func<Task<T>> action) { var values = new List<double>(); for (var index = 0; index < count; index++) values.Add(await Measure(action)); return values; }
    private static double Percentile(IEnumerable<double> source, double percentile) { var values = source.Order().ToArray(); return values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)]; }
    private static async Task<T> Scalar<T>(string connection, string sql, Guid? project = null) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); if (project.HasValue) command.Parameters.AddWithValue("project", project.Value); return (T)(await command.ExecuteScalarAsync())!; }
    private static async Task Execute(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters) { await using var command = new NpgsqlCommand(sql, db); command.CommandTimeout = 3600; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); await command.ExecuteNonQueryAsync(); }
    private static string FindRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent; return directory?.FullName ?? throw new InvalidOperationException(); }

    private sealed record HostResources(long? AvailableMemoryBytes, long? SwapUsedBytes)
    {
        public static HostResources Capture()
        {
            if (!OperatingSystem.IsMacOS()) return new(null, null);
            try
            {
                var vm = Command("/usr/bin/vm_stat"); var page = Regex.Match(vm, @"page size of (\d+) bytes"); var pages = page.Success ? long.Parse(page.Groups[1].Value) : 4096;
                long Available(string name) { var match = Regex.Match(vm, name + @":\s+(\d+)\."); return match.Success ? long.Parse(match.Groups[1].Value) * pages : 0; }
                var available = (Available("Pages free") + Available("Pages inactive") + Available("Pages speculative"));
                var swap = Command("/usr/sbin/sysctl", "-n", "vm.swapusage"); var used = Regex.Match(swap, @"used = ([\d.]+)([KMG])");
                var multiplier = used.Groups[2].Value switch { "K" => 1024L, "M" => 1024L * 1024, _ => 1024L * 1024 * 1024 };
                return new(available, used.Success ? (long)(double.Parse(used.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * multiplier) : null);
            }
            catch { return new(null, null); }
        }
        private static string Command(string executable, params string[] arguments)
        { var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true }; foreach (var argument in arguments) start.ArgumentList.Add(argument); using var process = Process.Start(start)!; return process.StandardOutput.ReadToEnd(); }
    }
}
