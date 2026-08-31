using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Threading.Channels;
using Whisper.net;

namespace InterviewTranscriberV5;

public sealed class TranscriptionEngine : IAsyncDisposable
{
    public const int SampleRate = 16000;
    private const double SpeechMultiplier = 3.0;
    private const double MinimumRms = 0.0065;
    private const double PreRollMs = 180;

    private readonly object _audioLock = new();
    private readonly List<float> _rollingSpeech = [];
    private readonly List<float> _utteranceBuffer = [];
    private readonly List<float> _preRoll = [];
    private readonly TranscriptAccumulator _transcript = new();

    private RuntimeSettings _settings = new(SampleRate, SampleRate / 2, true, 500);
    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private Channel<TranscriptionJob>? _jobs;
    private Task? _worker;
    private bool _stopping;
    private bool _inSpeech;
    private double _noiseFloor = 0.003;
    private DateTime _lastVoiceUtc;
    private long _sequence;
    private int _samplesSinceLastSubmit;

    public bool IsRunning => _capture is not null && !_stopping;
    public event Action<string>? StatusChanged;
    public event Action<double>? AudioLevelChanged;
    public event Action<string>? VadChanged;
    public event Action<string>? LatencyChanged;
    public event Action<string>? QueueChanged;
    public event Action<string>? TranscriptChanged;

    public async Task StartAsync(
        string deviceId,
        string deviceName,
        string modelPath,
        RuntimeSettings settings)
    {
        if (IsRunning) return;

        _settings = settings;
        ResetState();
        _stopping = false;
        _cts = new CancellationTokenSource();
        _jobs = Channel.CreateBounded<TranscriptionJob>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        QueueChanged?.Invoke("Queue: latest-only");
        StatusChanged?.Invoke("Initializing Whisper and capture...");

        try
        {
            await Task.Run(() => Initialize(deviceId, modelPath));
            _worker = Task.Run(() => ProcessJobsAsync(_cts.Token));
            StatusChanged?.Invoke($"Listening: {deviceName}");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;

        try { _capture?.StopRecording(); } catch { }
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
        }
        _capture = null;
        _captureFormat = null;

        _jobs?.Writer.TryComplete();
        _cts?.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }

        _worker = null;
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _cts?.Dispose();
        _cts = null;
        _jobs = null;
        ResetAudioState();
    }

    public void ClearTranscript()
    {
        _transcript.Clear();
        TranscriptChanged?.Invoke(string.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Initialize(string deviceId, string modelPath)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        if (device.DataFlow != DataFlow.Capture)
            throw new InvalidOperationException($"Selected endpoint is not a capture endpoint: {device.FriendlyName}");

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder().WithLanguage("auto").Build();
        var capture = new WasapiCapture(device);
        _captureFormat = capture.WaveFormat;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        _capture = capture;
        capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping || _captureFormat is not { } format) return;
        try
        {
            float[] mono = AudioSampleConverter.ToMono(e.Buffer, e.BytesRecorded, format);
            float[] audio = AudioSampleConverter.Resample(mono, format.SampleRate, SampleRate);
            double rms = AudioSampleConverter.CalculateRms(audio);
            AudioLevelChanged?.Invoke(audio.Length == 0 ? 0 : audio.Max(Math.Abs));
            if (_settings.VadEnabled) ProcessVad(audio, rms); else ProcessAlwaysOn(audio);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Capture error: {ex.Message}");
        }
    }

    private void ProcessVad(float[] audio, double rms)
    {
        lock (_audioLock)
        {
            if (!_inSpeech) _noiseFloor = _noiseFloor * 0.95 + Math.Min(rms, 0.05) * 0.05;
            bool voice = rms >= Math.Max(MinimumRms, _noiseFloor * SpeechMultiplier);
            AddPreRoll(audio);

            if (voice)
            {
                BeginSpeechIfNeeded();
                AddSpeech(audio);
                _lastVoiceUtc = DateTime.UtcNow;
                SubmitPartialIfDue();
            }
            else if (_inSpeech)
            {
                AddSpeech(audio);
                if ((DateTime.UtcNow - _lastVoiceUtc).TotalMilliseconds >= _settings.EndSilenceMs)
                    EndSpeech();
            }
            else
            {
                VadChanged?.Invoke("silence");
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
            SubmitPartialIfDue();
        }
    }

    private void AddPreRoll(float[] audio)
    {
        _preRoll.AddRange(audio);
        int max = (int)(SampleRate * PreRollMs / 1000);
        if (_preRoll.Count > max) _preRoll.RemoveRange(0, _preRoll.Count - max);
    }

    private void BeginSpeechIfNeeded()
    {
        if (_inSpeech) return;
        _inSpeech = true;
        _rollingSpeech.Clear();
        _rollingSpeech.AddRange(_preRoll);
        _utteranceBuffer.Clear();
        _utteranceBuffer.AddRange(_preRoll);
        _samplesSinceLastSubmit = 0;
        VadChanged?.Invoke("SPEECH");
    }

    private void AddSpeech(float[] audio)
    {
        _rollingSpeech.AddRange(audio);
        _utteranceBuffer.AddRange(audio);
        _samplesSinceLastSubmit += audio.Length;
        TrimRollingContext();
    }

    private void EndSpeech()
    {
        Submit(_utteranceBuffer, Math.Min(SampleRate * 20, _utteranceBuffer.Count), true);
        _inSpeech = false;
        _samplesSinceLastSubmit = 0;
        _rollingSpeech.Clear();
        _utteranceBuffer.Clear();
        _preRoll.Clear();
        VadChanged?.Invoke("silence");
    }

    private void SubmitPartialIfDue()
    {
        if (_samplesSinceLastSubmit < _settings.UpdateSamples) return;
        Submit(_rollingSpeech, Math.Min(_settings.ContextSamples, _rollingSpeech.Count), false);
        _samplesSinceLastSubmit = 0;
    }

    private void Submit(List<float> source, int count, bool isFinal)
    {
        if (_jobs is null || count < SampleRate * 0.25) return;
        float[] samples = source.Skip(source.Count - count).ToArray();
        long sequence = Interlocked.Increment(ref _sequence);
        _jobs.Writer.TryWrite(new TranscriptionJob(samples, sequence, IsFinal: isFinal));
        QueueChanged?.Invoke($"Queue: {(isFinal ? "final" : "latest")} #{sequence}");
    }

    private void TrimRollingContext()
    {
        int max = (int)(_settings.ContextSamples * 1.25);
        if (_rollingSpeech.Count > max) _rollingSpeech.RemoveRange(0, _rollingSpeech.Count - max);
    }

    private async Task ProcessJobsAsync(CancellationToken token)
    {
        if (_jobs is null) return;
        await foreach (TranscriptionJob job in _jobs.Reader.ReadAllAsync(token))
        {
            if (_processor is null) break;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var wav = AudioSampleConverter.CreateWav(job.Samples, SampleRate);
                var pieces = new List<string>();
                await foreach (var result in _processor.ProcessAsync(wav, token))
                {
                    string? text = result.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) pieces.Add(text);
                }

                string hypothesis = string.Join(' ', pieces).Trim();
                if (hypothesis.Length > 0)
                {
                    string transcript = job.IsFinal
                        ? _transcript.CommitFinal(hypothesis)
                        : _transcript.UpdateProvisional(hypothesis);
                    TranscriptChanged?.Invoke(transcript);
                }
                LatencyChanged?.Invoke($"STT: {stopwatch.Elapsed.TotalSeconds:0.00}s");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke($"Whisper error: {ex.Message}"); }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) StatusChanged?.Invoke($"Capture stopped: {e.Exception.Message}");
    }

    private void ResetState()
    {
        ResetAudioState();
        _transcript.Clear();
        _sequence = 0;
    }

    private void ResetAudioState()
    {
        lock (_audioLock)
        {
            _rollingSpeech.Clear();
            _utteranceBuffer.Clear();
            _preRoll.Clear();
            _inSpeech = false;
            _noiseFloor = 0.003;
            _samplesSinceLastSubmit = 0;
        }
    }
}
