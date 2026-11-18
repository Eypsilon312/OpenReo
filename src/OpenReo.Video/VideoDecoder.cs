using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Video;

/// <summary>
/// H.264 / H.265 decoder. Uses GPU decoding (D3D11VA / DXVA2 / CUDA) when available
/// and falls back to multi threaded software decoding otherwise.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private readonly AVCodecContext_get_format _getFormat; // must stay referenced while the decoder lives
    private AVPixelFormat _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _hwRejected;

    public string CodecName { get; }
    public AVHWDeviceType HwType { get; } = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

    /// <summary>True when frames are really decoded on the GPU.</summary>
    public bool IsHardware => _hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE && !_hwRejected;

    public string DecoderDescription =>
        IsHardware ? $"GPU ({HardwareAcceleration.DisplayName(HwType)})" : "CPU (Software)";

    public VideoDecoder(AVCodecParameters* par, HardwareAccelerationMode mode, bool lowLatency)
    {
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null)
            throw new FFmpegException($"Kein Decoder für Codec {ffmpeg.avcodec_get_name(par->codec_id)} verfügbar.");
        CodecName = ffmpeg.avcodec_get_name(par->codec_id)?.ToUpperInvariant() ?? "?";

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new FFmpegException("Decoder-Kontext konnte nicht erstellt werden.");
        FFmpegHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(_ctx, par), "Decoder-Parameter");

        _getFormat = SelectFormat;

        // Try the GPU device types in order of preference.
        foreach (var type in HardwareAcceleration.CandidateTypes(mode))
        {
            var pixFmt = HardwareAcceleration.GetHwPixelFormat(codec, type);
            if (pixFmt == AVPixelFormat.AV_PIX_FMT_NONE) continue;
            var dev = HardwareAcceleration.Shared.AcquireDevice(type);
            if (dev == null) continue;
            _ctx->hw_device_ctx = dev; // ownership of the reference moves to the codec context
            _ctx->get_format = _getFormat;
            _ctx->extra_hw_frames = 2; // one frame in the converter + one spare
            _hwPixFmt = pixFmt;
            HwType = type;
            break;
        }

        if (IsHardware)
        {
            _ctx->thread_count = 1; // GPU does the work, threads would only add latency
        }
        else
        {
            // Software: slice threads add no latency; frame threads add (n-1) frames of delay.
            _ctx->thread_count = 0; // auto
            _ctx->thread_type = lowLatency ? ffmpeg.FF_THREAD_SLICE : ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            if (lowLatency && par->width * par->height > 1920 * 1088)
                _ctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE; // big streams need frame threads
        }
        if (lowLatency) _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            var err = FFmpegHelper.ErrorString(ret);
            Dispose();
            throw new FFmpegException($"Decoder konnte nicht geöffnet werden: {err}", ret);
        }
    }

    /// <summary>get_format callback: choose the GPU surface format if the decoder offers it.</summary>
    private AVPixelFormat SelectFormat(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        AVPixelFormat firstSoftware = AVPixelFormat.AV_PIX_FMT_NONE;
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == _hwPixFmt && !_hwRejected) return *p;
            var desc = ffmpeg.av_pix_fmt_desc_get(*p);
            if (firstSoftware == AVPixelFormat.AV_PIX_FMT_NONE && desc != null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
                firstSoftware = *p;
        }
        if (_hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE && !_hwRejected)
        {
            _hwRejected = true; // e.g. unsupported profile -> silently decode in software
            Log.Warn($"GPU decoding not possible for this {CodecName} stream – using software decoder.");
        }
        return firstSoftware;
    }

    public int Send(AVPacket* packet) => ffmpeg.avcodec_send_packet(_ctx, packet);

    public int Receive(AVFrame* frame) => ffmpeg.avcodec_receive_frame(_ctx, frame);

    public void Flush()
    {
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
    }

    public void Dispose()
    {
        if (_ctx != null)
        {
            var c = _ctx;
            ffmpeg.avcodec_free_context(&c); // also releases hw_device_ctx reference
            _ctx = null;
        }
    }
}
