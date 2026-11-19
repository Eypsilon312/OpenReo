using NAudio.Wave;
using OpenReo.Core.Diagnostics;
using OpenReo.Video;

namespace OpenReo.App.Audio;

/// <summary>
/// Plays the decoded camera audio (48 kHz stereo S16) on the default output device.
/// The buffer is kept short: if the camera clock runs faster than the sound card, the
/// backlog is dropped instead of letting the delay grow.
/// </summary>
public sealed class AudioPlayer : IAudioSink, IAudioOutput, IDisposable
{
    /// <summary>Backlog above which buffered audio is dropped (live: 400 ms; playback is paced and may use more).</summary>
    public TimeSpan MaxBacklog { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeWaveProvider16 _volume;
    private WaveOutEvent? _output;
    private bool _playing;
    private float _level = 0.8f;
    private bool _muted;

    private const int DeviceLatencyMs = 120;

    /// <summary>Delay of the sound card output (WaveOut buffers).</summary>
    public TimeSpan OutputLatency => TimeSpan.FromMilliseconds(DeviceLatencyMs);

    /// <summary>Drops buffered audio that has not been played yet (after a seek / resync).</summary>
    public void Flush() => _buffer.ClearBuffer();

    /// <summary>False if no audio output device could be opened.</summary>
    public bool IsAvailable { get; private set; } = true;

    public AudioPlayer()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(AudioFormat.SampleRate, 16, AudioFormat.Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence when the camera delivers nothing
        };
        _volume = new VolumeWaveProvider16(_buffer) { Volume = _level };
    }

    public float Volume
    {
        get => _level;
        set { _level = Math.Clamp(value, 0f, 1f); _volume.Volume = _muted ? 0f : _level; }
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; _volume.Volume = _muted ? 0f : _level; }
    }

    /// <summary>Called from the stream worker thread with PCM data.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!_playing) return;
        if (_buffer.BufferedDuration > MaxBacklog) _buffer.ClearBuffer(); // keep latency low
        var data = pcm.ToArray();
        _buffer.AddSamples(data, 0, data.Length);
    }

    public void Start()
    {
        if (_playing || !IsAvailable) return;
        try
        {
            _output ??= CreateOutput();
            _buffer.ClearBuffer();
            _output.Play();
            _playing = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Log.Warn($"Audio output not available: {ex.Message}");
        }
    }

    public void Stop()
    {
        _playing = false;
        try { _output?.Stop(); } catch { /* ignore */ }
        _buffer.ClearBuffer();
    }

    private WaveOutEvent CreateOutput()
    {
        var o = new WaveOutEvent { DesiredLatency = DeviceLatencyMs, NumberOfBuffers = 3 };
        o.Init(_volume);
        return o;
    }

    public void Dispose()
    {
        Stop();
        _output?.Dispose();
        _output = null;
    }
}
