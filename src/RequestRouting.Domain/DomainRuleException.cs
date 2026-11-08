namespace RequestRouting.Domain;

/// <summary>An operation would break a business rule. <see cref="Code"/> is stable for API clients.</summary>
public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
