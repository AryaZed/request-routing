namespace RequestRouting.Application;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>How far ahead of its arrival time an urgent request is ranked. See QueuePolicy.</summary>
    public TimeSpan UrgentHeadStart { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often each instance re-checks the queue to recover from crashes. Zero disables it.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
