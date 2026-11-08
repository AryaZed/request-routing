namespace RequestRouting.Domain.Requests;

public enum RequestPriority : byte
{
    Normal = 1,
    Urgent = 2,
}

public enum RequestStatus : byte
{
    Pending = 1,
    Assigned = 2,
    Closed = 3,
}
