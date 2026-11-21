using System.Text.Json;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlatformOps;
using PlatformOps.Jobs;

namespace Atlas.Integration.Tests;

public sealed class PersistentJobTests
{
    [PostgreSqlFact]
    public async Task AT_T17_Lease_contention_checkpoint_restart_cancel_retry_and_inbox_deduplication()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var databaseName = $"atlas_t17_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await Execute(connection, $"CREATE DATABASE {databaseName}"); }
        administrator.Database = databaseName;
        try
        {
            await using (var context = Context(administrator.ConnectionString)) await context.Database.MigrateAsync();
            var project = Guid.NewGuid(); var principal = Guid.NewGuid();
            await Seed(administrator.ConnectionString, project, principal);
            var jobId = Guid.NewGuid();
            await using (var context = Context(administrator.ConnectionString))
                await new PostgresJobStore(context).EnqueueAsync(project, principal, new EnqueueJobCommand("test.checkpoint", JsonSerializer.SerializeToElement(new { value = 42 }), JobId: jobId));

            var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
            {
                await using var context = Context(administrator.ConnectionString);
                return await new PostgresJobStore(context).ClaimAsync(project, principal, $"worker-{index}", TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }));
            Assert.Single(claims, c => c is not null);
            var first = claims.Single(c => c is not null)!;
            Assert.True(await Checkpoint(administrator.ConnectionString, first, JsonSerializer.SerializeToElement(new { step = 1, progress = 0.5 }), 0.5));
            Assert.False(await Complete(administrator.ConnectionString, first with { Fence = first.Fence - 1 }, new JobCompletion(JsonSerializer.SerializeToElement(new { stale = true }))));

            // Simulate a killed worker: no completion, lease expires, another claim increments the fence.
            await Task.Delay(150);
            ClaimedJob? recovered;
            await using (var context = Context(administrator.ConnectionString))
                recovered = await new PostgresJobStore(context).ClaimAsync(project, principal, "worker-restart", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotNull(recovered); Assert.True(recovered.Fence > first.Fence); Assert.Equal(1, recovered.Job.Checkpoint.GetProperty("step").GetInt32());
            Assert.False(await Complete(administrator.ConnectionString, first, new JobCompletion(JsonSerializer.SerializeToElement(new { stale = true }))));
            Assert.True(await Complete(administrator.ConnectionString, recovered, new JobCompletion(JsonSerializer.SerializeToElement(new { resultRef = "artifact:test" }))));
            await using (var context = Context(administrator.ConnectionString))
            {
                var store = new PostgresJobStore(context);
                Assert.Equal(JobState.Succeeded, (await store.GetAsync(project, principal, jobId)).State);
                Assert.Equal("artifact:test", (await store.GetResultAsync(project, principal, jobId)).GetProperty("resultRef").GetString());
            }

            await using (var context = Context(administrator.ConnectionString))
            {
                var patterns = new PostgresErrorPatternStore(context);
                var candidate = await patterns.ProposeAsync(project, principal, new ErrorPattern(Guid.Empty, "t17.lease", 1,
                    JsonSerializer.SerializeToElement(new { kind = "test" }), "stale completion", "lease expired", "fence CAS", "reclaim with new fence", "AT-T17",
                    JsonSerializer.SerializeToElement(Array.Empty<object>()), "candidate"), "pattern-propose-1", "pattern-request-1", null, CancellationToken.None);
                var reviewed = await patterns.ReviewAsync(project, principal, true, candidate.PatternId, 1, "reviewed", "verified against the lease recovery test", "pattern-review-01", "pattern-request-2", null, CancellationToken.None);
                Assert.Equal("reviewed", reviewed.State); Assert.Equal(2, reviewed.PatternRevision);
                var page = await patterns.ListAsync(project, principal, null, 30, "test", null, CancellationToken.None);
                Assert.Single(page.Items); Assert.Equal(2, page.Items[0].PatternRevision);
            }

            var cancelId = Guid.NewGuid();
            await using (var context = Context(administrator.ConnectionString))
            {
                var store = new PostgresJobStore(context);
                await store.EnqueueAsync(project, principal, new EnqueueJobCommand("test.cancel", JsonSerializer.SerializeToElement(new { }), JobId: cancelId));
                var queued = await store.GetAsync(project, principal, cancelId);
                var canceled = await store.CancelAsync(project, principal, cancelId, queued.Version, "test cancellation", "cancel-test-0001", "request-1", null, CancellationToken.None);
                Assert.Equal(JobState.Canceled, canceled.State);
                var replay = await store.CancelAsync(project, principal, cancelId, queued.Version, "test cancellation", "cancel-test-0001", "request-1", null, CancellationToken.None);
                Assert.Equal(canceled.Version, replay.Version);
                var retried = await store.RetryAsync(project, principal, cancelId, canceled.Version, "test retry", "retry-test-00001", "request-2", null, CancellationToken.None);
                Assert.Equal(JobState.Queued, retried.State); Assert.Equal(0, retried.Attempt);
            }
            await using (var context = Context(administrator.ConnectionString))
            {
                var store = new PostgresJobStore(context);
                var running = await store.ClaimAsync(project, principal, "cancel-worker", TimeSpan.FromMinutes(1), CancellationToken.None);
                Assert.Equal(cancelId, running!.Job.JobId);
                var requested = await store.CancelAsync(project, principal, cancelId, running.Job.Version, "stop running work", "cancel-running-1", "request-3", null, CancellationToken.None);
                Assert.Equal(JobState.CancelRequested, requested.State);
                Assert.False(await store.CompleteAsync(running, new JobCompletion(JsonSerializer.SerializeToElement(new { forbidden = true })), CancellationToken.None));
                Assert.True(await store.AcknowledgeCancelAsync(running, CancellationToken.None));
                Assert.Equal(JobState.Canceled, (await store.GetAsync(project, principal, cancelId)).State);
            }

            var eventId = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
            {
                await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
                await SetContext(connection, project, principal, tx);
                await Execute(connection, "INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_revision,schema_version,payload,state) VALUES(@p,@e,'Test.v1',1,'v1','{}','queued')", tx,
                    new NpgsqlParameter("p", project), new NpgsqlParameter("e", eventId));
                await tx.CommitAsync();
            }
            foreach (var consumer in new[] { "same-consumer", "same-consumer" })
            {
                await using var connection = new NpgsqlConnection(administrator.ConnectionString);
                await connection.OpenAsync(); await SetContext(connection, project, principal, null);
                await Execute(connection, "INSERT INTO ops.inbox(project_id,consumer_key,event_id) VALUES(@p,@c,@e) ON CONFLICT DO NOTHING",
                    new NpgsqlParameter("p", project), new NpgsqlParameter("c", consumer), new NpgsqlParameter("e", eventId));
            }
            await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT count(*) FROM ops.inbox WHERE project_id=@p AND consumer_key='same-consumer' AND event_id=@e", connection);
                command.Parameters.AddWithValue("p", project); command.Parameters.AddWithValue("e", eventId);
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            await using (var context = Context(administrator.ConnectionString))
            {
                var outbox = new PostgresOutboxStore(context);
                ClaimedOutboxMessage? delivery = null;
                for (var index = 0; index < 20 && delivery?.EventId != eventId; index++)
                {
                    if (delivery is not null) Assert.True(await outbox.CompleteAsync(project, principal, delivery.EventId, delivery.Fence));
                    delivery = await outbox.ClaimAsync(project, principal, TimeSpan.FromMinutes(1));
                }
                Assert.NotNull(delivery); Assert.Equal(eventId, delivery.EventId);
                Assert.False(await outbox.CompleteAsync(project, principal, eventId, delivery.Fence - 1));
                Assert.True(await outbox.CompleteAsync(project, principal, eventId, delivery.Fence));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString);
            await connection.OpenAsync(); await Execute(connection, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)");
        }
    }

    private static AtlasDbContext Context(string connection) => new(new DbContextOptionsBuilder<AtlasDbContext>().UseNpgsql(connection).Options);
    private static async Task<bool> Checkpoint(string connection, ClaimedJob job, JsonElement checkpoint, double progress)
    { await using var context = Context(connection); return await new PostgresJobStore(context).SaveCheckpointAsync(job, new JobCheckpoint(checkpoint, progress), CancellationToken.None); }
    private static async Task<bool> Complete(string connection, ClaimedJob job, JobCompletion result)
    { await using var context = Context(connection); return await new PostgresJobStore(context).CompleteAsync(job, result, CancellationToken.None); }
    private static async Task Seed(string connection, Guid project, Guid principal)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "INSERT INTO identity.principal(principal_id,issuer,subject,kind) VALUES(@id,'atlas-test','t17','human')", new NpgsqlParameter("id", principal));
        await SetContext(db, project, principal, null);
        await Execute(db, "INSERT INTO identity.project(project_id,slug,display_name,owner_id) VALUES(@p,@s,'T17',@u)", new NpgsqlParameter("p", project), new NpgsqlParameter("s", $"t17-{project:N}"), new NpgsqlParameter("u", principal));
        await Execute(db, "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@p,@u,ARRAY['Contributor','Verifier'])", new NpgsqlParameter("p", project), new NpgsqlParameter("u", principal));
    }
    private static async Task SetContext(NpgsqlConnection db, Guid project, Guid principal, System.Data.Common.DbTransaction? tx)
    { await using var command = new NpgsqlCommand("SELECT set_config('atlas.project_id',@p,true),set_config('atlas.subject_id',@u,true)", db, (NpgsqlTransaction?)tx); command.Parameters.AddWithValue("p", project.ToString()); command.Parameters.AddWithValue("u", principal.ToString()); await command.ExecuteNonQueryAsync(); }
    private static async Task Execute(NpgsqlConnection db, string sql, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(); }
    private static async Task Execute(NpgsqlConnection db, string sql, NpgsqlTransaction tx, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db, tx); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(); }
}
