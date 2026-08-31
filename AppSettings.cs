namespace InterviewTranscriberV5;

public sealed class AppSettings
{
    public int ContextIndex { get; set; } = 1;
    public int UpdateIndex { get; set; } = 1;
    public int VadIndex { get; set; }
    public int SilenceIndex { get; set; } = 2;
    public string? CaptureDeviceId { get; set; }
}
