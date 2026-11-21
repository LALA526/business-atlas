using BuildingBlocks;

namespace Atlas.Domain.Tests;

public sealed class HealthStateTests
{
    [Fact]
    public void ShutdownIsIdempotentAndVisibleAcrossThreads()
    {
        var state = new HealthState();
        Assert.True(state.IsReady);
        Parallel.For(0, 32, _ => state.BeginShutdown());
        Assert.False(state.IsReady);
    }
}
