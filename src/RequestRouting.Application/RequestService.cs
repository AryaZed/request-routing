using Microsoft.Extensions.Options;
using RequestRouting.Domain;
using RequestRouting.Domain.Requests;

namespace RequestRouting.Application;

public sealed record SubmitRequestCommand(string RequestKey, string Topic, string Title, RequestPriority Priority);

public sealed record SubmitRequestResult(RequestView Request, bool Created);

public sealed record RequestView(
    long Id,
    string RequestKey,
    string Topic,
    string Title,
    RequestPriority Priority,
    RequestStatus Status,
    int? ExpertId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ClosedAt)
{
    public static RequestView From(ServiceRequest r) => new(
        r.Id, r.RequestKey, r.Topic, r.Title, r.Priority, r.Status, r.ExpertId, r.CreatedAt, r.AssignedAt, r.ClosedAt);
}

public sealed class RequestService(
    IAssignmentStore store,
    Dispatcher dispatcher,
    TimeProvider clock,
    IOptions<QueueOptions> options)
{
    public async Task<SubmitRequestResult> SubmitAsync(SubmitRequestCommand command, CancellationToken ct)
    {
        // The read is only an optimisation for retries; the unique index below is the real guarantee.
        if (await store.FindByKeyAsync(command.RequestKey, ct) is { } existing)
        {
            return Replay(existing, command);
        }

        var request = ServiceRequest.Submit(
            command.RequestKey,
            command.Topic,
            command.Title,
            command.Priority,
            clock.GetUtcNow(),
            options.Value.UrgentHeadStart);

        if (!await store.TryInsertAsync(request, ct))
        {
            var winner = await store.FindByKeyAsync(command.RequestKey, ct)
                ?? throw new InvalidOperationException($"Key '{command.RequestKey}' conflicted but cannot be found.");
            return Replay(winner, command);
        }

        await dispatcher.TryDrainAsync();

        // The request is committed, so this read must not be cancelled by a client disconnecting either.
        var current = await store.GetRequestAsync(request.Id, CancellationToken.None);
        return new SubmitRequestResult(RequestView.From(current ?? request), Created: true);
    }

    /// <summary>
    /// Closing is idempotent: closing a closed request changes nothing, so a retried or concurrent close
    /// can never release the expert's capacity twice.
    /// </summary>
    public async Task<RequestView?> CloseAsync(long id, CancellationToken ct)
    {
        var (found, freedCapacity) = await store.InTransactionAsync(
            async (session, c) =>
            {
                if (await session.LockRequestAsync(id, c) is not { } request)
                {
                    return (false, false);
                }

                if (request.Status == RequestStatus.Closed)
                {
                    return (true, false);
                }

                var assignee = request.ExpertId is { } expertId ? await session.LockExpertAsync(expertId, c) : null;
                request.Close(assignee, clock.GetUtcNow());
                return (true, assignee is not null);
            },
            ct);

        if (!found)
        {
            return null;
        }

        if (freedCapacity)
        {
            await dispatcher.TryDrainAsync();
        }

        var current = await store.GetRequestAsync(id, CancellationToken.None);
        return current is null ? null : RequestView.From(current);
    }

    public async Task<RequestView?> GetAsync(long id, CancellationToken ct) =>
        await store.GetRequestAsync(id, ct) is { } request ? RequestView.From(request) : null;

    public async Task<IReadOnlyList<RequestView>> ListPendingAsync(int take, CancellationToken ct)
    {
        var pending = await store.ListPendingAsync(take, ct);
        return [.. pending.Select(RequestView.From)];
    }

    private static SubmitRequestResult Replay(ServiceRequest existing, SubmitRequestCommand command)
    {
        if (!existing.Matches(command.Topic, command.Title, command.Priority))
        {
            throw new DomainRuleException(
                "request-key-reused",
                $"RequestKey '{command.RequestKey}' was already used for a different request.");
        }

        return new SubmitRequestResult(RequestView.From(existing), Created: false);
    }
}
