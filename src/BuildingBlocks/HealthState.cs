namespace BuildingBlocks;

/// <summary>Process-local health only; dependency probes are registered by their owning tasks.</summary>
public sealed class HealthState
{
    private int _stopping;

    public bool IsReady => Volatile.Read(ref _stopping) == 0;

    public void BeginShutdown() => Interlocked.Exchange(ref _stopping, 1);
}
