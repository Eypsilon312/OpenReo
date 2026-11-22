using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Localization;
using OpenReo.Core.Models;
using OpenReo.Video.Recordings;

namespace OpenReo.Video;

/// <summary>
/// Plays recorded video files (e.g. Reolink FTP uploads on a NAS): exact seeking, pause,
/// speed 0.25× … 16×, sound at 1×. Uses the same GPU decoder and frame conversion as the live view.
/// One worker thread; commands are passed in and executed between packets.
/// </summary>
public sealed unsafe class PlaybackSession : IDisposable
{
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private readonly HardwareAccelerationMode _hw;
    private volatile bool _disposed;

    // commands
    private string? _openPath;
    private double _openOffset;
    private double? _seekTarget;
    private bool _commandPending;

    private volatile bool _paused;
    private double _speed = 1.0;
    private volatile int _targetW = 1280, _targetH = 720;
    private volatile bool _timingReset = true;

    // playback clock (set by the video pacing) and timed audio queue
    private long _baseTick;
    private double _basePos;
    private bool _clockValid;
    private readonly TimedAudioQueue _audioQueue = new();
    private AudioTrackReader? _audioReader;
    private volatile bool _audioResync = true;

    public FrameExchange Frames { get; } = new();

    /// <summary>Sound output (only used at 1× and while playing).</summary>
    public IAudioSink? AudioSink { get; set; }

    /// <summary>File currently loaded.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>Position inside the current file in seconds.</summary>
    public double Position { get; private set; }

    /// <summary>Duration of the current file in seconds (0 if unknown).</summary>
    public double Duration { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Raised on the worker thread when the end of the file was reached.</summary>
    public event Action<PlaybackSession, string>? Ended;

    public PlaybackSession(HardwareAccelerationMode hw)
    {
        _hw = hw;
        _thread = new Thread(Run) { IsBackground = true, Name = "Playback" };
        _thread.Start();
    }

    public bool Paused
    {
        get => _paused;
        set { _paused = value; _timingReset = true; _wake.Set(); }
    }

    public double Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, 0.1, 32); _timingReset = true; _wake.Set(); }
    }

    public void SetTargetSize(int w, int h)
    {
        _targetW = Math.Max(16, w);
        _targetH = Math.Max(16, h);
    }

    /// <summary>Loads a file and starts at the given offset (seconds).</summary>
    public void Open(string path, double offset)
    {
        lock (_sync)
        {
            _openPath = path;
            _openOffset = Math.Max(0, offset);
            _seekTarget = null;
            _commandPending = true;
        }
        _wake.Set();
    }

    /// <summary>Jumps to a position (seconds) inside the current file.</summary>
    public void Seek(double seconds)
    {
        lock (_sync)
        {
            _seekTarget = Math.Max(0, seconds);
            _commandPending = true;
        }
        _wake.Set();
    }

    private void Run()
    {
        while (!_disposed)
        {
            string? path;
            double offset;
            lock (_sync)
            {
                path = _openPath;
                offset = _openOffset;
                _openPath = null;
                if (path == null) _commandPending = false;
            }
            if (path == null)
            {
                _wake.Wait(500);
                _wake.Reset();
                continue;
            }
            try
            {
                PlayFile(path, offset);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Warn($"Playback of {path} failed: {ex.Message}");
            }
        }
    }

    private void PlayFile(string path, double startOffset)
    {
        AVFormatContext* fmt = null;
        FragmentReader? reader = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        VideoDecoder? decoder = null;
        FrameConverter? converter = null;
        AudioDecoder? audio = null;
        try
        {
            Error = null;
            // Fragmented MP4 (Reolink FTP uploads): start FFmpeg directly at the right fragment
            // instead of letting it read every fragment header of the file first.
            // Time stamps then start at 0 for that fragment: time = baseTime + pts - origin.
            var fmp4 = FragmentedMp4.TryOpen(path);
            double baseTime = 0, origin = double.NaN;
            if (fmp4 != null)
            {
                var start = fmp4.Locate(startOffset);
                reader = fmp4.OpenAt(start.Fragment);
                fmt = reader.Context;
                baseTime = start.Time;
            }
            else if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) { fmt = null; throw new FFmpegException(Lang.T("Datei konnte nicht geöffnet werden.")); }
            FFmpegHelper.ThrowIfError(ffmpeg.avformat_find_stream_info(fmt, null), "Stream-Analyse");
            int vi = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (vi < 0) throw new FFmpegException(Lang.T("Kein Videostream gefunden."));
            int ai = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, vi, null, 0);
            var vs = fmt->streams[vi];
            double tb = ffmpeg.av_q2d(vs->time_base);
            double startPts = vs->start_time != ffmpeg.AV_NOPTS_VALUE ? vs->start_time * tb : 0;

            decoder = new VideoDecoder(vs->codecpar, _hw, lowLatency: false);
            converter = new FrameConverter(decoder.IsHardware ? decoder.HwType : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();

            CurrentFile = path;
            _audioQueue.Clear();
            _clockValid = false;
            // Audio has its own read position (second demuxer on the same file): cameras often store
            // audio a second or more before/after the matching video, so reading it together with
            // the video (paced by the video) delivers it too late or too early.
            if (ai >= 0)
            {
                fmt->streams[ai]->discard = AVDiscard.AVDISCARD_ALL;
                _audioReader = fmp4 != null ? AudioTrackReader.Open(fmp4) : AudioTrackReader.Open(path, startPts);
                _audioResync = true;
            }
            double skipUntil;
            if (fmp4 != null)
            {
                // the length needs a few reads spread over the file – don't hold up the first frame
                Duration = 0;
                _ = Task.Run(() =>
                {
                    try { var d = fmp4.Duration; if (CurrentFile == path) Duration = d; }
                    catch (Exception ex) { Log.Debug($"Duration of {path}: {ex.Message}"); }
                });
                skipUntil = startOffset;
            }
            else
            {
                Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                skipUntil = DoSeek(fmt, vi, tb, startPts, startOffset, decoder, ref audio);
            }
            Position = startOffset;

            long baseTick = 0;
            double basePos = 0;
            _timingReset = true;

            while (!_disposed)
            {
                // ---- commands
                double? seek = null;
                lock (_sync)
                {
                    if (_openPath != null) return; // another file requested
                    if (_seekTarget is double t) { seek = t; _seekTarget = null; }
                    _commandPending = false;
                }
                if (seek is double target)
                {
                    _audioQueue.Clear();
                    _clockValid = false;
                    if (fmp4 != null && reader != null)
                    {
                        var start = fmp4.Locate(target);
                        reader.Dispose();
                        reader = null;
                        fmt = null;
                        reader = fmp4.OpenAt(start.Fragment);
                        fmt = reader.Context;
                        if (ai >= 0) fmt->streams[ai]->discard = AVDiscard.AVDISCARD_ALL;
                        decoder.Flush();
                        baseTime = start.Time;
                        origin = double.NaN;
                        skipUntil = target;
                    }
                    else skipUntil = DoSeek(fmt, vi, tb, startPts, target, decoder, ref audio);
                    Position = target;
                    _timingReset = true;
                }
                if (_paused && skipUntil < 0)
                {
                    _wake.Wait(200);
                    _wake.Reset();
                    continue;
                }

                FeedAudio();
                int ret = ffmpeg.av_read_frame(fmt, pkt);
                if (ret < 0)
                {
                    // end of file: show the last frame and wait for the next command
                    Position = Duration > 0 ? Duration : Position;
                    try { Ended?.Invoke(this, path); } catch (Exception ex) { Log.Error("Ended handler", ex); }
                    while (!_disposed)
                    {
                        lock (_sync) { if (_openPath != null || _seekTarget != null) break; }
                        FeedAudio(); // play out the remaining sound
                        if (_wake.Wait(_audioQueue.Count > 0 ? 20 : 500)) _wake.Reset();
                    }
                    lock (_sync) { if (_openPath != null) return; }
                    continue; // a seek inside this file was requested
                }

                if (pkt->stream_index == ai) // (normally discarded – audio comes from the audio reader)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }
                if (pkt->stream_index != vi) { ffmpeg.av_packet_unref(pkt); continue; }
                if (fmp4 != null && double.IsNaN(origin))
                {
                    long first = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                    if (first != ffmpeg.AV_NOPTS_VALUE) origin = first * tb;
                }

                decoder.Send(pkt);
                ffmpeg.av_packet_unref(pkt);
                while (decoder.Receive(frame) == 0)
                {
                    long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                    double pos = ts == ffmpeg.AV_NOPTS_VALUE ? Position
                        : fmp4 != null ? baseTime + ts * tb - (double.IsNaN(origin) ? 0 : origin)
                        : ts * tb - startPts;
                    if (skipUntil >= 0 && pos < skipUntil - 0.02)
                    {
                        ffmpeg.av_frame_unref(frame); // decode up to the exact seek target without showing frames
                        continue;
                    }
                    bool firstAfterSeek = skipUntil >= 0;
                    skipUntil = -1;

                    // ---- pacing according to speed
                    if (_timingReset || firstAfterSeek)
                    {
                        baseTick = Environment.TickCount64;
                        basePos = pos;
                        _timingReset = false;
                        SetClock(baseTick, basePos);
                    }
                    else
                    {
                        long due = baseTick + (long)((pos - basePos) * 1000 / _speed);
                        long wait = due - Environment.TickCount64;
                        if (wait > 0)
                        {
                            // wait in small steps and keep feeding the sound card meanwhile
                            long until = Environment.TickCount64 + Math.Min(wait, 1000);
                            while (Environment.TickCount64 < until)
                            {
                                FeedAudio();
                                if (_wake.Wait((int)Math.Clamp(until - Environment.TickCount64, 1, 20))) { _wake.Reset(); break; }
                            }
                            lock (_sync) { if (_commandPending || _openPath != null) { ffmpeg.av_frame_unref(frame); break; } }
                        }
                        else if (wait < -2000) { baseTick = Environment.TickCount64; basePos = pos; SetClock(baseTick, basePos); } // fell behind (e.g. 16× on 4K)
                    }

                    // at high speed only convert what the screen can show
                    if (!Frames.HasUnconsumedFrame || firstAfterSeek)
                        converter.Convert(frame, _targetW, _targetH, Frames);
                    Position = pos;
                    ffmpeg.av_frame_unref(frame);
                    if (_paused) break; // paused after a seek: one frame shown
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
            lock (_audioQueue) { _audioReader?.Dispose(); _audioReader = null; }
            if (reader != null) reader.Dispose(); // owns fmt
            else if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    private void SetClock(long tick, double pos)
    {
        lock (_audioQueue) { _baseTick = tick; _basePos = pos; _clockValid = true; }
        _audioResync = true; // reposition the audio reader to the new clock
    }

    /// <summary>
    /// Releases queued audio whose time has come (about 250 ms ahead so the sound card never runs dry);
    /// audio that is late (e.g. after a hiccup) is dropped instead of delaying the sound.
    /// </summary>
    private void FeedAudio()
    {
        var sink = AudioSink;
        bool off = _paused || Math.Abs(_speed - 1.0) >= 0.01;
        if (sink == null || off)
        {
            if (off) _audioQueue.Clear();
            return;
        }
        // Lip sync: audio needs OutputLatency until it is heard, so it is handed over that much
        // earlier, plus a small buffer so the sound card never runs dry. Audio whose moment has
        // already passed is dropped instead of being played late.
        var output = sink as IAudioOutput;
        double latency = output?.OutputLatency.TotalSeconds ?? 0.12;
        const double buffer = 0.10;
        double clock;
        lock (_audioQueue)
        {
            if (!_clockValid) return;
            clock = _basePos + (Environment.TickCount64 - _baseTick) / 1000.0 * _speed;
            var reader = _audioReader;
            if (reader != null)
            {
                if (_audioResync)
                {
                    _audioResync = false;
                    _audioQueue.Clear();
                    output?.Flush(); // nothing old may stay in the sound card buffer
                    reader.Seek(Math.Max(0, clock + latency - 0.2));
                }
                reader.FillUntil(clock + latency + 0.6, _audioQueue); // read ahead independent of the video
            }
        }
        _audioQueue.Release(clock + latency + buffer, clock + latency - 0.02, sink);
    }

    /// <summary>Seeks to the key frame before <paramref name="seconds"/>; returns the exact target for frame skipping.</summary>
    private static double DoSeek(AVFormatContext* fmt, int vi, double tb, double startPts, double seconds, VideoDecoder decoder, ref AudioDecoder? audio)
    {
        if (seconds <= 0.05 && startPts <= 0.05)
        {
            ffmpeg.av_seek_frame(fmt, vi, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        else
        {
            long ts = (long)((seconds + startPts) / tb);
            ffmpeg.av_seek_frame(fmt, vi, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        decoder.Flush();
        audio?.Dispose();
        audio = null;
        return seconds;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        if (_thread.Join(3000)) Frames.Dispose();
    }
}

/// <summary>Decoded PCM pieces (about 20 ms each) with their presentation time.</summary>
internal sealed class TimedAudioQueue : IAudioSink
{
    private const int BytesPerSecond = AudioFormat.SampleRate * AudioFormat.Channels * 2;
    private const int PieceBytes = BytesPerSecond / 50; // 20 ms
    private readonly Queue<(double Pts, byte[] Data)> _items = new();
    private double _nextPts;

    public int Count { get { lock (_items) return _items.Count; } }

    /// <summary>End time of the newest queued audio (negative if empty since the last Clear).</summary>
    public double LastPts { get { lock (_items) return _items.Count == 0 ? -1 : _nextPts; } }

    /// <summary>Time stamp of the packet that is decoded next.</summary>
    public void BeginPacket(double pts) => _nextPts = pts;

    /// <summary>Called by the decoder: split into small pieces with increasing time stamps.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        lock (_items)
        {
            for (int off = 0; off < pcm.Length; off += PieceBytes)
            {
                int n = Math.Min(PieceBytes, pcm.Length - off);
                _items.Enqueue((_nextPts, pcm.Slice(off, n).ToArray()));
                _nextPts += n / (double)BytesPerSecond;
            }
            while (_items.Count > 50 * 30) _items.Dequeue(); // never keep more than 30 s
        }
    }

    /// <summary>Hands all pieces up to <paramref name="until"/> to the sink, dropping late ones.</summary>
    public void Release(double until, double dropBefore, IAudioSink sink)
    {
        while (true)
        {
            (double Pts, byte[] Data) item;
            lock (_items)
            {
                if (_items.Count == 0 || _items.Peek().Pts > until) return;
                item = _items.Dequeue();
            }
            if (item.Pts >= dropBefore) sink.Write(item.Data);
        }
    }

    public void Clear()
    {
        lock (_items) _items.Clear();
    }
}

/// <summary>
/// Separate demuxer for the audio track of a recording, so audio can be read ahead
/// independently of where the file stores it relative to the video.
/// </summary>
internal sealed unsafe class AudioTrackReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVPacket* _pkt;
    private AudioDecoder? _decoder;
    private readonly int _ai;
    private readonly double _tb;
    private readonly double _startPts;
    private bool _eof;
    private bool _failed;

    // fragmented MP4: context starts at a fragment, time = _base + pts - _origin
    private readonly FragmentedMp4? _fmp4;
    private FragmentReader? _reader;
    private double _base, _origin = double.NaN;

    private AudioTrackReader(AVFormatContext* fmt, int ai, double startPts)
    {
        _fmt = fmt;
        _ai = ai;
        _startPts = startPts;
        _tb = ffmpeg.av_q2d(fmt->streams[ai]->time_base);
        DiscardOthers();
        _pkt = ffmpeg.av_packet_alloc();
    }

    private AudioTrackReader(FragmentedMp4 fmp4, FragmentReader reader, int ai) : this(reader.Context, ai, 0)
    {
        _fmp4 = fmp4;
        _reader = reader;
    }

    private void DiscardOthers()
    {
        for (int i = 0; i < _fmt->nb_streams; i++)
            if (i != _ai) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL; // skip the video data cheaply
    }

    public static AudioTrackReader? Open(FragmentedMp4 fmp4)
    {
        FragmentReader? reader = null;
        try
        {
            reader = fmp4.OpenAt(fmp4.Locate(0).Fragment);
            int ai = ffmpeg.av_find_best_stream(reader.Context, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (ai < 0) { reader.Dispose(); return null; }
            return new AudioTrackReader(fmp4, reader, ai);
        }
        catch (Exception ex)
        {
            Log.Debug($"Audio reader for {fmp4.Path}: {ex.Message}");
            reader?.Dispose();
            return null;
        }
    }

    public static AudioTrackReader? Open(string path, double startPts)
    {
        AVFormatContext* fmt = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { ffmpeg.avformat_close_input(&fmt); return null; }
            int ai = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (ai < 0) { ffmpeg.avformat_close_input(&fmt); return null; }
            return new AudioTrackReader(fmt, ai, startPts);
        }
        catch (Exception ex)
        {
            Log.Debug($"Audio reader for {path}: {ex.Message}");
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            return null;
        }
    }

    public void Seek(double seconds)
    {
        if (_failed) return;
        if (_fmp4 != null)
        {
            try
            {
                var start = _fmp4.Locate(seconds);
                _reader?.Dispose();
                _reader = null;
                _fmt = null;
                _reader = _fmp4.OpenAt(start.Fragment);
                _fmt = _reader.Context;
                DiscardOthers();
                _base = start.Time;
                _origin = double.NaN;
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Debug($"Playback audio seek: {ex.Message}");
            }
        }
        else
        {
            long ts = (long)((seconds + _startPts) / _tb);
            ffmpeg.av_seek_frame(_fmt, _ai, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        _decoder?.Dispose();
        _decoder = null;
        _eof = false;
    }

    /// <summary>Decodes audio into <paramref name="queue"/> until it reaches <paramref name="untilPts"/>.</summary>
    public void FillUntil(double untilPts, TimedAudioQueue queue)
    {
        int guard = 0;
        while (!_eof && !_failed && queue.LastPts < untilPts && guard++ < 200)
        {
            int ret = ffmpeg.av_read_frame(_fmt, _pkt);
            if (ret < 0) { _eof = true; break; }
            try
            {
                if (_pkt->stream_index != _ai) continue;
                double pts;
                if (_pkt->pts == ffmpeg.AV_NOPTS_VALUE) pts = queue.LastPts;
                else if (_fmp4 != null)
                {
                    if (double.IsNaN(_origin)) _origin = _pkt->pts * _tb;
                    pts = _base + _pkt->pts * _tb - _origin;
                }
                else pts = _pkt->pts * _tb - _startPts;
                _decoder ??= new AudioDecoder(_fmt->streams[_ai]->codecpar);
                queue.BeginPacket(pts);
                _decoder.Decode(_pkt, queue);
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Debug($"Playback audio: {ex.Message}");
            }
            finally
            {
                ffmpeg.av_packet_unref(_pkt);
            }
        }
    }

    public void Dispose()
    {
        _decoder?.Dispose();
        _decoder = null;
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_reader != null) { _reader.Dispose(); _reader = null; _fmt = null; } // owns _fmt
        else if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }
}
