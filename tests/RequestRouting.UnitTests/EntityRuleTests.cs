using RequestRouting.Domain;
using RequestRouting.Domain.Experts;
using RequestRouting.Domain.Requests;

namespace RequestRouting.UnitTests;

public class EntityRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static ServiceRequest NewRequest() =>
        ServiceRequest.Submit("key", "finance", "title", RequestPriority.Normal, T0, TimeSpan.FromMinutes(30));

    private static void AssertRule(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<DomainRuleException>(act).Code);

    [Fact]
    public void A_request_cannot_be_assigned_twice()
    {
        var request = NewRequest();
        request.AssignTo(Expert.Create("a", 5, ["finance"]), T0);

        AssertRule("request-not-pending", () => request.AssignTo(Expert.Create("b", 5, ["finance"]), T0));
    }

    [Fact]
    public void An_expert_cannot_accept_past_capacity()
    {
        var expert = Expert.Create("a", 1, ["finance"]);
        expert.AcceptAssignment(T0);

        AssertRule("expert-unavailable", () => expert.AcceptAssignment(T0));
    }

    [Fact]
    public void Failed_assignment_leaves_the_request_pending()
    {
        var request = NewRequest();
        var full = Expert.Create("a", 1, ["finance"]);
        full.AcceptAssignment(T0);

        AssertRule("expert-unavailable", () => request.AssignTo(full, T0));

        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Null(request.ExpertId);
    }

    [Fact]
    public void Closing_an_assigned_request_frees_capacity()
    {
        var expert = Expert.Create("a", 1, ["finance"]);
        var request = NewRequest();
        request.AssignTo(expert, T0);

        request.Close(expert, T0);

        Assert.Equal(RequestStatus.Closed, request.Status);
        Assert.Equal(0, expert.OpenCount);
    }

    [Fact]
    public void Capacity_cannot_drop_below_open_count()
    {
        var expert = Expert.Create("a", 2, ["finance"]);
        expert.AcceptAssignment(T0);
        expert.AcceptAssignment(T0);

        AssertRule("capacity-below-open-count", () => expert.ChangeCapacity(1));
    }

    [Fact]
    public void An_expert_needs_at_least_one_topic() =>
        AssertRule("expert-needs-topic", () => Expert.Create("a", 1, []));

    [Theory]
    [InlineData("Finance ", "finance")]
    [InlineData("LEGAL", "legal")]
    [InlineData("tax-2", "tax-2")]
    public void Topics_are_normalized(string raw, string expected) =>
        Assert.Equal(expected, TopicCode.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("مالی")]
    public void Invalid_topics_are_rejected(string raw) =>
        AssertRule("invalid-topic", () => TopicCode.Normalize(raw));
}
