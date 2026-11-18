using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Video;

/// <summary>Options that apply to every stream.</summary>
public sealed record StreamOptions(
    HardwareAccelerationMode HardwareAcceleration,
    RtspTransport Transport,
    bool LowLatency,
    int ConnectTimeoutSeconds,
    int ReconnectMaxDelaySeconds)
{
    public static StreamOptions From(AppSettings s) =>
        new(s.HardwareAcceleration, s.Transport, s.LowLatency, s.ConnectTimeoutSeconds, s.ReconnectMaxDelaySeconds);
}

/// <summary>Snapshot of stream statistics (immutable, replaced once per second).</summary>
public sealed record StreamStatistics(
    string Codec,
    int Width,
    int Height,
    double Fps,
    double BitrateKbps,
    string Decoder,
    bool IsHardware,
    int OutputWidth,
    int OutputHeight,
    long FramesDecoded,
    long DecodeErrors)
{
    public static readonly StreamStatistics Empty = new("", 0, 0, 0, 0, "", false, 0, 0, 0, 0);
}

/// <summary>
/// One RTSP connection (camera + quality) with its own worker thread:
/// connect → demux → decode (GPU) → convert → publish frames.
/// Network failures never propagate: the stream switches to "Reconnecting" and retries
/// with exponential back-off until it is stopped.
/// </summary>
public sealed unsafe class RtspStream : IDisposable
{
    private readonly object _sync = new();
    private readonly List<StreamLease> _leases = new();
    private readonly AVIOInterruptCB_callback _interrupt; // keep delegate alive

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _restartRequested;
    private readonly ManualResetEventSlim _wake = new(false);

    private string _url;
    private StreamOptions _options;

    private long _activityTick;
    private long _timeoutMs;
    private volatile bool _timedOut;

    private volatile int _targetW, _targetH;
    private long _lastConvertTick;

    public Guid CameraId { get; }
    public StreamQuality Quality { get; }
    public string Name { get; }
    public string DisplayUrl { get; private set; }

    public FrameExchange Frames { get; } = new();

    public StreamStatus Status { get; private set; } = StreamStatus.Idle;
    public string? LastError { get; private set; }
    public DateTime? NextRetryUtc { get; private set; }
    public StreamStatistics Statistics { get; private set; } = StreamStatistics.Empty;

    private volatile IAudioSink? _audioSink;
    private volatile bool _audioFailed;

    /// <summary>Audio codec of the current connection, or null if the camera sends no audio.</summary>
    public string? AudioCodec { get; private set; }

    /// <summary>Destination for decoded audio (null = audio is received but not decoded).</summary>
    public IAudioSink? AudioSink
    {
        get => _audioSink;
        set { _audioSink = value; if (value != null) _audioFailed = false; }
    }

    /// <summary>Raised on the worker thread when <see cref="Status"/> changes.</summary>
    public event Action<RtspStream>? StatusChanged;

    /// <summary>Time when the last lease was released (used for delayed shutdown).</summary>
    internal DateTime? UnusedSinceUtc { get; set; }

    internal RtspStream(Guid cameraId, StreamQuality quality, string name, string url, string displayUrl, StreamOptions options)
    {
        CameraId = cameraId;
        Quality = quality;
        Name = name;
        _url = url;
        DisplayUrl = displayUrl;
        _options = options;
        _interrupt = OnInterrupt;
    }

    internal int LeaseCount { get { lock (_sync) return _leases.Count; } }

    internal void AddLease(StreamLease lease)
    {
        lock (_sync) { _leases.Add(lease); UnusedSinceUtc = null; }
        RecomputeTarget();
    }

    internal int RemoveLease(StreamLease lease)
    {
        int count;
        lock (_sync)
        {
            _leases.Remove(lease);
            count = _leases.Count;
            if (count == 0) UnusedSinceUtc = DateTime.UtcNow;
        }
        RecomputeTarget();
        return count;
    }

    /// <summary>Output size = the largest size requested by any visible consumer.</summary>
    internal void RecomputeTarget()
    {
        int w = 0, h = 0;
        lock (_sync)
        {
            foreach (var l in _leases)
            {
                w = Math.Max(w, l.TargetWidth);
                h = Math.Max(h, l.TargetHeight);
            }
        }
        _targetW = w;
        _targetH = h;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_thread != null) return;
            _stopRequested = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"RTSP {Name} [{Quality}]",
            };
            _thread.Start();
        }
    }

    /// <summary>Reconnects with a new URL / options without losing the consumers.</summary>
    internal void Restart(string url, string displayUrl, StreamOptions options)
    {
        lock (_sync)
        {
            _url = url;
            DisplayUrl = displayUrl;
            _options = options;
        }
        _restartRequested = true;
        _wake.Set();
    }

    /// <summary>Forces an immediate reconnect (e.g. user clicked "reconnect").</summary>
    public void Reconnect()
    {
        _restartRequested = true;
        _wake.Set();
    }

    /// <summary>Signals the worker to stop. Does not block.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _wake.Set();
    }

    /// <summary>Stops and waits for the worker (used on application exit).</summary>
    public bool Join(int timeoutMs)
    {
        Stop();
        var t = _thread;
        return t == null || t.Join(timeoutMs);
    }

    private void SetStatus(StreamStatus status, string? error = null)
    {
        bool changed = Status != status || error != LastError;
        Status = status;
        if (error != null || status == StreamStatus.Playing) LastError = error;
        if (changed)
        {
            try { StatusChanged?.Invoke(this); } catch (Exception ex) { Log.Error("StatusChanged handler failed", ex); }
        }
    }

    private int OnInterrupt(void* opaque)
    {
        if (_stopRequested || _restartRequested) return 1;
        if (Environment.TickCount64 - Interlocked.Read(ref _activityTick) > Interlocked.Read(ref _timeoutMs))
        {
            _timedOut = true;
            return 1;
        }
        return 0;
    }

    private void Touch(long timeoutMs)
    {
        Interlocked.Exchange(ref _timeoutMs, timeoutMs);
        Interlocked.Exchange(ref _activityTick, Environment.TickCount64);
    }

    private void Run()
    {
        int attempt = 0;
        Log.Info($"Stream start: {Name} [{Quality}] {DisplayUrl}");
        while (!_stopRequested)
        {
            _restartRequested = false;
            SetStatus(attempt == 0 ? StreamStatus.Connecting : StreamStatus.Reconnecting, LastError);
            NextRetryUtc = null;
            var startedAt = DateTime.UtcNow;
            bool hadFrames = false;
            try
            {
                hadFrames = Session();
            }
            catch (Exception ex)
            {
                if (!_stopRequested && !_restartRequested)
                {
                    LastError = ex.Message;
                    Log.Warn($"Stream {Name} [{Quality}]: {ex.Message}");
                }
            }

            if (_stopRequested) break;
            if (_restartRequested) { attempt = 0; continue; }

            // A connection that played for a while gets a fast first retry.
            if (hadFrames && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(10)) attempt = 0;
            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(_options.ReconnectMaxDelaySeconds, Math.Pow(2, Math.Min(attempt - 1, 8))));
            NextRetryUtc = DateTime.UtcNow + delay;
            SetStatus(StreamStatus.Reconnecting, LastError ?? "Verbindung verloren");
            _wake.Reset();
            _wake.Wait(delay);
        }
        SetStatus(StreamStatus.Stopped);
        Log.Info($"Stream stopped: {Name} [{Quality}]");
    }

    /// <summary>One connection attempt. Returns true when frames were decoded.</summary>
    private bool Session()
    {
        AVFormatContext* fmt = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        VideoDecoder? decoder = null;
        FrameConverter? converter = null;
        AudioDecoder? audio = null;
        bool hadFrames = false;
        string url;
        StreamOptions opt;
        lock (_sync) { url = _url; opt = _options; }

        try
        {
            long connectTimeoutMs = opt.ConnectTimeoutSeconds * 1000L;
            _timedOut = false;
            Touch(connectTimeoutMs);

            fmt = ffmpeg.avformat_alloc_context();
            fmt->interrupt_callback.callback = _interrupt;
            if (opt.LowLatency) fmt->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;
            fmt->flags |= ffmpeg.AVFMT_FLAG_DISCARD_CORRUPT;

            AVDictionary* dict = null;
            ffmpeg.av_dict_set(&dict, "rtsp_transport", opt.Transport == RtspTransport.Udp ? "udp" : "tcp", 0);
            // Audio is always received (Reolink AAC: ~32–64 kbit/s) but only decoded while someone listens,
            // so switching the sound on never requires a reconnect.
            ffmpeg.av_dict_set(&dict, "allowed_media_types", "video+audio", 0);
            ffmpeg.av_dict_set(&dict, "timeout", (connectTimeoutMs * 1000).ToString(), 0); // socket timeout in µs
            ffmpeg.av_dict_set(&dict, "user_agent", "OpenReo", 0);
            ffmpeg.av_dict_set(&dict, "max_delay", opt.LowLatency ? "200000" : "500000", 0);
            ffmpeg.av_dict_set(&dict, "buffer_size", "4194304", 0);
            ffmpeg.av_dict_set(&dict, "reorder_queue_size", opt.LowLatency ? "64" : "500", 0);
            ffmpeg.av_dict_set(&dict, "probesize", "2000000", 0);
            ffmpeg.av_dict_set(&dict, "analyzeduration", opt.LowLatency ? "1500000" : "3000000", 0);

            int ret = ffmpeg.avformat_open_input(&fmt, url, null, &dict);
            ffmpeg.av_dict_free(&dict);
            if (ret < 0)
            {
                // avformat_open_input frees the context on failure
                fmt = null;
                throw new FFmpegException(FFmpegHelper.FriendlyError(ret, _timedOut), ret);
            }

            Touch(connectTimeoutMs);
            ret = ffmpeg.avformat_find_stream_info(fmt, null);
            if (ret < 0) throw new FFmpegException("Stream-Analyse fehlgeschlagen: " + FFmpegHelper.FriendlyError(ret, _timedOut), ret);

            int videoIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIndex < 0) throw new FFmpegException("Kein Videostream gefunden.");
            int audioIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, videoIndex, null, 0);
            for (int i = 0; i < fmt->nb_streams; i++)
                if (i != videoIndex && i != audioIndex) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            AudioCodec = audioIndex >= 0 ? ffmpeg.avcodec_get_name(fmt->streams[audioIndex]->codecpar->codec_id)?.ToUpperInvariant() : null;

            var stream = fmt->streams[videoIndex];
            decoder = new VideoDecoder(stream->codecpar, opt.HardwareAcceleration, opt.LowLatency);
            converter = new FrameConverter(decoder.IsHardware ? decoder.HwType : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
            Log.Info($"Stream {Name} [{Quality}] opened: {decoder.CodecName} {stream->codecpar->width}x{stream->codecpar->height}, decoder {decoder.DecoderDescription}");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();

            long frames = 0, errors = 0, bytes = 0, framesInWindow = 0;
            long windowStart = Environment.TickCount64;
            int consecutiveErrors = 0;
            long readTimeoutMs = Math.Max(5000, connectTimeoutMs);

            // Local files (diagnostics / benchmark) are played at their real frame rate.
            bool pace = Path.IsPathFullyQualified(url) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            double timeBase = ffmpeg.av_q2d(stream->time_base);
            long paceStartTick = 0;
            double paceStartPts = double.NaN;

            while (!_stopRequested && !_restartRequested)
            {
                Touch(readTimeoutMs);
                ret = ffmpeg.av_read_frame(fmt, pkt);
                if (pace && ret >= 0 && pkt->stream_index == videoIndex && pkt->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    double pts = pkt->pts * timeBase;
                    if (double.IsNaN(paceStartPts)) { paceStartPts = pts; paceStartTick = Environment.TickCount64; }
                    long wait = (long)((pts - paceStartPts) * 1000) - (Environment.TickCount64 - paceStartTick);
                    if (wait > 0) _wake.Wait((int)Math.Min(wait, 1000));
                }
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
                if (ret < 0)
                {
                    if (_stopRequested || _restartRequested) break;
                    throw new FFmpegException(FFmpegHelper.FriendlyError(ret, _timedOut), ret);
                }

                if (pkt->stream_index != videoIndex)
                {
                    if (pkt->stream_index == audioIndex) HandleAudio(fmt->streams[audioIndex], pkt, ref audio);
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                bytes += pkt->size;
                ret = decoder.Send(pkt);
                ffmpeg.av_packet_unref(pkt);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    errors++;
                    consecutiveErrors++;
                    if (consecutiveErrors > 300)
                        throw new FFmpegException("Zu viele Decoder-Fehler – Stream wird neu aufgebaut.");
                }

                while (decoder.Receive(frame) == 0)
                {
                    consecutiveErrors = 0;
                    frames++;
                    framesInWindow++;
                    if (!hadFrames)
                    {
                        hadFrames = true;
                        SetStatus(StreamStatus.Playing);
                    }
                    PublishFrame(frame, converter);
                    ffmpeg.av_frame_unref(frame);
                }

                var now = Environment.TickCount64;
                if (now - windowStart >= 1000)
                {
                    double secs = (now - windowStart) / 1000.0;
                    int srcW = stream->codecpar->width, srcH = stream->codecpar->height;
                    var (outW, outH) = _targetW > 0 ? FrameConverter.ComputeOutputSize(srcW, srcH, _targetW, _targetH) : (0, 0);
                    Statistics = new StreamStatistics(
                        decoder.CodecName, srcW, srcH,
                        framesInWindow / secs,
                        bytes * 8 / 1000.0 / secs,
                        decoder.DecoderDescription + (decoder.IsHardware && converter.UsesGpuScaling ? " + GPU-Skalierung" : ""),
                        decoder.IsHardware,
                        outW, outH,
                        frames, errors);
                    windowStart = now;
                    framesInWindow = 0;
                    bytes = 0;
                }
            }
        }
        finally
        {
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            converter?.Dispose();
            decoder?.Dispose();
            audio?.Dispose();
            AudioCodec = null;
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            Statistics = Statistics with { Fps = 0, BitrateKbps = 0 };
        }
        return hadFrames;
    }

    /// <summary>Decodes an audio packet if a sink is attached (decoder is created lazily, kept for the session).</summary>
    private void HandleAudio(AVStream* stream, AVPacket* pkt, ref AudioDecoder? audio)
    {
        var sink = _audioSink;
        if (sink == null || _audioFailed) return;
        try
        {
            audio ??= new AudioDecoder(stream->codecpar);
            audio.Decode(pkt, sink);
        }
        catch (Exception ex)
        {
            _audioFailed = true; // never let audio problems affect the video
            Log.Warn($"Audio of {Name} [{Quality}] disabled: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts the frame for display unless nobody needs it. Every frame is decoded
    /// (required for reference frames), but conversion is skipped when no tile is visible
    /// or when the UI has not picked up the previous frame yet (e.g. window minimized).
    /// </summary>
    private void PublishFrame(AVFrame* frame, FrameConverter converter)
    {
        int tw = _targetW, th = _targetH;
        if (tw <= 0 || th <= 0) return;
        var now = Environment.TickCount64;
        if (Frames.HasUnconsumedFrame && now - _lastConvertTick < 250) return;
        if (converter.Convert(frame, tw, th, Frames))
            _lastConvertTick = now;
    }

    public void Dispose()
    {
        if (Join(3000))
            Frames.Dispose(); // only free buffers after the worker has exited
        _wake.Dispose();
    }
}

/// <summary>
/// A consumer's reference to a shared stream. Disposing releases the reference;
/// the stream is closed after the configured delay when no lease remains.
/// </summary>
public sealed class StreamLease : IDisposable
{
    private readonly StreamManager _owner;
    private bool _disposed;

    public RtspStream Stream { get; }
    public int TargetWidth { get; private set; }
    public int TargetHeight { get; private set; }

    internal StreamLease(StreamManager owner, RtspStream stream)
    {
        _owner = owner;
        Stream = stream;
    }

    /// <summary>
    /// Sets the pixel size this consumer displays the stream at (0 = not visible).
    /// The stream itself is never changed – only the size of the converted picture.
    /// </summary>
    public void SetTargetSize(int width, int height)
    {
        if (width == TargetWidth && height == TargetHeight) return;
        TargetWidth = Math.Max(0, width);
        TargetHeight = Math.Max(0, height);
        Stream.RecomputeTarget();
    }

    public void Dispose() => Release(immediate: false);

    /// <summary>Releases the lease; with <paramref name="immediate"/> the stream is closed right away if unused.</summary>
    public void Release(bool immediate)
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Release(this, immediate);
    }
}
