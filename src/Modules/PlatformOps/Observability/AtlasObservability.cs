using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Persistence;

namespace PlatformOps.Observability;

/// <summary>Low-dependency OpenTelemetry-compatible Activity/Meter instrumentation and internal Prometheus export.</summary>
public static class AtlasObservability
{
    public const string Name = "BusinessAtlas";
    public static readonly ActivitySource ActivitySource = new(Name, "1.0");
    private static readonly Meter Meter = new(Name, "1.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("atlas.http.requests", unit: "{request}");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("atlas.http.duration", unit: "ms");
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("atlas.job.claims", unit: "{job}");
    private static readonly Counter<long> Recoveries = Meter.CreateCounter<long>("atlas.job.lease_recoveries", unit: "{job}");
    private static readonly Counter<long> StaleCommits = Meter.CreateCounter<long>("atlas.job.stale_commits", unit: "{job}");
    private static long requests;
    private static double durationMilliseconds;
    private static long claims;
    private static long recoveries;
    private static long staleCommits;

    public static async Task MeasureHttpRequests(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Requests.Add(1, new KeyValuePair<string, object?>("status", context.Response.StatusCode));
            RequestDuration.Record(elapsed, new KeyValuePair<string, object?>("status", context.Response.StatusCode));
            Interlocked.Increment(ref requests);
            Interlocked.Exchange(ref durationMilliseconds, elapsed);
        }
    }

    public static void JobClaimed(bool recovered)
    {
        Claims.Add(1); Interlocked.Increment(ref claims);
        if (recovered) { Recoveries.Add(1); Interlocked.Increment(ref recoveries); }
    }

    public static void StaleCommit()
    {
        StaleCommits.Add(1); Interlocked.Increment(ref staleCommits);
    }

    public static async Task<IResult> Metrics(AtlasDbContext database, CancellationToken cancellationToken)
    {
        await using var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object(
              'oldestQueuedSeconds',COALESCE((SELECT EXTRACT(EPOCH FROM now()-min(available_at)) FROM ops.job WHERE state='queued'),0),
              'expiredLeases',COALESCE((SELECT count(*) FROM ops.job WHERE state='running' AND lease_until<now()),0),
              'failedJobs',COALESCE((SELECT count(*) FROM ops.job WHERE state='failed'),0),
              'runningJobs',COALESCE((SELECT count(*) FROM ops.job WHERE state='running'),0),
              'outboxRetries',COALESCE((SELECT count(*) FROM ops.outbox WHERE attempt>1),0),
              'outboxBacklog',COALESCE((SELECT count(*) FROM ops.outbox WHERE state='queued'),0),
              'databaseConnections',(SELECT count(*) FROM pg_stat_activity))
            """;
        var values = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!).RootElement;
        var output = new StringBuilder();
        Gauge(output, "atlas_queue_oldest_seconds", "Age of the oldest available queued job", values, "oldestQueuedSeconds");
        Gauge(output, "atlas_job_lease_expired", "Running jobs whose lease has expired", values, "expiredLeases");
        Gauge(output, "atlas_job_failed", "Durable jobs in failed state", values, "failedJobs");
        Gauge(output, "atlas_job_running", "Durable jobs currently leased", values, "runningJobs");
        Gauge(output, "atlas_outbox_retries", "Outbox records with at least one delivery attempt", values, "outboxRetries");
        Gauge(output, "atlas_outbox_backlog", "Outbox records awaiting delivery", values, "outboxBacklog");
        Gauge(output, "atlas_database_connections", "PostgreSQL connections", values, "databaseConnections");
        Counter(output, "atlas_http_requests_total", "HTTP requests handled", Interlocked.Read(ref requests));
        Counter(output, "atlas_job_claims_total", "Durable jobs claimed", Interlocked.Read(ref claims));
        Counter(output, "atlas_job_lease_recoveries_total", "Expired job leases reclaimed", Interlocked.Read(ref recoveries));
        Counter(output, "atlas_job_stale_commits_total", "Stale fencing-token commits rejected", Interlocked.Read(ref staleCommits));
        Gauge(output, "atlas_http_last_duration_milliseconds", "Last observed HTTP duration; use tracing aggregation for fleet percentiles", Interlocked.Exchange(ref durationMilliseconds, durationMilliseconds));
        return Results.Text(output.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static void Gauge(StringBuilder output, string name, string help, JsonElement values, string property) =>
        Gauge(output, name, help, values.GetProperty(property).GetDouble());
    private static void Gauge(StringBuilder output, string name, string help, double value)
    {
        output.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} gauge\n{name} {value}\n");
    }
    private static void Counter(StringBuilder output, string name, string help, long value)
    {
        output.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} counter\n{name} {value}\n");
    }
}
