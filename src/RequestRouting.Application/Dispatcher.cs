using Microsoft.Extensions.Logging;
using RequestRouting.Domain.Experts;

namespace RequestRouting.Application;

/// <summary>
/// The only code that assigns requests. Every state change that could make an assignment possible commits
/// first and then calls this, so whichever change commits last is guaranteed to see all the others.
/// </summary>
public sealed partial class Dispatcher(IAssignmentStore store, TimeProvider clock, ILogger<Dispatcher> logger)
{
    // Raising one expert's capacity could otherwise make a single HTTP call assign thousands of requests.
    private const int MaxAssignmentsPerCaller = 50;

    /// <summary>Assigns queued requests until none can be served. Safe to run from any number of instances.</summary>
    public Task<int> DrainAsync(CancellationToken ct) => DrainAsync(int.MaxValue, ct);

    private async Task<int> DrainAsync(int maxAssignments, CancellationToken ct)
    {
        var assigned = 0;

        while (assigned < maxAssignments
            && await store.PeekNextDispatchableTopicAsync(ct) is not null
            && await store.InTransactionAsync(AssignNextAsync, ct))
        {
            assigned++;
        }

        return assigned;
    }

    /// <summary>
    /// For use after a change has committed. The change stands whatever happens here, so a failure must not
    /// be reported to the caller as if it had failed: it is logged, and the sweeper finishes the job. It also
    /// ignores the caller's cancellation, because a client disconnecting must not abandon the queue.
    /// </summary>
    public async Task TryDrainAsync()
    {
        try
        {
            await DrainAsync(MaxAssignmentsPerCaller, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogDrainFailed(logger, ex);
        }
    }

    private async Task<bool> AssignNextAsync(IAssignmentSession session, CancellationToken ct)
    {
        // One assignment at a time, across all instances, so that everything read below is current when the
        // decision is made. Row locks alone were measured to be slower: see docs/DECISIONS.md.
        await session.AcquireDispatchLockAsync(ct);

        if (await session.NextDispatchableTopicAsync(ct) is not { } topic)
        {
            return false;
        }

        // Experts are still locked, because closing a request and editing an expert don't take the dispatch lock.
        var candidates = await session.LockActiveExpertsForTopicAsync(topic, ct);

        if (ExpertSelector.PickBest(candidates) is not { } expert)
        {
            return false;
        }

        if (await session.LockNextPendingAsync(topic, ct) is not { } request)
        {
            return false;
        }

        request.AssignTo(expert, clock.GetUtcNow());
        return true;
    }

    [LoggerMessage(LogLevel.Warning, "Dispatch after commit failed; the queue sweeper will retry.")]
    private static partial void LogDrainFailed(ILogger logger, Exception exception);
}
