using NAudio.Utils;
using NAudio.Wave;
using System.IO;

namespace InterviewTranscriberV5;

public static class AudioSampleConverter
{
    public static float[] ToMono(byte[] buffer, int bytesRecorded, WaveFormat format) =>
        (format.Encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => FloatToMono(buffer, bytesRecorded, format.Channels),
            (WaveFormatEncoding.Pcm, 16) => Pcm16ToMono(buffer, bytesRecorded, format.Channels),
            _ => throw new NotSupportedException(
                $"Unsupported capture format: {format.Encoding}, {format.BitsPerSample}-bit, {format.SampleRate} Hz")
        };

    public static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return input;
        if (input.Length == 0) return [];

        int outputLength = (int)Math.Round(input.Length * (double)targetRate / sourceRate);
        var output = new float[outputLength];
        double ratio = (double)sourceRate / targetRate;

        for (int i = 0; i < outputLength; i++)
        {
            double position = i * ratio;
            int index = (int)position;
            double fraction = position - index;
            output[i] = index >= input.Length - 1
                ? input[^1]
                : (float)(input[index] * (1 - fraction) + input[index + 1] * fraction);
        }

        return output;
    }

    public static double CalculateRms(float[] samples) =>
        samples.Length == 0 ? 0 : Math.Sqrt(samples.Sum(sample => sample * sample) / samples.Length);

    public static MemoryStream CreateWav(float[] samples, int sampleRate)
    {
        var stream = new MemoryStream();
        using (var nonClosingStream = new IgnoreDisposeStream(stream))
        using (var writer = new WaveFileWriter(nonClosingStream, new WaveFormat(sampleRate, 16, 1)))
        {
            foreach (float sample in samples)
            {
                short pcm = (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
                writer.WriteByte((byte)(pcm & 0xff));
                writer.WriteByte((byte)((pcm >> 8) & 0xff));
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static float[] FloatToMono(byte[] buffer, int bytesRecorded, int channels)
    {
        int frames = bytesRecorded / (4 * channels);
        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
                sum += BitConverter.ToSingle(buffer, (frame * channels + channel) * 4);
            mono[frame] = (float)(sum / channels);
        }
        return mono;
    }

    private static float[] Pcm16ToMono(byte[] buffer, int bytesRecorded, int channels)
    {
        int frames = bytesRecorded / (2 * channels);
        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
                sum += BitConverter.ToInt16(buffer, (frame * channels + channel) * 2) / 32768f;
            mono[frame] = (float)(sum / channels);
        }
        return mono;
    }
}
