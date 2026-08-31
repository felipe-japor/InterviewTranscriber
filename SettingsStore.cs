using System.IO;
using System.Text.Json;

namespace InterviewTranscriberV5;

public sealed class SettingsStore
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InterviewTranscriber",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are optional and must never interrupt transcription.
        }
    }
}
