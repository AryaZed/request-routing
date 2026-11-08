using RequestRouting.Domain.Experts;

namespace RequestRouting.Application;

public sealed record CreateExpertCommand(string Name, int Capacity, IReadOnlyCollection<string> Topics, bool IsActive);

/// <summary>Null members are left unchanged.</summary>
public sealed record UpdateExpertCommand(int? Capacity, bool? IsActive, IReadOnlyCollection<string>? Topics);

public sealed record ExpertView(
    int Id,
    string Name,
    int Capacity,
    int OpenCount,
    bool IsActive,
    IReadOnlyList<string> Topics,
    DateTimeOffset? LastAssignedAt)
{
    public static ExpertView From(Expert e) => new(
        e.Id, e.Name, e.Capacity, e.OpenCount, e.IsActive,
        [.. e.Topics.Select(t => t.Topic).Order()], e.LastAssignedAt);
}

public sealed class ExpertService(IAssignmentStore store, Dispatcher dispatcher)
{
    public async Task<ExpertView> CreateAsync(CreateExpertCommand command, CancellationToken ct)
    {
        var expert = await store.InTransactionAsync(
            (session, _) =>
            {
                var created = Expert.Create(command.Name, command.Capacity, command.Topics, command.IsActive);
                session.Add(created);
                return Task.FromResult(created);
            },
            ct);

        // A new expert may be exactly what the waiting queue needs.
        await dispatcher.TryDrainAsync();
        return await GetAsync(expert.Id, CancellationToken.None) ?? ExpertView.From(expert);
    }

    public async Task<ExpertView?> UpdateAsync(int id, UpdateExpertCommand command, CancellationToken ct)
    {
        var found = await store.InTransactionAsync(
            async (session, c) =>
            {
                if (await session.LockExpertWithTopicsAsync(id, c) is not { } expert)
                {
                    return false;
                }

                if (command.Capacity is { } capacity)
                {
                    expert.ChangeCapacity(capacity);
                }

                if (command.IsActive is { } isActive)
                {
                    expert.SetActive(isActive);
                }

                if (command.Topics is { } topics)
                {
                    expert.ReplaceTopics(topics);
                }

                return true;
            },
            ct);

        if (!found)
        {
            return null;
        }

        // Reactivating, adding capacity or adding a topic can all unblock waiting requests.
        await dispatcher.TryDrainAsync();
        return await GetAsync(id, CancellationToken.None);
    }

    public async Task<ExpertView?> GetAsync(int id, CancellationToken ct) =>
        await store.GetExpertAsync(id, ct) is { } expert ? ExpertView.From(expert) : null;
}
