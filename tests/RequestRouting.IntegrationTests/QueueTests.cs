using RequestRouting.Application;
using RequestRouting.Domain.Requests;

namespace RequestRouting.IntegrationTests;

/// <summary>Requirement 3. The default head start is 30 minutes.</summary>
public class QueueTests(SqlServerFixture server)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Closing_a_request_assigns_the_next_one_from_the_queue()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 1, "finance");
        var running = await app.SubmitAsync("finance");
        var waiting = await app.SubmitAsync("finance");
        Assert.Equal(RequestStatus.Pending, waiting.Status);

        await app.CloseAsync(running.Id, app.InstanceB);

        var next = await app.GetRequestAsync(waiting.Id);
        Assert.Equal(RequestStatus.Assigned, next.Status);
        Assert.Equal(expert.Id, next.ExpertId);
        Assert.Equal(1, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Fact]
    public async Task Urgent_is_served_before_an_older_normal_request()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("only", 1, "finance");
        var running = await app.SubmitAsync("finance");

        var normal = await app.SubmitAsync("finance");
        app.Clock.Advance(5 * Minute);
        var urgent = await app.SubmitAsync("finance", RequestPriority.Urgent);

        await app.CloseAsync(running.Id);

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(urgent.Id)).Status);
        Assert.Equal(RequestStatus.Pending, (await app.GetRequestAsync(normal.Id)).Status);
    }

    [Fact]
    public async Task A_normal_request_older_than_the_head_start_beats_a_new_urgent_one()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("only", 1, "finance");
        var running = await app.SubmitAsync("finance");

        var normal = await app.SubmitAsync("finance");
        app.Clock.Advance(31 * Minute);
        var urgent = await app.SubmitAsync("finance", RequestPriority.Urgent);

        await app.CloseAsync(running.Id);

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(normal.Id)).Status);
        Assert.Equal(RequestStatus.Pending, (await app.GetRequestAsync(urgent.Id)).Status);
    }

    [Fact]
    public async Task A_constant_stream_of_urgent_requests_cannot_starve_a_normal_one()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("only", 1, "finance");
        var current = await app.SubmitAsync("finance");
        var normal = await app.SubmitAsync("finance");

        // A new urgent request every 10 minutes, and the expert finishes exactly one in that time.
        // Urgent arrivals at +10 and +20 overtake it; the one at +30 is ranked level, and the older id wins.
        var rounds = 0;
        while ((await app.GetRequestAsync(normal.Id)).Status == RequestStatus.Pending)
        {
            Assert.True(++rounds <= 3, "The normal request should be served within the 30 minute head start.");

            app.Clock.Advance(10 * Minute);
            await app.SubmitAsync("finance", RequestPriority.Urgent);
            await app.CloseAsync(current.Id);
            current = await CurrentlyAssignedAsync(app);
        }

        Assert.Equal(3, rounds);
    }

    [Fact]
    public async Task A_request_with_no_suitable_expert_does_not_block_the_queue()
    {
        await using var app = await TestApp.StartAsync(server);

        // The oldest and most urgent request in the system, and nobody can serve it.
        var blocked = await app.SubmitAsync("legal", RequestPriority.Urgent);
        await app.CreateExpertAsync("finance", 1, "finance");
        var running = await app.SubmitAsync("finance");
        var waiting = await app.SubmitAsync("finance");

        await app.CloseAsync(running.Id);

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(waiting.Id)).Status);
        Assert.Equal(RequestStatus.Pending, (await app.GetRequestAsync(blocked.Id)).Status);
    }

    [Fact]
    public async Task A_freed_expert_takes_the_earliest_request_across_all_their_topics()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("both", 1, "finance", "legal");
        var running = await app.SubmitAsync("finance");
        var legal = await app.SubmitAsync("legal");
        app.Clock.Advance(Minute);
        var finance = await app.SubmitAsync("finance");

        await app.CloseAsync(running.Id);

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(legal.Id)).Status);
        Assert.Equal(RequestStatus.Pending, (await app.GetRequestAsync(finance.Id)).Status);
    }

    [Fact]
    public async Task The_pending_list_shows_the_order_the_queue_will_be_served_in()
    {
        await using var app = await TestApp.StartAsync(server);
        var normal = await app.SubmitAsync("finance");
        app.Clock.Advance(5 * Minute);
        var urgent = await app.SubmitAsync("finance", RequestPriority.Urgent);
        app.Clock.Advance(40 * Minute);
        var lateUrgent = await app.SubmitAsync("finance", RequestPriority.Urgent);

        var pending = await app.GetPendingAsync();

        Assert.Equal(new[] { urgent.Id, normal.Id, lateUrgent.Id }, pending.Select(r => r.Id));
    }

    [Fact]
    public async Task Closing_a_pending_request_removes_it_from_the_queue_without_touching_capacity()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 1, "finance");
        var running = await app.SubmitAsync("finance");
        var cancelled = await app.SubmitAsync("finance");

        var closed = await app.CloseAsync(cancelled.Id);

        Assert.Equal(RequestStatus.Closed, closed.Status);
        Assert.Null(closed.ExpertId);
        Assert.Equal(1, (await app.GetExpertAsync(expert.Id)).OpenCount);

        await app.CloseAsync(running.Id);
        Assert.Equal(RequestStatus.Closed, (await app.GetRequestAsync(cancelled.Id)).Status);
        Assert.Equal(0, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Fact]
    public async Task Reactivating_an_expert_drains_the_queue()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 1, "finance");
        await app.PatchExpertAsync(expert.Id, new { isActive = false });
        var waiting = await app.SubmitAsync("finance");

        await app.PatchExpertAsync(expert.Id, new { isActive = true });

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(waiting.Id)).Status);
    }

    [Fact]
    public async Task Raising_capacity_drains_the_queue()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", 1, "finance");
        await app.SubmitAsync("finance");
        var waiting = await app.SubmitAsync("finance");

        await app.PatchExpertAsync(expert.Id, new { capacity = 2 });

        Assert.Equal(RequestStatus.Assigned, (await app.GetRequestAsync(waiting.Id)).Status);
    }

    [Fact]
    public async Task Adding_a_new_expert_drains_the_queue()
    {
        await using var app = await TestApp.StartAsync(server);
        var waiting = await app.SubmitAsync("finance");

        var expert = await app.CreateExpertAsync("new", 1, "finance");

        Assert.Equal(expert.Id, (await app.GetRequestAsync(waiting.Id)).ExpertId);
    }

    [Fact]
    public async Task A_new_request_cannot_jump_ahead_of_one_already_waiting()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("only", 1, "finance");

        // A request stranded by a crash: committed as pending, but its process died before dispatching.
        await InsertStrandedRequestAsync(app, "stranded");

        var late = await app.SubmitAsync("finance");

        Assert.Equal(RequestStatus.Pending, late.Status);
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ServiceRequests WHERE RequestKey = 'stranded' AND Status = 2"));
    }

    [Fact]
    public async Task The_sweeper_recovers_a_request_stranded_by_a_crash()
    {
        await using var app = await TestApp.StartAsync(server, sweepInterval: TimeSpan.FromMilliseconds(100));
        await app.CreateExpertAsync("only", 1, "finance");

        await InsertStrandedRequestAsync(app, "stranded");

        const string assigned = "SELECT COUNT(*) FROM ServiceRequests WHERE RequestKey = 'stranded' AND Status = 2";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await app.ScalarAsync(assigned) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The sweeper did not assign the stranded request.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<RequestView> CurrentlyAssignedAsync(TestApp app)
    {
        var ids = await app.IdsAsync("SELECT Id FROM ServiceRequests WHERE Status = 2");
        return await app.GetRequestAsync(Assert.Single(ids));
    }

    private static Task InsertStrandedRequestAsync(TestApp app, string key) =>
        app.ExecuteSqlAsync($"""
            INSERT INTO ServiceRequests (RequestKey, Topic, Title, Priority, Status, QueueRank, CreatedAt)
            VALUES ('{key}', 'finance', 't', 1, 1, '2025-01-01T00:00:00+00:00', '2025-01-01T00:00:00+00:00')
            """);
}
