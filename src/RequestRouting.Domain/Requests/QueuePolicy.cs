namespace RequestRouting.Domain.Requests;

/// <summary>
/// The queue is served in ascending rank order. An urgent request is ranked as if it had arrived
/// <c>urgentHeadStart</c> earlier than it did, so it overtakes normal requests that are younger than the
/// head start and nothing else. A normal request created at time T can therefore only be overtaken by
/// urgent requests created before T + headStart: a finite set, which rules out starvation.
/// </summary>
public static class QueuePolicy
{
    public static DateTimeOffset RankFor(
        RequestPriority priority,
        DateTimeOffset createdAt,
        TimeSpan urgentHeadStart) =>
        priority == RequestPriority.Urgent ? createdAt - urgentHeadStart : createdAt;
}
