using RequestRouting.Domain.Requests;

namespace RequestRouting.UnitTests;

public class QueuePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HeadStart = TimeSpan.FromMinutes(30);

    private static DateTimeOffset Rank(RequestPriority priority, int minutesAfterT0) =>
        QueuePolicy.RankFor(priority, T0.AddMinutes(minutesAfterT0), HeadStart);

    [Fact]
    public void Urgent_overtakes_a_normal_request_younger_than_the_head_start() =>
        Assert.True(Rank(RequestPriority.Urgent, 29) < Rank(RequestPriority.Normal, 0));

    [Fact]
    public void Normal_keeps_its_place_once_older_than_the_head_start() =>
        Assert.True(Rank(RequestPriority.Normal, 0) < Rank(RequestPriority.Urgent, 31));

    [Fact]
    public void Same_priority_is_first_come_first_served()
    {
        Assert.True(Rank(RequestPriority.Normal, 0) < Rank(RequestPriority.Normal, 1));
        Assert.True(Rank(RequestPriority.Urgent, 0) < Rank(RequestPriority.Urgent, 1));
    }
}
