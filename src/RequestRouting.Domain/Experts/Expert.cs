namespace RequestRouting.Domain.Experts;

public sealed class Expert
{
    private readonly List<ExpertTopic> _topics = [];

    private Expert()
    {
        Name = null!;
    }

    public int Id { get; private set; }
    public string Name { get; private set; }
    public int Capacity { get; private set; }

    /// <summary>
    /// Requests currently assigned to this expert. Kept as a column, not computed, so that the database
    /// itself can enforce <c>OpenCount &lt;= Capacity</c> through a CHECK constraint.
    /// </summary>
    public int OpenCount { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset? LastAssignedAt { get; private set; }
    public IReadOnlyCollection<ExpertTopic> Topics => _topics;

    public bool CanAccept => IsActive && OpenCount < Capacity;

    public static Expert Create(string name, int capacity, IEnumerable<string> topics, bool isActive = true)
    {
        var expert = new Expert { Name = name, IsActive = isActive };
        expert.ChangeCapacity(capacity);
        expert.ReplaceTopics(topics);
        return expert;
    }

    public void AcceptAssignment(DateTimeOffset now)
    {
        if (!CanAccept)
        {
            throw new DomainRuleException("expert-unavailable", $"Expert {Id} cannot accept another request.");
        }

        OpenCount++;
        LastAssignedAt = now;
    }

    public void ReleaseAssignment()
    {
        if (OpenCount == 0)
        {
            throw new DomainRuleException("expert-has-no-open-requests", $"Expert {Id} has no open requests.");
        }

        OpenCount--;
    }

    /// <summary>
    /// Capacity cannot drop below the current open count: the invariant is "never over capacity", and
    /// silently unassigning work that is already in progress is not this method's decision to make.
    /// </summary>
    public void ChangeCapacity(int capacity)
    {
        if (capacity < 1)
        {
            throw new DomainRuleException("invalid-capacity", "Capacity must be at least 1.");
        }

        if (capacity < OpenCount)
        {
            throw new DomainRuleException(
                "capacity-below-open-count",
                $"Expert {Id} has {OpenCount} open requests; capacity cannot be set to {capacity}.");
        }

        Capacity = capacity;
    }

    /// <summary>Deactivation only stops new assignments. Requests already assigned stay with the expert.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;

    public void ReplaceTopics(IEnumerable<string> topics)
    {
        var codes = topics.Select(TopicCode.Normalize).ToHashSet();

        if (codes.Count == 0)
        {
            throw new DomainRuleException("expert-needs-topic", "An expert needs at least one topic.");
        }

        _topics.RemoveAll(t => !codes.Contains(t.Topic));
        codes.ExceptWith(_topics.Select(t => t.Topic));
        _topics.AddRange(codes.Select(code => new ExpertTopic(code)));
    }
}
