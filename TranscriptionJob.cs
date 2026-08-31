namespace InterviewTranscriberV5;

public sealed record TranscriptionJob(
    float[] Samples,
    long Sequence,
    bool IsFinal);
