using System.Net;
using System.Net.Http.Json;
using RequestRouting.Domain.Requests;

namespace RequestRouting.IntegrationTests;

/// <summary>Requirement 1, end to end through the API.</summary>
public class ExpertSelectionTests(SqlServerFixture server)
{
    [Fact]
    public async Task The_example_from_the_task_picks_the_second_expert()
    {
        await using var app = await TestApp.StartAsync(server);

        // Private topics let each expert be loaded to a known level before the shared one is used.
        var first = await app.CreateExpertAsync("first", 4, "finance", "only-first");
        var second = await app.CreateExpertAsync("second", 10, "finance", "only-second");
        for (var i = 0; i < 2; i++)
        {
            await app.SubmitAsync("only-first");
        }

        for (var i = 0; i < 3; i++)
        {
            await app.SubmitAsync("only-second");
        }

        var request = await app.SubmitAsync("finance");

        Assert.Equal(second.Id, request.ExpertId);
        Assert.Equal(2, (await app.GetExpertAsync(first.Id)).OpenCount);
        Assert.Equal(4, (await app.GetExpertAsync(second.Id)).OpenCount);
    }

    [Fact]
    public async Task Ties_go_to_the_expert_idle_longest_then_the_lowest_id()
    {
        await using var app = await TestApp.StartAsync(server);
        var first = await app.CreateExpertAsync("first", 2, "finance");
        var second = await app.CreateExpertAsync("second", 2, "finance");

        // Both never assigned at 0/2: the lowest id decides.
        Assert.Equal(first.Id, (await app.SubmitAsync("finance")).ExpertId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        // 1/2 against 0/2: the ratio decides.
        Assert.Equal(second.Id, (await app.SubmitAsync("finance")).ExpertId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        // Both at 1/2: the first expert has waited longer since their last assignment.
        Assert.Equal(first.Id, (await app.SubmitAsync("finance")).ExpertId);
    }

    [Fact]
    public async Task Only_experts_with_the_topic_are_considered()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("legal", 5, "legal");
        var finance = await app.CreateExpertAsync("finance", 5, "finance");

        Assert.Equal(finance.Id, (await app.SubmitAsync("finance")).ExpertId);
    }

    [Fact]
    public async Task A_request_waits_when_no_expert_is_suitable()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("legal", 5, "legal");

        var request = await app.SubmitAsync("finance");

        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Null(request.ExpertId);
    }

    [Fact]
    public async Task Inactive_experts_get_no_new_requests_but_keep_their_open_ones()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 5, "finance");
        var open = await app.SubmitAsync("finance");

        await app.PatchExpertAsync(expert.Id, new { isActive = false });

        Assert.Equal(RequestStatus.Pending, (await app.SubmitAsync("finance")).Status);
        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(open.Id)).Status);
        Assert.Equal(1, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Fact]
    public async Task Capacity_cannot_be_reduced_below_open_requests()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 2, "finance");
        await app.SubmitAsync("finance");
        await app.SubmitAsync("finance");

        var response = await app.InstanceA.PatchAsJsonAsync($"/api/experts/{expert.Id}", new { capacity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(2, (await app.GetExpertAsync(expert.Id)).Capacity);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_it_reaches_the_domain()
    {
        await using var app = await TestApp.StartAsync(server);

        var noPriority = await app.InstanceA.PostAsJsonAsync("/api/requests", new { requestKey = "k", topic = "finance", title = "t" });
        var badTopic = await app.InstanceA.PostAsJsonAsync("/api/requests", new { requestKey = "k", topic = "not valid", title = "t", priority = "Normal" });
        var badPriority = await app.InstanceA.PostAsJsonAsync("/api/requests", new { requestKey = "k", topic = "finance", title = "t", priority = "Critical" });
        var noCapacity = await app.InstanceA.PostAsJsonAsync("/api/experts", new { name = "n", capacity = 0, topics = new[] { "finance" } });
        var noTopics = await app.InstanceA.PostAsJsonAsync("/api/experts", new { name = "n", capacity = 1, topics = Array.Empty<string>() });
        var badExpertTopic = await app.InstanceA.PostAsJsonAsync("/api/experts", new { name = "n", capacity = 1, topics = new[] { "Bad Topic!" } });
        var nullTopic = await app.InstanceA.PostAsJsonAsync("/api/experts", new { name = "n", capacity = 1, topics = new string?[] { null } });
        var malformed = await app.InstanceA.PostAsync("/api/requests", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        var badPage = await app.InstanceA.GetAsync("/api/requests/pending?take=0");

        Assert.Equal(HttpStatusCode.BadRequest, nullTopic.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badExpertTopic.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noPriority.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTopic.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPriority.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCapacity.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTopics.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [Fact]
    public async Task Unknown_ids_return_404()
    {
        await using var app = await TestApp.StartAsync(server);

        Assert.Equal(HttpStatusCode.NotFound, (await app.InstanceA.GetAsync("/api/requests/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.InstanceA.PostAsync("/api/requests/999/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.InstanceA.GetAsync("/api/experts/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.InstanceA.PatchAsJsonAsync("/api/experts/999", new { capacity = 1 })).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_report_the_database_is_reachable()
    {
        await using var app = await TestApp.StartAsync(server);

        Assert.Equal(HttpStatusCode.OK, (await app.InstanceA.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.InstanceA.GetAsync("/health/ready")).StatusCode);
    }
}
