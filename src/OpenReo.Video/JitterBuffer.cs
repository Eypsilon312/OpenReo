namespace OpenReo.Video;

/// <summary>
/// Adaptive de-jitter buffer for live streams. Cameras (especially over Wi-Fi or while they upload
/// recordings at the same time) often deliver frames in bursts. Packets are therefore not decoded on
/// arrival but at <c>timestamp + offset + delay</c>, so the picture runs evenly.
/// The delay follows the measured delivery jitter of the last 10 s (rises immediately, falls slowly)
/// and is capped by the configured maximum. Thread safe.
/// </summary>
internal sealed class JitterBuffer
{
    private const long WindowMs = 10_000;
    private const double MarginMs = 40;
    private const double DecayPerSecondMs = 50;

    private readonly object _sync = new();
    private readonly Queue<(long Tick, double Offset)> _window = new();
    private readonly double _maxDelayMs;
    private double _delayMs;
    private double _minOffset = double.NaN;
    private long _lastUpdate;

    public JitterBuffer(int maxDelayMs) { _maxDelayMs = Math.Max(0, maxDelayMs); }

    public bool Enabled => _maxDelayMs > 0;

    /// <summary>Current buffering delay in ms.</summary>
    public double DelayMs { get { lock (_sync) return _delayMs; } }

    /// <summary>Records the arrival of a video packet (arrival tick in ms, time stamp in ms).</summary>
    public void Observe(long arrivalTick, double ptsMs)
    {
        if (!Enabled || double.IsNaN(ptsMs)) return;
        lock (_sync)
        {
            double offset = arrivalTick - ptsMs;
            // time stamp discontinuity (camera restart, wrap around) → start over
            if (!double.IsNaN(_minOffset) && Math.Abs(offset - _minOffset) > 5000 + _maxDelayMs)
            {
                _window.Clear();
                _delayMs = 0;
            }
            _window.Enqueue((arrivalTick, offset));
            while (_window.Count > 0 && arrivalTick - _window.Peek().Tick > WindowMs) _window.Dequeue();

            double min = double.MaxValue, max = double.MinValue;
            foreach (var (_, o) in _window) { if (o < min) min = o; if (o > max) max = o; }
            _minOffset = min;

            double target = Math.Min(_maxDelayMs, max - min + MarginMs);
            double elapsed = _lastUpdate == 0 ? 0 : (arrivalTick - _lastUpdate) / 1000.0;
            _lastUpdate = arrivalTick;
            _delayMs = target >= _delayMs ? target : Math.Max(target, _delayMs - elapsed * DecayPerSecondMs);
        }
    }

    /// <summary>Tick (ms) at which a packet with this time stamp is due for decoding.</summary>
    public long DueTick(double ptsMs, long now)
    {
        if (!Enabled || double.IsNaN(ptsMs)) return now;
        lock (_sync)
        {
            if (double.IsNaN(_minOffset)) return now;
            return (long)(ptsMs + _minOffset + _delayMs);
        }
    }
}

/// <summary>Packets received by the reader thread, waiting to be decoded. Thread safe.</summary>
internal sealed unsafe class LivePacketQueue : IDisposable
{
    public readonly record struct Entry(IntPtr Packet, long Arrival, double PtsMs, bool Video, int Size);

    private readonly object _sync = new();
    private readonly Queue<Entry> _video = new();
    private readonly Queue<Entry> _audio = new();
    private readonly ManualResetEventSlim _signal = new(false);

    public Exception? Error { get; private set; }
    public bool Completed { get; private set; }

    /// <summary>Number of queued video packets.</summary>
    public int Count { get { lock (_sync) return _video.Count; } }

    public void Add(FFmpeg.AutoGen.AVPacket* packet, long arrival, double ptsMs, bool video)
    {
        lock (_sync) (video ? _video : _audio).Enqueue(new Entry((IntPtr)packet, arrival, ptsMs, video, packet->size));
        _signal.Set();
    }

    public void Fail(Exception ex)
    {
        Error = ex;
        Completed = true;
        _signal.Set();
    }

    public void Complete()
    {
        Completed = true;
        _signal.Set();
    }

    public bool TryPeek(bool video, out Entry entry)
    {
        lock (_sync) return (video ? _video : _audio).TryPeek(out entry);
    }

    public bool TryDequeue(bool video, out Entry entry)
    {
        lock (_sync) return (video ? _video : _audio).TryDequeue(out entry);
    }

    public bool IsEmpty { get { lock (_sync) return _video.Count == 0 && _audio.Count == 0; } }

    public void Wait(int ms)
    {
        _signal.Wait(ms);
        _signal.Reset();
    }

    public static void Free(Entry e)
    {
        var p = (FFmpeg.AutoGen.AVPacket*)e.Packet;
        FFmpeg.AutoGen.ffmpeg.av_packet_free(&p);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            while (_video.TryDequeue(out var e)) Free(e);
            while (_audio.TryDequeue(out var e)) Free(e);
        }
        _signal.Dispose();
    }
}
