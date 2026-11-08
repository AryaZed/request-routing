using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RequestRouting.Application;
using RequestRouting.Domain;
using RequestRouting.Domain.Experts;
using RequestRouting.Domain.Requests;

namespace RequestRouting.Infrastructure.Persistence;

/// <summary>
/// All SQL Server specifics live here. The queue queries are hand-written so the literal <c>Status = 1</c>
/// matches the filtered indexes and the lock hints are visible rather than hidden behind an interceptor.
/// </summary>
public sealed class SqlAssignmentStore(IDbContextFactory<AppDbContext> contexts) : IAssignmentStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DispatchLockTimeoutMs = 20_000;

    public async Task<T> InTransactionAsync<T>(
        Func<IAssignmentSession, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(ct);

        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(
            async cancellation =>
            {
                // A new context per attempt, and per transaction: EF does not overwrite entities it is already
                // tracking, so reusing one would hide the fresh values a lock has just read.
                await using var db = await contexts.CreateDbContextAsync(cancellation);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellation);

                var result = await work(new Session(db), cancellation);

                await db.SaveChangesAsync(cancellation);
                await transaction.CommitAsync(cancellation);
                return result;
            },
            ct);
    }

    public async Task<string?> PeekNextDispatchableTopicAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await NextDispatchableTopicAsync(db, ct);
    }

    private static async Task<string?> NextDispatchableTopicAsync(AppDbContext db, CancellationToken ct)
    {
        var topics = await db.Database.SqlQuery<string>($"""
            SELECT TOP (1) head.Topic AS Value
            FROM (
                SELECT DISTINCT t.Topic
                FROM ExpertTopics AS t
                JOIN Experts AS e ON e.Id = t.ExpertId
                WHERE e.IsActive = 1 AND e.OpenCount < e.Capacity) AS servable
            CROSS APPLY (
                SELECT TOP (1) r.Topic, r.QueueRank, r.Id
                FROM ServiceRequests AS r
                WHERE r.Status = 1 AND r.Topic = servable.Topic
                ORDER BY r.QueueRank, r.Id) AS head
            ORDER BY head.QueueRank, head.Id
            """).ToListAsync(ct);

        return topics.Count == 0 ? null : topics[0];
    }

    public async Task<bool> TryInsertAsync(ServiceRequest request, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.ServiceRequests.Add(request);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException
        {
            Number: UniqueIndexViolation or UniqueConstraintViolation
        })
        {
            return false;
        }
    }

    public async Task<ServiceRequest?> FindByKeyAsync(string requestKey, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(r => r.RequestKey == requestKey, ct);
    }

    public async Task<ServiceRequest?> GetRequestAsync(long id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<Expert?> GetExpertAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Experts.AsNoTracking().Include(e => e.Topics).SingleOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<ServiceRequest>> ListPendingAsync(int take, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ServiceRequests
            .FromSql($"SELECT TOP ({take}) * FROM ServiceRequests WHERE Status = 1 ORDER BY QueueRank, Id")
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // Raw SQL parameters default to nvarchar. Against this varchar column that forces a conversion, which turns
    // an index seek into a range scan through every waiting request: measured at 605 reads against a 100k queue.
    private static SqlParameter TopicParameter(string topic) =>
        new() { SqlDbType = SqlDbType.VarChar, Size = TopicCode.MaxLength, Value = topic };

    private sealed class Session(AppDbContext db) : IAssignmentSession
    {
        public async Task AcquireDispatchLockAsync(CancellationToken ct)
        {
            var results = await db.Database.SqlQuery<int>($"""
                DECLARE @result int;
                EXEC @result = sp_getapplock
                    @Resource = 'RequestRouting.Dispatch',
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = {DispatchLockTimeoutMs};
                SELECT @result AS Value;
                """).ToListAsync(ct);

            // Zero or one mean granted. A failure here reaches TryDrainAsync, and the sweeper tries again.
            if (results[0] < 0)
            {
                throw new TimeoutException($"Could not get the dispatch lock (sp_getapplock returned {results[0]}).");
            }
        }

        public Task<string?> NextDispatchableTopicAsync(CancellationToken ct) =>
            SqlAssignmentStore.NextDispatchableTopicAsync(db, ct);

        // UPDLOCK: at most one holder per row until commit, and the read sees the latest committed values
        // whether or not the database uses READ_COMMITTED_SNAPSHOT. ROWLOCK keeps unrelated rows free.
        public async Task<IReadOnlyList<Expert>> LockActiveExpertsForTopicAsync(string topic, CancellationToken ct)
        {
            // The plan is pinned because it decides which rows get locked. Left to itself the optimizer scans
            // small tables, which locks every expert in turn and makes unrelated topics block each other.
            // Reading the topic index first locks only this topic's experts, and always in id order.
            var locked = await db.Experts.FromSql($"""
                SELECT e.*
                FROM ExpertTopics AS t
                INNER LOOP JOIN Experts AS e WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON e.Id = t.ExpertId
                WHERE t.Topic = {TopicParameter(topic)} AND e.IsActive = 1
                ORDER BY t.ExpertId
                OPTION (FORCE ORDER)
                """).ToListAsync(ct);

            if (locked.Count == 0)
            {
                return locked;
            }

            // Under snapshot isolation the join above saw topics as they were when the statement began, which
            // may be before a wait for the lock. Changes to an expert's topics hold that same lock, so this
            // new statement sees the current topics of every expert now locked.
            var current = await db.Database
                .SqlQuery<int>($"SELECT t.ExpertId AS Value FROM ExpertTopics AS t WHERE t.Topic = {TopicParameter(topic)}")
                .ToListAsync(ct);

            return [.. locked.Where(e => current.Contains(e.Id))];
        }

        public async Task<ServiceRequest?> LockNextPendingAsync(string topic, CancellationToken ct)
        {
            var head = await db.ServiceRequests.FromSql($"""
                SELECT TOP (1) r.*
                FROM ServiceRequests AS r WITH (UPDLOCK, ROWLOCK)
                WHERE r.Status = 1 AND r.Topic = {TopicParameter(topic)}
                ORDER BY r.QueueRank, r.Id
                """).ToListAsync(ct);

            return head.Count == 0 ? null : head[0];
        }

        public async Task<ServiceRequest?> LockRequestAsync(long id, CancellationToken ct)
        {
            var rows = await db.ServiceRequests
                .FromSql($"SELECT * FROM ServiceRequests WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
                .ToListAsync(ct);

            return rows.Count == 0 ? null : rows[0];
        }

        public async Task<Expert?> LockExpertAsync(int id, CancellationToken ct)
        {
            var rows = await db.Experts
                .FromSql($"SELECT * FROM Experts WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
                .ToListAsync(ct);

            return rows.Count == 0 ? null : rows[0];
        }

        public async Task<Expert?> LockExpertWithTopicsAsync(int id, CancellationToken ct)
        {
            if (await LockExpertAsync(id, ct) is not { } expert)
            {
                return null;
            }

            await db.Entry(expert).Collection(e => e.Topics).LoadAsync(ct);
            return expert;
        }

        public void Add(Expert expert) => db.Experts.Add(expert);
    }
}
