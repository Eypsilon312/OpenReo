using FFmpeg.AutoGen;
using OpenReo.Core.Models;

namespace OpenReo.Video;

public sealed record StreamProbeResult(bool Success, string? Codec, int Width, int Height, double Fps, string? Error)
{
    public override string ToString() => Success
        ? $"{Codec} {Width}×{Height}" + (Fps > 0 ? $" @ {Fps:0.#} fps" : "")
        : $"Fehler: {Error}";
}

/// <summary>
/// Opens an RTSP URL briefly to read codec / resolution. Used when setting up a camera
/// (stream detection and the "test streams" button).
/// </summary>
public static unsafe class StreamProbe
{
    public static Task<StreamProbeResult> ProbeAsync(string url, RtspTransport transport, TimeSpan timeout, CancellationToken ct = default)
        => Task.Factory.StartNew(() => Probe(url, transport, timeout, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static StreamProbeResult Probe(string url, RtspTransport transport, TimeSpan timeout, CancellationToken ct)
    {
        FFmpegLoader.Initialize();
        AVFormatContext* fmt = ffmpeg.avformat_alloc_context();
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        bool timedOut = false;
        AVIOInterruptCB_callback cb = _ =>
        {
            if (ct.IsCancellationRequested) return 1;
            if (Environment.TickCount64 > deadline) { timedOut = true; return 1; }
            return 0;
        };
        fmt->interrupt_callback.callback = cb;

        AVDictionary* dict = null;
        ffmpeg.av_dict_set(&dict, "rtsp_transport", transport == RtspTransport.Udp ? "udp" : "tcp", 0);
        ffmpeg.av_dict_set(&dict, "allowed_media_types", "video", 0);
        ffmpeg.av_dict_set(&dict, "timeout", ((long)timeout.TotalMilliseconds * 1000).ToString(), 0);
        ffmpeg.av_dict_set(&dict, "user_agent", "OpenReo", 0);
        ffmpeg.av_dict_set(&dict, "analyzeduration", "1500000", 0);
        try
        {
            int ret = ffmpeg.avformat_open_input(&fmt, url, null, &dict);
            if (ret < 0)
            {
                fmt = null;
                return new StreamProbeResult(false, null, 0, 0, 0, FFmpegHelper.FriendlyError(ret, timedOut));
            }
            ret = ffmpeg.avformat_find_stream_info(fmt, null);
            if (ret < 0) return new StreamProbeResult(false, null, 0, 0, 0, FFmpegHelper.FriendlyError(ret, timedOut));
            int idx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (idx < 0) return new StreamProbeResult(false, null, 0, 0, 0, "Kein Videostream");
            var st = fmt->streams[idx];
            var par = st->codecpar;
            double fps = st->avg_frame_rate.den > 0 ? (double)st->avg_frame_rate.num / st->avg_frame_rate.den
                       : st->r_frame_rate.den > 0 ? (double)st->r_frame_rate.num / st->r_frame_rate.den : 0;
            if (fps > 200) fps = 0;
            return new StreamProbeResult(true, ffmpeg.avcodec_get_name(par->codec_id)?.ToUpperInvariant(), par->width, par->height, fps, null);
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            GC.KeepAlive(cb);
        }
    }
}
