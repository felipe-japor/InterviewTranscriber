namespace InterviewTranscriberV5;

public sealed record RuntimeSettings(
    int ContextSamples,
    int UpdateSamples,
    bool VadEnabled,
    double EndSilenceMs);
