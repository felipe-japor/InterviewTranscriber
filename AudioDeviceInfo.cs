namespace InterviewTranscriberV5;

public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string FriendlyName { get; init; }
    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}
