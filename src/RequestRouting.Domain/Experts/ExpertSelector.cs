namespace RequestRouting.Domain.Experts;

/// <summary>
/// Picks the expert for a request from candidates that already match its topic.
/// <list type="number">
/// <item>Lowest open/capacity ratio: the rule the task asks for.</item>
/// <item>Tie: longest since last assignment, never-assigned first. Without this, every tie would go to
/// the same expert, and idle experts would be filled in id order.</item>
/// <item>Tie: lowest id. This makes the order total, so every app instance decides the same way.</item>
/// </list>
/// </summary>
public static class ExpertSelector
{
    public static Expert? PickBest(IEnumerable<Expert> candidates)
    {
        Expert? best = null;

        foreach (var candidate in candidates)
        {
            if (candidate.CanAccept && (best is null || IsBetter(candidate, best)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsBetter(Expert a, Expert b)
    {
        // a.Open / a.Capacity < b.Open / b.Capacity, cross-multiplied to stay exact with no floating point.
        var ratio = ((long)a.OpenCount * b.Capacity).CompareTo((long)b.OpenCount * a.Capacity);
        if (ratio != 0)
        {
            return ratio < 0;
        }

        // Nullable comparison sorts null first, so an expert who has never been assigned wins.
        var recency = Nullable.Compare(a.LastAssignedAt, b.LastAssignedAt);
        return recency != 0 ? recency < 0 : a.Id < b.Id;
    }
}
