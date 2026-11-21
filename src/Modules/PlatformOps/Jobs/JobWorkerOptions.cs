namespace PlatformOps.Jobs;

public sealed class JobWorkerOptions
{
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DefaultExecutionBudget { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxAttempts { get; set; } = 5;
}
