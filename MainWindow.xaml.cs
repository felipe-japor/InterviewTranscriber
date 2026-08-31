using System.IO;
using System.Windows;

namespace InterviewTranscriberV5;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AudioDeviceService _deviceService = new();
    private readonly TranscriptionEngine _engine = new();
    private AppSettings _settings = new();
    private bool _settingsLoaded;
    private string? _selectedDeviceId;
    private string? _selectedDeviceName;

    public MainWindow()
    {
        InitializeComponent();
        SubscribeToEngine();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsStore.Load();
        ApplySavedSettings();
        LoadDevices();
        _settingsLoaded = true;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        SaveSettings();
        await _engine.DisposeAsync();
    }

    private void SubscribeToEngine()
    {
        _engine.StatusChanged += text => Post(() => StatusText.Text = text);
        _engine.AudioLevelChanged += peak => Post(() => LevelText.Text = $"Audio: {(peak * 100):0}%");
        _engine.VadChanged += state => Post(() => VadText.Text = $"VAD: {state}");
        _engine.LatencyChanged += text => Post(() => LatencyText.Text = text);
        _engine.QueueChanged += text => Post(() => QueueText.Text = text);
        _engine.TranscriptChanged += text => Post(() => ReplaceTranscript(text));
    }

    private void LoadDevices()
    {
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = _deviceService.GetVoicemeeterCaptureDevices();
            DeviceCombo.ItemsSource = devices;
            DeviceCombo.SelectedItem =
                devices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    _settings.CaptureDeviceId,
                    StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(AudioDeviceService.IsRecommended)
                ?? devices.FirstOrDefault();

            StatusText.Text = devices.Count == 0
                ? "No Voicemeeter capture endpoint found."
                : $"{devices.Count} Voicemeeter capture endpoint(s) found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Device enumeration failed.";
            ShowError("Device enumeration error", ex);
        }
    }

    private void ApplySavedSettings()
    {
        ContextCombo.SelectedIndex = ClampIndex(_settings.ContextIndex, ContextCombo.Items.Count, 1);
        UpdateCombo.SelectedIndex = ClampIndex(_settings.UpdateIndex, UpdateCombo.Items.Count, 1);
        VadCombo.SelectedIndex = ClampIndex(_settings.VadIndex, VadCombo.Items.Count, 0);
        SilenceCombo.SelectedIndex = ClampIndex(_settings.SilenceIndex, SilenceCombo.Items.Count, 2);
    }

    private void SaveSettings()
    {
        if (!IsLoaded) return;
        _settings.ContextIndex = ContextCombo.SelectedIndex;
        _settings.UpdateIndex = UpdateCombo.SelectedIndex;
        _settings.VadIndex = VadCombo.SelectedIndex;
        _settings.SilenceIndex = SilenceCombo.SelectedIndex;
        _settings.CaptureDeviceId = _selectedDeviceId;
        _settingsStore.Save(_settings);
    }

    private RuntimeSettings GetRuntimeSettings()
    {
        double contextSeconds = ContextCombo.SelectedIndex switch
        {
            0 => 0.75,
            1 => 1.0,
            2 => 1.25,
            3 => 1.5,
            _ => 2.0
        };
        double updateSeconds = UpdateCombo.SelectedIndex switch
        {
            0 => 0.35,
            1 => 0.50,
            _ => 0.75
        };
        double silenceMs = SilenceCombo.SelectedIndex switch
        {
            0 => 250,
            1 => 350,
            2 => 500,
            3 => 750,
            4 => 1000,
            _ => 1500
        };

        return new RuntimeSettings(
            (int)(TranscriptionEngine.SampleRate * contextSeconds),
            (int)(TranscriptionEngine.SampleRate * updateSeconds),
            VadCombo.SelectedIndex == 0,
            silenceMs);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedDeviceId))
        {
            MessageBox.Show(
                this,
                "Select the Voicemeeter Output / B1 capture endpoint first.",
                "Capture device",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.bin");
        if (!File.Exists(modelPath))
        {
            MessageBox.Show(
                this,
                $"Whisper model not found:\n{modelPath}\n\nPut ggml-base.bin in a Models folder next to the executable.",
                "Model not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            SaveSettings();
            SetUiRunning(true);
            ReplaceTranscript(string.Empty);
            await _engine.StartAsync(
                _selectedDeviceId,
                _selectedDeviceName ?? _selectedDeviceId,
                modelPath,
                GetRuntimeSettings());
        }
        catch (Exception ex)
        {
            await StopAsync();
            ShowError("Start error", ex);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        await _engine.StopAsync();
        if (!Dispatcher.HasShutdownStarted) SetUiRunning(false);
    }

    private void DeviceCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not AudioDeviceInfo device) return;
        _selectedDeviceId = device.Id;
        _selectedDeviceName = device.FriendlyName;
        SelectedDeviceText.Text = $"Capture device: {device.FriendlyName}\nEndpoint ID: {device.Id}";
        if (_settingsLoaded) SaveSettings();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsRunning) LoadDevices();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _engine.ClearTranscript();

    private void SetUiRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        RefreshButton.IsEnabled = !running;
        DeviceCombo.IsEnabled = !running;
        ContextCombo.IsEnabled = !running;
        UpdateCombo.IsEnabled = !running;
        VadCombo.IsEnabled = !running;
        SilenceCombo.IsEnabled = !running;

        if (!running)
        {
            StatusText.Text = "Stopped";
            LevelText.Text = "Audio: --";
            VadText.Text = "VAD: --";
            LatencyText.Text = "STT: --";
            QueueText.Text = "Queue: --";
        }
    }

    private void ReplaceTranscript(string text)
    {
        TranscriptBox.Document.Blocks.Clear();
        if (!string.IsNullOrWhiteSpace(text)) TranscriptBox.AppendText(text);
        TranscriptBox.ScrollToEnd();
    }

    private void Post(Action update)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(update);
    }

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static int ClampIndex(int index, int itemCount, int defaultIndex)
    {
        if (itemCount <= 0) return -1;
        return index >= 0 && index < itemCount ? index : Math.Min(defaultIndex, itemCount - 1);
    }
}
