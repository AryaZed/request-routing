namespace RequestRouting.Domain.Experts;

public sealed class ExpertTopic(string topic)
{
    public int ExpertId { get; private set; }
    public string Topic { get; private set; } = topic;
}
