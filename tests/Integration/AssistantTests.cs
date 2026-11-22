using System.Text.Json;
using AccessApplicability.Contracts;
using Assistant.Contracts;
using Assistant.Infrastructure;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlatformOps.Jobs;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class AssistantTests
{
    [PostgreSqlFact]
    public async Task AT_T28_Conversation_history_is_persistent_private_cancelable_and_reauthorized()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t28_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database}";
            await command.ExecuteNonQueryAsync();
        }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var dbContext = new AtlasDbContext(options)) await dbContext.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var identities = new PostgresProjectIdentityStore(runtime);
            var issuer = $"https://identity.test/{Guid.NewGuid():N}";
            var owner = await identities.ResolvePrincipalAsync(issuer, "owner", PrincipalKind.Human, CancellationToken.None);
            var peer = await identities.ResolvePrincipalAsync(issuer, "peer", PrincipalKind.Human, CancellationToken.None);
            var project = await identities.CreateProjectAsync(owner, new CreateProjectCommand("Assistant test", "assistant-test", "create-assistant-1"), "request-1", null, CancellationToken.None);
            await identities.ChangeMembershipAsync(owner, project.ProjectId, new ChangeMembershipCommand(peer.PrincipalId, ["ProjectReader"], 0, true, project.Version, "membership-t28-1"), "request-2", null, CancellationToken.None);

            var store = new PostgresAssistantStore(runtime);
            var providerId = Guid.NewGuid();
            var provider = await store.ConfigureProviderAsync(owner.PrincipalId, project.ProjectId, providerId,
                new ProviderConfig(providerId, "integration", "https://models.example/v1/chat/completions", "integration-model", "env:ATLAS_TEST_MODEL_KEY", true, JsonSerializer.SerializeToElement(new { }), 0),
                0, "provider-t28-0001", "request-3", null, CancellationToken.None);
            Assert.Equal(1, provider.Version);
            Assert.Single((await store.ListProvidersAsync(owner.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items);

            var context = new ReadContextRequest(ReadMode.Published, ReadPurpose.Understand, project.CurrentBaselineId, null, null);
            var conversation = await store.CreateConversationAsync(owner.PrincipalId, project.ProjectId,
                new CreateConversationRequest(null, context, "Persistent assistant test"), "conversation-t28-1", "request-4", null, CancellationToken.None);
            var job = await store.SendMessageAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId,
                new SendMessageRequest("Which approved requirements govern assistant citations?", context), conversation.Version,
                "message-t28-00001", "request-5", null, CancellationToken.None);
            Assert.Equal(JobState.Queued, job.State);
            var messages = await store.ListMessagesAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId, null, 30, CancellationToken.None);
            Assert.Single(messages.Items);
            Assert.Equal("user", messages.Items[0].Kind);
            Assert.Equal("Which approved requirements govern assistant citations?", messages.Items[0].Text);
            Assert.Contains((await store.EventsAfterAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId, 0, 30, CancellationToken.None)), e => e.Kind == "message.accepted");

            // Same-project membership is not permission to read another user's assistant history.
            Assert.Empty((await store.ListConversationsAsync(peer.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items);
            var denied = await Assert.ThrowsAsync<ProjectIdentity.Application.ProjectIdentityException>(() =>
                store.ListMessagesAsync(peer.PrincipalId, project.ProjectId, conversation.ConversationId, null, 30, CancellationToken.None));
            Assert.Equal(404, denied.StatusCode);

            var canceled = await store.CancelAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId, job.JobId, conversation.Version + 1,
                "test cancellation", "cancel-t28-00001", "request-6", null, CancellationToken.None);
            Assert.Equal(JobState.Canceled, canceled.State);
            Assert.Contains((await store.EventsAfterAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId, 0, 30, CancellationToken.None)), e => e.Kind == "conversation.cancel_requested");

            // A membership revocation is checked again when persisted history or SSE events are read.
            await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("UPDATE identity.membership SET active=false WHERE project_id=@project AND principal_id=@principal", connection);
                command.Parameters.AddWithValue("project", project.ProjectId); command.Parameters.AddWithValue("principal", owner.PrincipalId);
                await command.ExecuteNonQueryAsync();
            }
            Assert.Empty((await store.ListConversationsAsync(owner.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items);
            var revoked = await Assert.ThrowsAsync<ProjectIdentity.Application.ProjectIdentityException>(() =>
                store.EventsAfterAsync(owner.PrincipalId, project.ProjectId, conversation.ConversationId, 0, 30, CancellationToken.None));
            Assert.Equal(404, revoked.StatusCode);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }
}
