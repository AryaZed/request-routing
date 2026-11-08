using RequestRouting.Domain.Experts;

namespace RequestRouting.Domain.Requests;

public sealed class ServiceRequest
{
    private ServiceRequest()
    {
        RequestKey = null!;
        Topic = null!;
        Title = null!;
    }

    public long Id { get; private set; }
    public string RequestKey { get; private set; }
    public string Topic { get; private set; }
    public string Title { get; private set; }
    public RequestPriority Priority { get; private set; }
    public RequestStatus Status { get; private set; }
    public int? ExpertId { get; private set; }

    /// <summary>Position in the waiting queue. See <see cref="QueuePolicy"/>.</summary>
    public DateTimeOffset QueueRank { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? AssignedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Every request starts Pending. Assignment always happens by being taken from the queue.</summary>
    public static ServiceRequest Submit(
        string requestKey,
        string topic,
        string title,
        RequestPriority priority,
        DateTimeOffset now,
        TimeSpan urgentHeadStart) =>
        new()
        {
            RequestKey = requestKey,
            Topic = TopicCode.Normalize(topic),
            Title = title,
            Priority = priority,
            Status = RequestStatus.Pending,
            QueueRank = QueuePolicy.RankFor(priority, now, urgentHeadStart),
            CreatedAt = now,
        };

    /// <summary>True when a retry with the same key describes the same request.</summary>
    public bool Matches(string topic, string title, RequestPriority priority) =>
        Topic == TopicCode.Normalize(topic) && Title == title && Priority == priority;

    public void AssignTo(Expert expert, DateTimeOffset now)
    {
        if (Status != RequestStatus.Pending)
        {
            throw new DomainRuleException("request-not-pending", $"Request {Id} is {Status}.");
        }

        expert.AcceptAssignment(now);
        ExpertId = expert.Id;
        Status = RequestStatus.Assigned;
        AssignedAt = now;
    }

    /// <param name="assignee">The assigned expert, or null when the request is still pending.</param>
    /// <param name="now">The closing time.</param>
    public void Close(Expert? assignee, DateTimeOffset now)
    {
        if (Status == RequestStatus.Closed)
        {
            throw new DomainRuleException("request-already-closed", $"Request {Id} is already closed.");
        }

        if (assignee?.Id != ExpertId)
        {
            throw new DomainRuleException("wrong-assignee", $"Request {Id} is not assigned to that expert.");
        }

        assignee?.ReleaseAssignment();
        Status = RequestStatus.Closed;
        ClosedAt = now;
    }
}
