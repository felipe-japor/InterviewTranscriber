using NAudio.CoreAudioApi;

namespace InterviewTranscriberV5;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetVoicemeeterCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Where(IsVoicemeeterDevice)
            .Select(device => new AudioDeviceInfo
            {
                Id = device.ID,
                FriendlyName = device.FriendlyName,
                DisplayName = IsRecommended(device.FriendlyName)
                    ? $"Recommended → {device.FriendlyName}"
                    : device.FriendlyName
            })
            .OrderByDescending(IsRecommended)
            .ThenBy(device => device.FriendlyName)
            .ToList();
    }

    public static bool IsRecommended(AudioDeviceInfo device) =>
        IsRecommended(device.FriendlyName);

    private static bool IsVoicemeeterDevice(MMDevice device) =>
        device.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecommended(string name) =>
        (name.Contains("Voicemeeter Output", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Out B1", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("B1", StringComparison.OrdinalIgnoreCase)) &&
        !name.Contains("AUX", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("VAIO3", StringComparison.OrdinalIgnoreCase);
}
