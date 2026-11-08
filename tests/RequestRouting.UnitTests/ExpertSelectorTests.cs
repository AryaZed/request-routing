using RequestRouting.Domain.Experts;

namespace RequestRouting.UnitTests;

public class ExpertSelectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static Expert ExpertWith(string name, int open, int capacity, DateTimeOffset? lastAssignedAt = null)
    {
        var expert = Expert.Create(name, capacity, ["finance"]);
        for (var i = 0; i < open; i++)
        {
            expert.AcceptAssignment(lastAssignedAt ?? T0);
        }

        return expert;
    }

    [Fact]
    public void Picks_the_lowest_ratio_not_the_lowest_count()
    {
        // The example from the task: 2 of 4 (0.5) loses to 3 of 10 (0.3).
        var first = ExpertWith("first", open: 2, capacity: 4);
        var second = ExpertWith("second", open: 3, capacity: 10);

        Assert.Same(second, ExpertSelector.PickBest([first, second]));
    }

    [Fact]
    public void Treats_equal_fractions_as_a_tie()
    {
        // 1/3 and 2/6 are the same load, so the longer-idle expert wins, in either input order.
        var recent = ExpertWith("recent", open: 1, capacity: 3, lastAssignedAt: T0.AddMinutes(5));
        var idle = ExpertWith("idle", open: 2, capacity: 6, lastAssignedAt: T0);

        Assert.Same(idle, ExpertSelector.PickBest([recent, idle]));
        Assert.Same(idle, ExpertSelector.PickBest([idle, recent]));
    }

    [Fact]
    public void Prefers_a_never_assigned_expert_on_a_tie()
    {
        var used = ExpertWith("used", open: 0, capacity: 5);
        used.AcceptAssignment(T0);
        used.ReleaseAssignment();
        var fresh = ExpertWith("fresh", open: 0, capacity: 5);

        Assert.Same(fresh, ExpertSelector.PickBest([used, fresh]));
    }

    [Fact]
    public void Never_picks_a_full_or_inactive_expert()
    {
        var full = ExpertWith("full", open: 2, capacity: 2);
        var inactive = ExpertWith("inactive", open: 0, capacity: 5);
        inactive.SetActive(false);

        Assert.Null(ExpertSelector.PickBest([full, inactive]));
        Assert.Null(ExpertSelector.PickBest([]));
    }

    [Fact]
    public void Compares_large_capacities_without_overflow()
    {
        var busy = ExpertWith("busy", open: 1, capacity: int.MaxValue);
        var idle = ExpertWith("idle", open: 0, capacity: int.MaxValue);

        Assert.Same(idle, ExpertSelector.PickBest([busy, idle]));
    }
}
