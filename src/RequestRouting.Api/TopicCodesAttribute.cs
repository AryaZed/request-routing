using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using RequestRouting.Domain;

namespace RequestRouting.Api;

/// <summary>Validates every element of a topic list, which <see cref="RegularExpressionAttribute"/> cannot do.</summary>
public sealed class TopicCodesAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) =>
        value is not IEnumerable<string?> topics
        || topics.All(t => t is { Length: <= TopicCode.MaxLength } && Regex.IsMatch(t, TopicCode.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)));

    public override string FormatErrorMessage(string name) =>
        $"Every item in {name} must be a topic code of at most {TopicCode.MaxLength} characters matching {TopicCode.Pattern}.";
}
