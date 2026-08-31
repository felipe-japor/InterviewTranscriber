using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using Whisper.net;

namespace InterviewTranscriberV5;

public partial class MainWindow : Window
{
    private const int SampleRate = 16000;

    private static readonly string SettingsDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewTranscriber");

    private static readonly string SettingsFilePath =
        Path.Combine(
            SettingsDirectory,
            "settings.json");

    private sealed class AppSettings
    {
        public int ContextIndex { get; set; } = 1;
        public int UpdateIndex { get; set; } = 1;
        public int VadIndex { get; set; } = 0;
        public int SilenceIndex { get; set; } = 2;
        public string? CaptureDeviceId { get; set; }
    }

    private AppSettings _settings = new();
    private bool _settingsLoaded;

    private MMDeviceEnumerator? _uiEnumerator;

    private string? _selectedDeviceId;
    private string? _selectedDeviceName;

    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    private CancellationTokenSource? _cts;
    private Task? _whisperWorkerTask;

    private Channel<TranscriptionJob>? _jobs;

    private readonly object _audioLock = new();

    // Rolling context used for low-latency partial transcription.
    private readonly List<float> _rollingSpeech = new();

    // Full utterance buffer. Unlike _rollingSpeech, this is not trimmed on every
    // update. It is used when silence is detected to run one final transcription
    // over the whole spoken phrase and recover words missed by partial windows.
    private readonly List<float> _utteranceBuffer = new();

    // Pre-roll keeps the start of a word when VAD changes from silence to speech.
    private readonly List<float> _preRoll = new();

    private bool _stopping;
    private bool _inSpeech;

    private double _noiseFloor = 0.003;
    private DateTime _lastVoiceUtc;
    private DateTime _startUtc;

    private long _sequence;
    private int _samplesSinceLastSubmit;

    // Snapshot runtime settings. Worker threads never read WPF controls.
    private int _contextSamples = SampleRate;
    private int _updateSamples = SampleRate / 2;
    private bool _vadEnabled = true;

    private const double SpeechMultiplier = 3.0;
    private const double MinimumRms = 0.0065;
    private double _endSilenceMs = 500;
    private const double PreRollMs = 180;

    // Live-caption text state.
    // _committedText = finalized utterances already accepted.
    // _currentUtteranceText = current provisional utterance, refined on each rolling Whisper result.
    private readonly object _textLock = new();
    private string _committedText = string.Empty;
    private string _currentUtteranceText = string.Empty;

    // After a real silence/final utterance, the next committed utterance starts
    // on a new line.
    private bool _newLineBeforeNextUtterance;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            LoadSettings();
            ApplySavedDropdownSettings();
            LoadDevices();
            _settingsLoaded = true;
        };

        Closed += async (_, _) =>
        {
            SaveSettings();
            await StopCaptureAsync();
            _uiEnumerator?.Dispose();
        };
    }

    // -----------------------------------------------------------------
    // UI SETTINGS SNAPSHOT
    // -----------------------------------------------------------------

    private void SnapshotRuntimeSettings()
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

        _contextSamples = (int)(SampleRate * contextSeconds);
        _updateSamples = (int)(SampleRate * updateSeconds);
        _vadEnabled = VadCombo.SelectedIndex == 0;

        _endSilenceMs = SilenceCombo.SelectedIndex switch
        {
            0 => 250,
            1 => 350,
            2 => 500,
            3 => 750,
            4 => 1000,
            _ => 1500
        };

        SaveSettings();
    }

    // -----------------------------------------------------------------
    // SETTINGS PERSISTENCE
    // -----------------------------------------------------------------

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                _settings = new AppSettings();
                return;
            }

            string json =
                File.ReadAllText(
                    SettingsFilePath);

            _settings =
                JsonSerializer.Deserialize<AppSettings>(json)
                ?? new AppSettings();
        }
        catch
        {
            // A corrupt/missing settings file must never prevent the app
            // from starting. Fall back to defaults.
            _settings = new AppSettings();
        }
    }

    private void ApplySavedDropdownSettings()
    {
        ContextCombo.SelectedIndex =
            ClampIndex(
                _settings.ContextIndex,
                ContextCombo.Items.Count,
                1);

        UpdateCombo.SelectedIndex =
            ClampIndex(
                _settings.UpdateIndex,
                UpdateCombo.Items.Count,
                1);

        VadCombo.SelectedIndex =
            ClampIndex(
                _settings.VadIndex,
                VadCombo.Items.Count,
                0);

        SilenceCombo.SelectedIndex =
            ClampIndex(
                _settings.SilenceIndex,
                SilenceCombo.Items.Count,
                2);
    }

    private void SaveSettings()
    {
        try
        {
            // Save only when controls have been initialized.
            if (!IsLoaded)
                return;

            _settings.ContextIndex =
                ContextCombo.SelectedIndex;

            _settings.UpdateIndex =
                UpdateCombo.SelectedIndex;

            _settings.VadIndex =
                VadCombo.SelectedIndex;

            _settings.SilenceIndex =
                SilenceCombo.SelectedIndex;

            _settings.CaptureDeviceId =
                _selectedDeviceId;

            Directory.CreateDirectory(
                SettingsDirectory);

            string json =
                JsonSerializer.Serialize(
                    _settings,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                SettingsFilePath,
                json);
        }
        catch
        {
            // Persistence is convenience-only; never interrupt transcription
            // because settings could not be written.
        }
    }

    private static int ClampIndex(
        int index,
        int itemCount,
        int defaultIndex)
    {
        if (itemCount <= 0)
            return -1;

        if (index >= 0 &&
            index < itemCount)
        {
            return index;
        }

        return Math.Min(
            defaultIndex,
            itemCount - 1);
    }

    // -----------------------------------------------------------------
    // DEVICE ENUMERATION
    // -----------------------------------------------------------------

    private void LoadDevices()
    {
        try
        {
            _uiEnumerator?.Dispose();
            _uiEnumerator = new MMDeviceEnumerator();

            var allCaptureDevices = _uiEnumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .ToList();

            var devices = allCaptureDevices
                .Where(d =>
                    d.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
                    d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                .Select(d => new AudioDeviceInfo
                {
                    Id = d.ID,
                    FriendlyName = d.FriendlyName,
                    DisplayName = BuildDisplayName(d.FriendlyName)
                })
                .OrderByDescending(IsLikelyStandardOutput)
                .ThenBy(d => d.FriendlyName)
                .ToList();

            DeviceCombo.ItemsSource = devices;

            var preferred =
                (!string.IsNullOrWhiteSpace(_settings.CaptureDeviceId)
                    ? devices.FirstOrDefault(d =>
                        string.Equals(
                            d.Id,
                            _settings.CaptureDeviceId,
                            StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? devices.FirstOrDefault(IsLikelyStandardOutput)
                ?? devices.FirstOrDefault();

            DeviceCombo.SelectedItem = preferred;

            StatusText.Text = devices.Count == 0
                ? "No Voicemeeter capture endpoint found."
                : $"{devices.Count} Voicemeeter capture endpoint(s) found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Device enumeration failed.";

            MessageBox.Show(
                this,
                ex.ToString(),
                "Device enumeration error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool IsLikelyStandardOutput(AudioDeviceInfo device)
    {
        var n = device.FriendlyName;

        return
            (n.Contains("Voicemeeter Output", StringComparison.OrdinalIgnoreCase)
             || n.Contains("Out B1", StringComparison.OrdinalIgnoreCase)
             || n.Contains("B1", StringComparison.OrdinalIgnoreCase))
            && !n.Contains("AUX", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("VAIO3", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDisplayName(string name)
    {
        bool recommended =
            (name.Contains("Voicemeeter Output", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Out B1", StringComparison.OrdinalIgnoreCase)
             || name.Contains("B1", StringComparison.OrdinalIgnoreCase))
            && !name.Contains("AUX", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("VAIO3", StringComparison.OrdinalIgnoreCase);

        return recommended
            ? $"Recommended → {name}"
            : name;
    }

    private void DeviceCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not AudioDeviceInfo info)
            return;

        _selectedDeviceId = info.Id;
        _selectedDeviceName = info.FriendlyName;

        SelectedDeviceText.Text =
            $"Capture device: {info.FriendlyName}\nEndpoint ID: {info.Id}";

        if (_settingsLoaded)
            SaveSettings();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null)
            LoadDevices();
    }

    // -----------------------------------------------------------------
    // START / STOP
    // -----------------------------------------------------------------

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

        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "Models",
            "ggml-base.bin");

        if (!File.Exists(modelPath))
        {
            MessageBox.Show(
                this,
                $"Whisper model not found:\n{modelPath}\n\n" +
                "Put ggml-base.bin in a Models folder next to the executable.",
                "Model not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            SnapshotRuntimeSettings();
            SetUiRunning(true);

            TranscriptBox.Document.Blocks.Clear();

            lock (_audioLock)
            {
                _rollingSpeech.Clear();
                _utteranceBuffer.Clear();
                _preRoll.Clear();
                _inSpeech = false;
                _noiseFloor = 0.003;
                _samplesSinceLastSubmit = 0;
                _sequence = 0;
            }

            lock (_textLock)
            {
                _committedText = string.Empty;
                _currentUtteranceText = string.Empty;
                _newLineBeforeNextUtterance = false;
            }

            _stopping = false;
            _startUtc = DateTime.UtcNow;
            _cts = new CancellationTokenSource();

            // Capacity 1 + DropOldest is intentional:
            // if Whisper is slower than real time, always process the newest
            // rolling context instead of accumulating seconds of stale work.
            _jobs = Channel.CreateBounded<TranscriptionJob>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });

            StatusText.Text = "Initializing Whisper and capture...";
            QueueText.Text = "Queue: latest-only";

            string deviceId = _selectedDeviceId;
            string selectedName = _selectedDeviceName ?? deviceId;

            await Task.Run(() =>
                InitializeAudioAndWhisper(
                    deviceId,
                    selectedName,
                    modelPath));

            _whisperWorkerTask =
                Task.Run(() => WhisperWorkerAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            await StopCaptureAsync();

            MessageBox.Show(
                this,
                ex.ToString(),
                "Start error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InitializeAudioAndWhisper(
        string deviceId,
        string selectedName,
        string modelPath)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);

        if (device.DataFlow != DataFlow.Capture)
            throw new InvalidOperationException(
                $"Selected endpoint is not a capture endpoint: {device.FriendlyName}");

        _factory = WhisperFactory.FromPath(modelPath);

        _processor = _factory
            .CreateBuilder()
            .WithLanguage("auto")
            .Build();

        var capture = new WasapiCapture(device);

        _captureFormat = capture.WaveFormat;

        capture.DataAvailable += Capture_DataAvailable;
        capture.RecordingStopped += Capture_RecordingStopped;

        _capture = capture;
        capture.StartRecording();

        PostStatus($"Listening: {selectedName}");
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync();
    }

    private async Task StopCaptureAsync()
    {
        if (_stopping)
            return;

        _stopping = true;

        try
        {
            _capture?.StopRecording();
        }
        catch { }

        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.RecordingStopped -= Capture_RecordingStopped;
            }
        }
        catch { }

        _capture?.Dispose();
        _capture = null;
        _captureFormat = null;

        try
        {
            _jobs?.Writer.TryComplete();
        }
        catch { }

        try
        {
            _cts?.Cancel();
        }
        catch { }

        if (_whisperWorkerTask is not null)
        {
            try
            {
                await _whisperWorkerTask;
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        _whisperWorkerTask = null;

        _processor?.Dispose();
        _processor = null;

        _factory?.Dispose();
        _factory = null;

        _cts?.Dispose();
        _cts = null;
        _jobs = null;

        lock (_audioLock)
        {
            _rollingSpeech.Clear();
            _utteranceBuffer.Clear();
            _preRoll.Clear();
            _inSpeech = false;
            _samplesSinceLastSubmit = 0;
        }

        if (!Dispatcher.HasShutdownStarted)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetUiRunning(false);
            });
        }
    }

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

    // -----------------------------------------------------------------
    // AUDIO CAPTURE CALLBACK
    // -----------------------------------------------------------------

    private void Capture_DataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (_stopping)
            return;

        try
        {
            var format = _captureFormat;
            if (format is null)
                return;

            float[] mono;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat &&
                format.BitsPerSample == 32)
            {
                mono = ConvertFloatToMono(
                    e.Buffer,
                    e.BytesRecorded,
                    format.Channels);
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm &&
                     format.BitsPerSample == 16)
            {
                mono = ConvertPcm16ToMono(
                    e.Buffer,
                    e.BytesRecorded,
                    format.Channels);
            }
            else
            {
                PostStatus(
                    $"Unsupported capture format: {format.Encoding}, " +
                    $"{format.BitsPerSample}-bit, {format.SampleRate} Hz");
                return;
            }

            var audio = Resample(
                mono,
                format.SampleRate,
                SampleRate);

            double rms = CalculateRms(audio);

            double peak =
                audio.Length == 0
                    ? 0
                    : audio.Max(x => Math.Abs(x));

            PostAudioLevel(peak);

            if (_vadEnabled)
                ProcessVad(audio, rms);
            else
                ProcessAlwaysOn(audio);
        }
        catch (Exception ex)
        {
            PostStatus($"Capture error: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    // LOW-LATENCY VAD + ROLLING CONTEXT
    // -----------------------------------------------------------------

    private void ProcessVad(float[] audio, double rms)
    {
        lock (_audioLock)
        {
            if (!_inSpeech)
            {
                _noiseFloor =
                    (_noiseFloor * 0.95) +
                    (Math.Min(rms, 0.05) * 0.05);
            }

            double threshold =
                Math.Max(
                    MinimumRms,
                    _noiseFloor * SpeechMultiplier);

            bool voice =
                rms >= threshold;

            int maxPreRoll =
                (int)(SampleRate * PreRollMs / 1000.0);

            _preRoll.AddRange(audio);

            if (_preRoll.Count > maxPreRoll)
            {
                _preRoll.RemoveRange(
                    0,
                    _preRoll.Count - maxPreRoll);
            }

            if (voice)
            {
                if (!_inSpeech)
                {
                    _inSpeech = true;

                    _rollingSpeech.Clear();
                    _rollingSpeech.AddRange(_preRoll);

                    _utteranceBuffer.Clear();
                    _utteranceBuffer.AddRange(_preRoll);

                    _samplesSinceLastSubmit = 0;
                    PostVad("SPEECH");
                }

                _rollingSpeech.AddRange(audio);
                _utteranceBuffer.AddRange(audio);

                _samplesSinceLastSubmit += audio.Length;
                _lastVoiceUtc = DateTime.UtcNow;

                TrimRollingContext();

                if (_samplesSinceLastSubmit >= _updateSamples)
                {
                    SubmitLatestContextLocked(isFinal: false);
                    _samplesSinceLastSubmit = 0;
                }
            }
            else if (_inSpeech)
            {
                _rollingSpeech.AddRange(audio);
                _utteranceBuffer.AddRange(audio);

                _samplesSinceLastSubmit += audio.Length;
                TrimRollingContext();

                double silenceMs =
                    (DateTime.UtcNow - _lastVoiceUtc)
                    .TotalMilliseconds;

                if (silenceMs >= _endSilenceMs)
                {
                    SubmitFinalUtteranceLocked();

                    _inSpeech = false;
                    _samplesSinceLastSubmit = 0;

                    _rollingSpeech.Clear();
                    _utteranceBuffer.Clear();
                    _preRoll.Clear();

                    PostVad("silence");
                }
            }
            else
            {
                PostVad("silence");
            }
        }
    }

    private void ProcessAlwaysOn(float[] audio)
    {
        lock (_audioLock)
        {
            _rollingSpeech.AddRange(audio);
            _samplesSinceLastSubmit += audio.Length;

            TrimRollingContext();

            if (_samplesSinceLastSubmit >= _updateSamples)
            {
                SubmitLatestContextLocked(isFinal: false);
                _samplesSinceLastSubmit = 0;
            }
        }
    }

    private void TrimRollingContext()
    {
        // Keep slightly more than the nominal context so the most recent
        // complete speech fragment is retained.
        int maxSamples =
            Math.Max(
                _contextSamples,
                (int)(_contextSamples * 1.25));

        if (_rollingSpeech.Count > maxSamples)
        {
            _rollingSpeech.RemoveRange(
                0,
                _rollingSpeech.Count - maxSamples);
        }
    }

    private void SubmitLatestContextLocked(bool isFinal)
    {
        var jobs = _jobs;
        if (jobs is null || _rollingSpeech.Count < SampleRate * 0.25)
            return;

        int count =
            Math.Min(
                _contextSamples,
                _rollingSpeech.Count);

        var snapshot =
            _rollingSpeech
                .Skip(_rollingSpeech.Count - count)
                .ToArray();

        long sequence =
            Interlocked.Increment(ref _sequence);

        jobs.Writer.TryWrite(
            new TranscriptionJob(
                snapshot,
                sequence,
                isFinal));

        PostQueue($"Queue: latest #{sequence}");
    }

    private void SubmitFinalUtteranceLocked()
    {
        var jobs = _jobs;

        if (jobs is null ||
            _utteranceBuffer.Count < SampleRate * 0.25)
        {
            return;
        }

        // Keep final utterances bounded so a very long monologue doesn't create
        // an excessively expensive Whisper call. 20 seconds is enough context
        // for interview questions while still being practical.
        int maxFinalSamples = SampleRate * 20;

        int count =
            Math.Min(
                maxFinalSamples,
                _utteranceBuffer.Count);

        var snapshot =
            _utteranceBuffer
                .Skip(_utteranceBuffer.Count - count)
                .ToArray();

        long sequence =
            Interlocked.Increment(ref _sequence);

        jobs.Writer.TryWrite(
            new TranscriptionJob(
                snapshot,
                sequence,
                IsFinal: true));

        PostQueue(
            $"Queue: final #{sequence}");
    }

    // -----------------------------------------------------------------
    // WHISPER WORKER
    // -----------------------------------------------------------------

    private async Task WhisperWorkerAsync(
        CancellationToken token)
    {
        var jobs = _jobs;
        if (jobs is null)
            return;

        await foreach (
            var job in jobs.Reader.ReadAllAsync(token))
        {
            if (token.IsCancellationRequested)
                break;

            var processor = _processor;
            if (processor is null)
                break;

            var sw =
                Stopwatch.StartNew();

            try
            {
                using var wav =
                    CreateWav(job.Samples);

                var pieces =
                    new List<string>();

                await foreach (
                    var result in
                    processor.ProcessAsync(wav, token))
                {
                    var text =
                        result.Text?.Trim();

                    if (!string.IsNullOrWhiteSpace(text))
                        pieces.Add(text);
                }

                string currentText =
                    string.Join(" ", pieces)
                        .Trim();

                if (!string.IsNullOrWhiteSpace(currentText))
                {
                    if (job.IsFinal)
                    {
                        CommitFinalHypothesis(currentText);
                    }
                    else
                    {
                        UpdateProvisionalHypothesis(currentText);
                    }
                }

                PostLatency(
                    $"STT: {sw.Elapsed.TotalSeconds:0.00}s");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PostStatus(
                    $"Whisper error: {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------
    // LIVE CAPTION / PROVISIONAL TEXT MERGING
    // -----------------------------------------------------------------

    private void UpdateProvisionalHypothesis(string hypothesis)
    {
        lock (_textLock)
        {
            _currentUtteranceText =
                MergeRollingText(
                    _currentUtteranceText,
                    hypothesis);

            RenderTranscriptLocked();
        }
    }

    private void CommitFinalHypothesis(string hypothesis)
    {
        lock (_textLock)
        {
            hypothesis =
                CleanWhitespace(
                    hypothesis);

            // The final job is transcribed from the complete utterance buffer,
            // so prefer it over the accumulated partial text. This recovers
            // words that may have disappeared from short rolling windows.
            if (!string.IsNullOrWhiteSpace(hypothesis))
            {
                _currentUtteranceText =
                    hypothesis;
            }

            if (!string.IsNullOrWhiteSpace(_currentUtteranceText))
            {
                _committedText =
                    JoinCommittedUtterance(
                        _committedText,
                        _currentUtteranceText,
                        _newLineBeforeNextUtterance);
            }

            _currentUtteranceText = string.Empty;

            // A finalized utterance means a period of silence was detected.
            // The next utterance will be rendered on a new line.
            _newLineBeforeNextUtterance = true;

            RenderTranscriptLocked();
        }
    }

    /// <summary>
    /// Merges a new rolling Whisper hypothesis into the current utterance.
    ///
    /// Example:
    /// existing: "Tell us why you are"
    /// incoming: "you are a good fit for this position."
    /// result:   "Tell us why you are a good fit for this position."
    ///
    /// If Whisper revises a short phrase rather than extending it, the newer
    /// hypothesis replaces the overlapping tail instead of being appended as
    /// another sentence.
    /// </summary>
    private static string MergeRollingText(
        string existing,
        string incoming)
    {
        existing = CleanWhitespace(existing);
        incoming = CleanWhitespace(incoming);

        if (string.IsNullOrWhiteSpace(existing))
            return incoming;

        if (string.IsNullOrWhiteSpace(incoming))
            return existing;

        if (string.Equals(
            existing,
            incoming,
            StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        // If one hypothesis fully contains the other, prefer the longer/newer one.
        if (incoming.Contains(
            existing,
            StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        if (existing.Contains(
            incoming,
            StringComparison.OrdinalIgnoreCase))
        {
            // Whisper sometimes emits a shorter rolling fragment.
            // Keep the fuller current utterance rather than shrinking it.
            return existing;
        }

        var existingWords =
            existing.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        var incomingWords =
            incoming.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        int maxOverlap =
            Math.Min(
                Math.Min(
                    existingWords.Length,
                    incomingWords.Length),
                20);

        // First try exact suffix(existing) -> prefix(incoming) overlap.
        for (int overlap = maxOverlap;
             overlap >= 1;
             overlap--)
        {
            bool match = true;

            for (int i = 0;
                 i < overlap;
                 i++)
            {
                string a =
                    NormalizeWord(
                        existingWords[
                            existingWords.Length - overlap + i]);

                string b =
                    NormalizeWord(
                        incomingWords[i]);

                if (!string.Equals(
                    a,
                    b,
                    StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return CleanWhitespace(
                    JoinText(
                        string.Join(" ", existingWords),
                        string.Join(
                            " ",
                            incomingWords.Skip(overlap))));
            }
        }

        // If there is no exact overlap, look for the longest incoming prefix
        // inside the tail of the current utterance. This handles small Whisper
        // revisions without duplicating the entire partial sentence.
        int tailStart =
            Math.Max(
                0,
                existingWords.Length - 12);

        for (int incomingPrefixLength =
                 Math.Min(
                     incomingWords.Length,
                     8);
             incomingPrefixLength >= 2;
             incomingPrefixLength--)
        {
            for (int start =
                     existingWords.Length - incomingPrefixLength;
                 start >= tailStart;
                 start--)
            {
                bool match = true;

                for (int i = 0;
                     i < incomingPrefixLength;
                     i++)
                {
                    if (!string.Equals(
                        NormalizeWord(existingWords[start + i]),
                        NormalizeWord(incomingWords[i]),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    var prefix =
                        existingWords.Take(start);

                    return CleanWhitespace(
                        string.Join(
                            " ",
                            prefix.Concat(incomingWords)));
                }
            }
        }

        // No reliable overlap was found. Because this is a rolling live caption,
        // prefer the new hypothesis as the provisional tail only when it appears
        // to be a substantial revision. Otherwise append it naturally.
        if (incomingWords.Length >= 3 &&
            existingWords.Length <= 5)
        {
            return incoming;
        }

        return CleanWhitespace(
            JoinText(
                existing,
                incoming));
    }

    private void RenderTranscriptLocked()
    {
        string committed =
            _committedText.TrimEnd();

        string provisional =
            CleanWhitespace(
                _currentUtteranceText);

        string fullText;

        if (string.IsNullOrEmpty(committed))
        {
            fullText = provisional;
        }
        else if (string.IsNullOrEmpty(provisional))
        {
            fullText = committed;
        }
        else if (_newLineBeforeNextUtterance)
        {
            fullText =
                committed +
                Environment.NewLine +
                provisional;
        }
        else
        {
            fullText =
                JoinText(
                    committed,
                    provisional);
        }

        PostTranscriptReplace(fullText);
    }

    private static string JoinCommittedUtterance(
        string committed,
        string utterance,
        bool newLineBefore)
    {
        committed = committed.TrimEnd();
        utterance = CleanWhitespace(utterance);

        if (string.IsNullOrWhiteSpace(committed))
            return utterance;

        if (string.IsNullOrWhiteSpace(utterance))
            return committed;

        if (newLineBefore)
            return committed + Environment.NewLine + utterance;

        return JoinText(
            committed,
            utterance);
    }

    private static string JoinText(
        string left,
        string right)
    {
        left = CleanWhitespace(left);
        right = CleanWhitespace(right);

        if (string.IsNullOrEmpty(left))
            return right;

        if (string.IsNullOrEmpty(right))
            return left;

        char first =
            right[0];

        bool punctuation =
            ".,;:!?)]}".Contains(first);

        return punctuation
            ? left + right
            : left + " " + right;
    }

    private static string CleanWhitespace(string text)
    {
        return string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeWord(string word)
    {
        return word.Trim(
            '.', ',', ';', ':', '!', '?',
            '"', '\'', '(', ')', '[', ']',
            '{', '}');
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_textLock)
        {
            _committedText = string.Empty;
            _currentUtteranceText = string.Empty;
            _newLineBeforeNextUtterance = false;
        }

        TranscriptBox.Document.Blocks.Clear();
    }

    // -----------------------------------------------------------------
    // UI MARSHALING
    // -----------------------------------------------------------------

    private void PostStatus(string text) =>
        Dispatcher.BeginInvoke(() =>
            StatusText.Text = text);

    private void PostAudioLevel(double peak) =>
        Dispatcher.BeginInvoke(() =>
            LevelText.Text =
                $"Audio: {(peak * 100):0}%");

    private void PostVad(string state) =>
        Dispatcher.BeginInvoke(() =>
            VadText.Text =
                $"VAD: {state}");

    private void PostLatency(string text) =>
        Dispatcher.BeginInvoke(() =>
            LatencyText.Text = text);

    private void PostQueue(string text) =>
        Dispatcher.BeginInvoke(() =>
            QueueText.Text = text);

    private void PostTranscriptReplace(string text) =>
        Dispatcher.BeginInvoke(() =>
        {
            TranscriptBox.Document.Blocks.Clear();

            if (!string.IsNullOrWhiteSpace(text))
                TranscriptBox.AppendText(text);

            TranscriptBox.ScrollToEnd();
        });

    // -----------------------------------------------------------------
    // AUDIO HELPERS
    // -----------------------------------------------------------------

    private static float[] ConvertFloatToMono(
        byte[] buffer,
        int bytesRecorded,
        int channels)
    {
        int frames =
            bytesRecorded /
            (4 * channels);

        var mono =
            new float[frames];

        for (int frame = 0;
             frame < frames;
             frame++)
        {
            double sum = 0;

            for (int channel = 0;
                 channel < channels;
                 channel++)
            {
                int offset =
                    (frame * channels + channel) * 4;

                sum +=
                    BitConverter.ToSingle(
                        buffer,
                        offset);
            }

            mono[frame] =
                (float)(sum / channels);
        }

        return mono;
    }

    private static float[] ConvertPcm16ToMono(
        byte[] buffer,
        int bytesRecorded,
        int channels)
    {
        int frames =
            bytesRecorded /
            (2 * channels);

        var mono =
            new float[frames];

        for (int frame = 0;
             frame < frames;
             frame++)
        {
            double sum = 0;

            for (int channel = 0;
                 channel < channels;
                 channel++)
            {
                int offset =
                    (frame * channels + channel) * 2;

                short sample =
                    BitConverter.ToInt16(
                        buffer,
                        offset);

                sum +=
                    sample /
                    32768f;
            }

            mono[frame] =
                (float)(sum / channels);
        }

        return mono;
    }

    private static double CalculateRms(
        float[] samples)
    {
        if (samples.Length == 0)
            return 0;

        double sum = 0;

        foreach (float sample in samples)
            sum += sample * sample;

        return
            Math.Sqrt(
                sum /
                samples.Length);
    }

    private static float[] Resample(
        float[] input,
        int sourceRate,
        int targetRate)
    {
        if (sourceRate == targetRate)
            return input;

        if (input.Length == 0)
            return Array.Empty<float>();

        int outputLength =
            (int)Math.Round(
                input.Length *
                (double)targetRate /
                sourceRate);

        var output =
            new float[outputLength];

        double ratio =
            (double)sourceRate /
            targetRate;

        for (int i = 0;
             i < outputLength;
             i++)
        {
            double position =
                i * ratio;

            int index =
                (int)position;

            double fraction =
                position - index;

            output[i] =
                index >= input.Length - 1
                    ? input[^1]
                    : (float)(
                        input[index] *
                            (1 - fraction) +
                        input[index + 1] *
                            fraction);
        }

        return output;
    }

    private static MemoryStream CreateWav(
        float[] samples)
    {
        var stream =
            new MemoryStream();

        using (
            var nonClosingStream =
                new IgnoreDisposeStream(stream))
        using (
            var writer =
                new WaveFileWriter(
                    nonClosingStream,
                    new WaveFormat(
                        SampleRate,
                        16,
                        1)))
        {
            foreach (float sample in samples)
            {
                short pcm =
                    (short)(
                        Math.Clamp(
                            sample,
                            -1f,
                            1f) *
                        short.MaxValue);

                writer.WriteByte(
                    (byte)(pcm & 0xff));

                writer.WriteByte(
                    (byte)(
                        (pcm >> 8) &
                        0xff));
            }

            writer.Flush();
        }

        stream.Position = 0;

        return stream;
    }

    private void Capture_RecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            PostStatus(
                $"Capture stopped: {e.Exception.Message}");
        }
    }
}
