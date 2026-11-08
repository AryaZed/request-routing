using System.Text.RegularExpressions;

namespace RequestRouting.Domain;

/// <summary>
/// Topics are machine codes ("finance", "legal"), not display text, so that matching a request to an
/// expert never depends on database collation or on Persian/Arabic letter variants.
/// </summary>
public static partial class TopicCode
{
    public const int MaxLength = 50;
    public const string Pattern = "^[a-z0-9][a-z0-9-]*$";

    public static string Normalize(string raw)
    {
        var code = raw.Trim().ToLowerInvariant();

        if (code.Length is 0 or > MaxLength || !ValidCode().IsMatch(code))
        {
            throw new DomainRuleException("invalid-topic", $"'{raw}' is not a valid topic code.");
        }

        return code;
    }

    [GeneratedRegex(Pattern)]
    private static partial Regex ValidCode();
}
