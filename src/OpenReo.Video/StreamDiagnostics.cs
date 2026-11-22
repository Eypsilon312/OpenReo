using FFmpeg.AutoGen;
using OpenReo.Core.Models;

namespace OpenReo.Video;

/// <summary>Result of <see cref="StreamDiagnostics.Measure"/>.</summary>
public sealed record StreamTimingReport(
    string Codec, int Width, int Height, int Frames, double Seconds,
    double StreamFps, double MaxArrivalGapMs, double P95ArrivalGapMs, int Bursts, double JitterMs, string? Error,
    double MaxTimestampGapMs = 0, int TimestampGaps = 0)
{
    public override string ToString() => Error != null
        ? $"Fehler: {Error}"
        : $"{Codec} {Width}x{Height}, {Frames} Bilder in {Seconds:0.0} s = {StreamFps:0.0} fps (laut Zeitstempel), Zeitstempel-Lücken: max. {MaxTimestampGapMs:0} ms, {TimestampGaps} × > 150 ms, " +
          $"Ankunft: max. Lücke {MaxArrivalGapMs:0} ms, 95 % < {P95ArrivalGapMs:0} ms, {Bursts} Bündel, Jitter {JitterMs:0} ms";
}

/// <summary>
/// Measures how evenly a stream delivers its frames: arrival time of every video packet compared
/// with its time stamp. Large gaps / bursts mean the network or camera delivers unevenly.
/// </summary>
public static unsafe class StreamDiagnostics
{
    public static StreamTimingReport Measure(string url, RtspTransport transport, double seconds)
    {
        FFmpegLoader.Initialize();
        AVFormatContext* fmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            AVDictionary* d = null;
            ffmpeg.av_dict_set(&d, "rtsp_transport", transport == RtspTransport.Udp ? "udp" : "tcp", 0);
            ffmpeg.av_dict_set(&d, "allowed_media_types", "video", 0);
            ffmpeg.av_dict_set(&d, "timeout", "10000000", 0);
            int ret = ffmpeg.avformat_open_input(&fmt, url, null, &d);
            ffmpeg.av_dict_free(&d);
            if (ret < 0) { fmt = null; return Fail(FFmpegHelper.FriendlyError(ret, false)); }
            fmt->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return Fail("find_stream_info");
            int vi = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (vi < 0) return Fail("kein Video");
            var st = fmt->streams[vi];
            double tb = ffmpeg.av_q2d(st->time_base);

            var arrivals = new List<double>();
            var pts = new List<double>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (ffmpeg.av_read_frame(fmt, pkt) < 0) break;
                if (pkt->stream_index == vi)
                {
                    arrivals.Add(sw.Elapsed.TotalMilliseconds);
                    long ts = pkt->dts != ffmpeg.AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                    pts.Add(ts != ffmpeg.AV_NOPTS_VALUE ? ts * tb * 1000 : double.NaN);
                }
                ffmpeg.av_packet_unref(pkt);
            }
            // ignore the start-up phase (frames buffered during stream analysis arrive at once)
            int skip = arrivals.FindIndex(a => a > 2000);
            if (skip > 0) { arrivals = arrivals.Skip(skip).ToList(); pts = pts.Skip(skip).ToList(); }
            if (arrivals.Count < 3) return Fail("zu wenige Bilder");

            var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
            var valid = pts.Where(p => !double.IsNaN(p)).ToList();
            double span = valid.Count > 1 ? (valid.Max() - valid.Min()) / 1000.0 : arrivals[^1] / 1000.0;
            double fps = span > 0 ? (pts.Count - 1) / span : 0;
            double expected = fps > 0 ? 1000 / fps : 40;
            int bursts = gaps.Count(g => g < expected * 0.25);
            // jitter: variation of (arrival − time stamp) – how much a buffer must absorb
            var p0 = valid.Count > 0 ? valid.Min() : 0;
            var offsets = arrivals.Zip(pts, (a, p) => a - (p - p0)).Where(o => !double.IsNaN(o)).ToList();
            double jitter = offsets.Count > 0 ? offsets.Max() - offsets.Min() : 0;
            // gaps in the time stamps themselves = frames the camera did not send at all
            var tsGaps = valid.Zip(valid.Skip(1), (x, y) => y - x).ToList();
            return new StreamTimingReport(ffmpeg.avcodec_get_name(st->codecpar->codec_id)?.ToUpperInvariant() ?? "?",
                st->codecpar->width, st->codecpar->height, arrivals.Count, sw.Elapsed.TotalSeconds, fps,
                gaps[^1], gaps[(int)(gaps.Count * 0.95)], bursts, jitter, null,
                tsGaps.Count > 0 ? tsGaps.Max() : 0, tsGaps.Count(g => g > 150));
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }

        static StreamTimingReport Fail(string e) => new("", 0, 0, 0, 0, 0, 0, 0, 0, 0, e);
    }
}
