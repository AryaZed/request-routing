using RequestRouting.Domain.Experts;
using RequestRouting.Domain.Requests;

namespace RequestRouting.Application;

/// <summary>Persistence port. It speaks in the operations this system needs, not generic CRUD.</summary>
public interface IAssignmentStore
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction and commits the tracked changes. On a transient
    /// failure, such as being chosen as a deadlock victim, the whole delegate runs again on fresh state,
    /// so it must have no side effects outside the session.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<IAssignmentSession, CancellationToken, Task<T>> work, CancellationToken ct);

    /// <summary>
    /// Topic of the first request in queue order that has an available expert, read without locks, so that
    /// callers with nothing to do never queue for the dispatch lock. Requests nobody can serve are skipped
    /// here, so they never hold up the rest of the queue.
    /// </summary>
    Task<string?> PeekNextDispatchableTopicAsync(CancellationToken ct);

    /// <summary>Inserts the request. Returns false if another request already holds its key.</summary>
    Task<bool> TryInsertAsync(ServiceRequest request, CancellationToken ct);

    Task<ServiceRequest?> FindByKeyAsync(string requestKey, CancellationToken ct);
    Task<ServiceRequest?> GetRequestAsync(long id, CancellationToken ct);
    Task<Expert?> GetExpertAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<ServiceRequest>> ListPendingAsync(int take, CancellationToken ct);
}

/// <summary>
/// Locks taken here are held until the transaction ends. To stay free of lock cycles, dispatching takes
/// the dispatch lock, then experts (in id order), then one pending request, and closing locks its request
/// and then its expert. A request being closed is never the pending request a dispatcher is waiting for
/// while that closer also waits on an expert, because only assigned requests need the expert lock.
/// </summary>
public interface IAssignmentSession
{
    /// <summary>Waits until this transaction is the only one assigning requests, across all instances.</summary>
    Task AcquireDispatchLockAsync(CancellationToken ct);

    /// <summary>The same question as <see cref="IAssignmentStore.PeekNextDispatchableTopicAsync"/>, inside the lock.</summary>
    Task<string?> NextDispatchableTopicAsync(CancellationToken ct);

    Task<IReadOnlyList<Expert>> LockActiveExpertsForTopicAsync(string topic, CancellationToken ct);
    Task<ServiceRequest?> LockNextPendingAsync(string topic, CancellationToken ct);
    Task<ServiceRequest?> LockRequestAsync(long id, CancellationToken ct);
    Task<Expert?> LockExpertAsync(int id, CancellationToken ct);
    Task<Expert?> LockExpertWithTopicsAsync(int id, CancellationToken ct);
    void Add(Expert expert);
}
