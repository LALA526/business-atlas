using System.Text.Json;
using BuildingBlocks.Commands;

namespace Atlas.Domain.Tests;

public sealed class CommandPipelineTests
{
    private static readonly CommandContext Context = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"),
        "testCommand",
        "request-key-0001",
        "request-1");

    [Fact]
    public async Task SuccessfulCommandCommitsResultAuditAndOutboxAtomically()
    {
        var transaction = new FakeTransaction();
        var pipeline = new CommandPipeline(new FakeFactory(transaction), new AllowCommands());
        var handler = new TestHandler(async (command, tx, _) =>
        {
            await tx.AppendOutboxAsync(new OutboxMessage(
                command.ProjectId,
                Guid.NewGuid(),
                "KnowledgeCommandApplied.v1",
                command.ProjectId,
                3,
                "1.2",
                JsonSerializer.SerializeToElement(new { value = command.Value }),
                DateTimeOffset.UtcNow), CancellationToken.None);
            return new CommandResult(new { value = command.Value }, 201);
        });

        var result = await pipeline.ExecuteAsync(Context, new TestCommand(Context.ProjectId, 42), handler);

        Assert.False(result.Replayed);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(42, result.Data.GetProperty("value").GetInt32());
        Assert.True(transaction.Committed);
        Assert.False(transaction.RolledBack);
        Assert.Single(transaction.Audit);
        Assert.Single(transaction.Outbox);
        Assert.Equal(RequestDigest.Compute(new TestCommand(Context.ProjectId, 42)), transaction.SavedDigest);
    }

    [Fact]
    public async Task SameKeyAndRequestReturnsStoredResultWithoutRunningHandler()
    {
        var stored = new StoredCommandResult(JsonSerializer.SerializeToElement(new { value = 42 }), 201);
        var digest = RequestDigest.Compute(new TestCommand(Context.ProjectId, 42));
        var transaction = new FakeTransaction { Existing = new IdempotencyMatch(digest, stored) };
        var pipeline = new CommandPipeline(new FakeFactory(transaction), new AllowCommands());
        var calls = 0;
        var handler = new TestHandler((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new CommandResult(new { value = 0 }));
        });

        var result = await pipeline.ExecuteAsync(Context, new TestCommand(Context.ProjectId, 42), handler);

        Assert.True(result.Replayed);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(42, result.Data.GetProperty("value").GetInt32());
        Assert.Equal(0, calls);
        Assert.False(transaction.Committed);
        Assert.Empty(transaction.Audit);
    }

    [Fact]
    public async Task SameKeyWithDifferentRequestReturnsConflictAndNoBusinessWrite()
    {
        var digest = RequestDigest.Compute(new TestCommand(Context.ProjectId, 42));
        var stored = new StoredCommandResult(JsonSerializer.SerializeToElement(new { value = 42 }), 201);
        var transaction = new FakeTransaction { Existing = new IdempotencyMatch(digest, stored) };
        var pipeline = new CommandPipeline(new FakeFactory(transaction), new AllowCommands());
        var calls = 0;
        var handler = new TestHandler((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new CommandResult(new { value = 0 }));
        });

        var error = await Assert.ThrowsAsync<AtlasCommandException>(() =>
            pipeline.ExecuteAsync(Context, new TestCommand(Context.ProjectId, 43), handler));

        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, calls);
        Assert.False(transaction.Committed);
    }

    [Fact]
    public async Task HandlerFailureRollsBackEveryWriteAndIdempotencyRecord()
    {
        var transaction = new FakeTransaction();
        var pipeline = new CommandPipeline(new FakeFactory(transaction), new AllowCommands());
        var handler = new TestHandler(async (_, tx, _) =>
        {
            await tx.AppendAuditAsync(new AuditEntryInput("inner", EmptyObject(), "OK", EmptyObject()), CancellationToken.None);
            throw new InvalidOperationException("fail after business write");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(Context, new TestCommand(Context.ProjectId, 42), handler));

        Assert.True(transaction.RolledBack);
        Assert.False(transaction.Committed);
        Assert.Null(transaction.SavedDigest);
    }

    [Fact]
    public async Task RacingWinnerResultIsReplayedAndLoserIsRolledBack()
    {
        var digest = RequestDigest.Compute(new TestCommand(Context.ProjectId, 42));
        var winner = new IdempotencyMatch(
            digest,
            new StoredCommandResult(JsonSerializer.SerializeToElement(new { value = 42 }), 201));
        var transaction = new FakeTransaction { RaceWinner = winner };
        var pipeline = new CommandPipeline(new FakeFactory(transaction), new AllowCommands());
        var handler = new TestHandler((_, _, _) => Task.FromResult(new CommandResult(new { value = 42 }, 201)));

        var result = await pipeline.ExecuteAsync(Context, new TestCommand(Context.ProjectId, 42), handler);

        Assert.True(result.Replayed);
        Assert.Equal(201, result.StatusCode);
        Assert.True(transaction.RolledBack);
        Assert.False(transaction.Committed);
        Assert.Empty(transaction.Audit);
    }

    [Fact]
    public void RequestDigestUsesCanonicalPropertyOrdering()
    {
        var left = RequestDigest.ComputeJson("{\"b\":[2,1],\"a\":{\"z\":true,\"y\":null}}");
        var right = RequestDigest.ComputeJson("{ \"a\" : { \"y\" : null, \"z\" : true }, \"b\" : [2,1] }");
        Assert.Equal(left, right);
        Assert.Matches("^[0-9a-f]{64}$", left);
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    private sealed record TestCommand(Guid ProjectId, int Value);

    private sealed class TestHandler(Func<TestCommand, ICommandTransaction, CancellationToken, Task<CommandResult>> handle)
        : ICommandHandler<TestCommand>
    {
        public Task<CommandResult> HandleAsync(TestCommand command, ICommandTransaction transaction, CancellationToken cancellationToken) =>
            handle(command, transaction, cancellationToken);
    }

    private sealed class AllowCommands : ICommandAuthorizer
    {
        public Task AuthorizeAsync(CommandContext context, object command, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeFactory(ICommandTransaction transaction) : ICommandTransactionFactory
    {
        public Task<ICommandTransaction> BeginAsync(CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(transaction);
    }

    private sealed class FakeTransaction : ICommandTransaction
    {
        internal IdempotencyMatch? Existing { get; init; }
        internal IdempotencyMatch? RaceWinner { get; init; }
        internal bool Committed { get; private set; }
        internal bool RolledBack { get; private set; }
        internal string? SavedDigest { get; private set; }
        internal List<AuditEntryInput> Audit { get; } = [];
        internal List<OutboxMessage> Outbox { get; } = [];
        public CommandContext CommandContext => Context;

        public Task<IdempotencyMatch?> FindIdempotencyAsync(CancellationToken cancellationToken) => Task.FromResult(Existing);

        public Task SaveIdempotencyAsync(string requestSha256, StoredCommandResult result, CancellationToken cancellationToken)
        {
            if (RaceWinner is not null)
            {
                throw new IdempotencyRaceException(RaceWinner);
            }

            SavedDigest = requestSha256;
            return Task.CompletedTask;
        }

        public Task AppendAuditAsync(AuditEntryInput entry, CancellationToken cancellationToken)
        {
            Audit.Add(entry);
            return Task.CompletedTask;
        }

        public Task AppendOutboxAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Outbox.Add(message);
            return Task.CompletedTask;
        }

        public Task<bool> TryRecordInboxAsync(string consumerKey, Guid eventId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<long> CompareAndSwapAsync(CasUpdate update, CancellationToken cancellationToken) => Task.FromResult(update.ExpectedVersion + 1);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!Committed)
            {
                RolledBack = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
