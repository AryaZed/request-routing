using System.Net;
using Microsoft.Data.SqlClient;
using RequestRouting.Application;
using RequestRouting.Domain.Requests;

namespace RequestRouting.IntegrationTests;

/// <summary>Requirement 2. Each scenario sends traffic through both application instances at once.</summary>
public class ConcurrencyTests(SqlServerFixture server)
{
    private const string AssignedCount = "SELECT COUNT(*) FROM ServiceRequests WHERE Status = 2";
    private const string PendingCount = "SELECT COUNT(*) FROM ServiceRequests WHERE Status = 1";

    private static HttpClient Via(TestApp app, int i) => i % 2 == 0 ? app.InstanceA : app.InstanceB;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Twenty_concurrent_requests_for_capacity_five_assign_exactly_five(bool readCommittedSnapshot)
    {
        await using var app = await TestApp.StartAsync(server, readCommittedSnapshot);
        var expert = await app.CreateExpertAsync("only", capacity: 5, "finance");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => app.SubmitAsync("finance", via: Via(app, i))));

        Assert.Equal(20, results.Select(r => r.Id).Distinct().Count());
        Assert.Equal(5, await app.ScalarAsync(AssignedCount));
        Assert.Equal(15, await app.ScalarAsync(PendingCount));
        Assert.Equal(5, (await app.GetExpertAsync(expert.Id)).OpenCount);

        // Without this, the CHECK constraint could hide broken locking by rejecting the excess assignments.
        Assert.Equal(0, app.DispatchFailures);
    }

    [Fact]
    public async Task An_expert_is_not_assigned_a_topic_removed_while_waiting_for_their_lock()
    {
        await using var app = await TestApp.StartAsync(server, readCommittedSnapshot: true);
        var expert = await app.CreateExpertAsync("only", capacity: 5, "finance", "legal");

        // An in-progress topic change: it holds the expert's lock and has not committed yet.
        await using var connection = new SqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var change = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT Id FROM Experts WITH (UPDLOCK, ROWLOCK) WHERE Id = {expert.Id};
            DELETE FROM ExpertTopics WHERE ExpertId = {expert.Id} AND Topic = 'finance';
            """,
            connection,
            change);
        await command.ExecuteNonQueryAsync();

        // The dispatcher still sees the committed topic, so it goes on to wait for the expert's lock.
        var submit = app.SubmitAsync("finance");
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await change.CommitAsync();

        var request = await submit;

        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Equal(0, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Fact]
    public async Task Concurrent_requests_are_balanced_between_experts()
    {
        await using var app = await TestApp.StartAsync(server);
        var first = await app.CreateExpertAsync("first", capacity: 10, "finance");
        var second = await app.CreateExpertAsync("second", capacity: 10, "finance");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => app.SubmitAsync("finance", via: Via(app, i))));

        // Stale reads would pile work onto one expert. Locking before comparing keeps this an exact split.
        Assert.Equal(5, (await app.GetExpertAsync(first.Id)).OpenCount);
        Assert.Equal(5, (await app.GetExpertAsync(second.Id)).OpenCount);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_RequestKey_create_one_request()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", capacity: 5, "finance");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => app.PostRequestAsync("same-key", "finance", via: Via(app, i))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        var requests = await Task.WhenAll(responses.Select(TestApp.ReadAsync<RequestView>));
        Assert.Single(requests.Select(r => r.Id).Distinct());
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ServiceRequests"));
        Assert.Equal(1, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Fact]
    public async Task A_retry_returns_the_original_request_with_its_current_state()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.CreateExpertAsync("only", capacity: 1, "finance");

        var first = await app.PostRequestAsync("key", "finance");
        var retry = await app.PostRequestAsync("key", "finance", via: app.InstanceB);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await TestApp.ReadAsync<RequestView>(retry);
        Assert.Equal((await TestApp.ReadAsync<RequestView>(first)).Id, replayed.Id);
        Assert.Equal(RequestStatus.Assigned, replayed.Status);
    }

    [Fact]
    public async Task Reusing_a_RequestKey_for_a_different_request_is_rejected()
    {
        await using var app = await TestApp.StartAsync(server);
        await app.PostRequestAsync("key", "finance");

        var conflict = await app.PostRequestAsync("key", "legal");

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ServiceRequests"));
    }

    [Fact]
    public async Task Concurrent_closes_of_one_request_release_capacity_once()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", capacity: 2, "finance");
        var closing = await app.SubmitAsync("finance");
        await app.SubmitAsync("finance");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => app.CloseAsync(closing.Id, Via(app, i))));

        Assert.Equal(1, (await app.GetExpertAsync(expert.Id)).OpenCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invariants_hold_under_mixed_load_with_overlapping_topics(bool readCommittedSnapshot)
    {
        await using var app = await TestApp.StartAsync(server, readCommittedSnapshot);

        // Each expert shares a topic with both others, the shape most likely to produce lock cycles.
        var experts = new[]
        {
            await app.CreateExpertAsync("ab", 10, "a", "b"),
            await app.CreateExpertAsync("bc", 10, "b", "c"),
            await app.CreateExpertAsync("ca", 10, "c", "a"),
        };
        string[] topics = ["a", "b", "c"];

        await Task.WhenAll(Enumerable.Range(0, 90).Select(i =>
            app.SubmitAsync(topics[i % 3], i % 5 == 0 ? RequestPriority.Urgent : RequestPriority.Normal, Via(app, i))));

        Assert.Equal(30, await app.ScalarAsync(AssignedCount));
        Assert.Equal(60, await app.ScalarAsync(PendingCount));

        // Closing all 30 at once releases capacity from many transactions while queued work is reassigned.
        var assigned = await app.IdsAsync("SELECT Id FROM ServiceRequests WHERE Status = 2");
        await Task.WhenAll(assigned.Select((id, i) => app.CloseAsync(id, Via(app, i))));

        Assert.Equal(30, await app.ScalarAsync("SELECT COUNT(*) FROM ServiceRequests WHERE Status = 3"));
        Assert.Equal(30, await app.ScalarAsync(AssignedCount));
        Assert.Equal(30, await app.ScalarAsync(PendingCount));

        foreach (var expert in experts)
        {
            var open = (await app.GetExpertAsync(expert.Id)).OpenCount;
            var actual = await app.ScalarAsync($"SELECT COUNT(*) FROM ServiceRequests WHERE Status = 2 AND ExpertId = {expert.Id}");
            Assert.Equal(actual, open);
            Assert.Equal(10, open);
        }

        Assert.Equal(0, app.DispatchFailures);
    }

    [Fact]
    public async Task The_database_itself_refuses_to_exceed_capacity()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", capacity: 1, "finance");

        var error = await Assert.ThrowsAsync<SqlException>(() =>
            app.ExecuteSqlAsync($"UPDATE Experts SET OpenCount = 2 WHERE Id = {expert.Id}"));

        Assert.Contains("CK_Experts_OpenCount", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_pending_request_with_an_expert()
    {
        await using var app = await TestApp.StartAsync(server);
        var expert = await app.CreateExpertAsync("only", capacity: 1, "finance");
        var request = await app.SubmitAsync("legal");

        var error = await Assert.ThrowsAsync<SqlException>(() =>
            app.ExecuteSqlAsync($"UPDATE ServiceRequests SET ExpertId = {expert.Id} WHERE Id = {request.Id}"));

        Assert.Contains("CK_ServiceRequests_StatusExpert", error.Message, StringComparison.Ordinal);
    }
}
