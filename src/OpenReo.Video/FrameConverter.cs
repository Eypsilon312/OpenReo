using FFmpeg.AutoGen;

namespace OpenReo.Video;

/// <summary>
/// Converts decoded frames (GPU surfaces or YUV software frames) into BGRA at the size
/// actually required by the display. The source stream (e.g. Clear / 4K) is always
/// decoded in full – only the final picture is scaled to the on-screen pixel size, which
/// keeps memory bandwidth low when many cameras are shown.
/// </summary>
internal sealed unsafe class FrameConverter : IDisposable
{
    private SwsContext* _sws;
    private AVFrame* _swFrame;
    private int _srcW, _srcH, _dstW, _dstH;
    private AVPixelFormat _srcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVColorRange _srcRange;
    private AVColorSpace _srcSpace;

    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcStride = new int[4];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];

    private readonly GpuScaler? _gpuScaler;

    /// <param name="hwType">Device type of the decoder; enables GPU side scaling when supported.</param>
    public FrameConverter(AVHWDeviceType hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
    {
        _swFrame = ffmpeg.av_frame_alloc();
        _gpuScaler = GpuScaler.Create(hwType);
    }

    /// <summary>True when GPU scaling is active.</summary>
    public bool UsesGpuScaling => _gpuScaler is { Disabled: false };

    /// <summary>Computes the output size: fits into the target box, never larger than the source.</summary>
    public static (int w, int h) ComputeOutputSize(int srcW, int srcH, int targetW, int targetH)
    {
        if (targetW <= 0 || targetH <= 0) return (srcW & ~1, srcH & ~1);
        double scale = Math.Min(1.0, Math.Min((double)targetW / srcW, (double)targetH / srcH));
        int w = Math.Max(16, (int)Math.Round(srcW * scale)) & ~1;
        int h = Math.Max(16, (int)Math.Round(srcH * scale)) & ~1;
        return (w, h);
    }

    /// <summary>Converts <paramref name="frame"/> into the writer buffer of <paramref name="exchange"/>.</summary>
    public bool Convert(AVFrame* frame, int targetW, int targetH, FrameExchange exchange)
    {
        if (frame->width <= 0 || frame->height <= 0) return false;
        var (dw, dh) = ComputeOutputSize(frame->width, frame->height, targetW, targetH);

        AVFrame* src = frame;
        if (frame->hw_frames_ctx != null)
        {
            // Scale on the GPU first (if smaller than the source), then download (NV12) into system memory.
            AVFrame* hw = frame;
            if (_gpuScaler != null && (dw < frame->width || dh < frame->height))
            {
                var scaled = _gpuScaler.Scale(frame, dw, dh);
                if (scaled != null) hw = scaled;
            }
            ffmpeg.av_frame_unref(_swFrame);
            if (ffmpeg.av_hwframe_transfer_data(_swFrame, hw, 0) < 0) return false;
            _swFrame->color_range = frame->color_range;
            _swFrame->colorspace = frame->colorspace;
            src = _swFrame;
        }

        int sw = src->width, sh = src->height;
        if (sw <= 0 || sh <= 0) return false;
        var fmt = (AVPixelFormat)src->format;

        if (_sws == null || sw != _srcW || sh != _srcH || fmt != _srcFmt || dw != _dstW || dh != _dstH
            || src->color_range != _srcRange || src->colorspace != _srcSpace)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            // Bilinear is a good speed/quality trade-off for down scaling; pure colour conversion uses point sampling.
            int flags = (dw == sw && dh == sh) ? (int)SwsFlags.SWS_POINT : (int)SwsFlags.SWS_BILINEAR;
            _sws = ffmpeg.sws_getContext(sw, sh, fmt, dw, dh, AVPixelFormat.AV_PIX_FMT_BGRA, flags, null, null, null);
            if (_sws == null) return false;
            _srcW = sw; _srcH = sh; _srcFmt = fmt; _dstW = dw; _dstH = dh;
            _srcRange = src->color_range; _srcSpace = src->colorspace;
            ApplyColorspace(fmt, sh);
        }

        var dst = exchange.BeginWrite(dw, dh);
        for (int i = 0; i < 4; i++)
        {
            _srcData[i] = src->data[(uint)i];
            _srcStride[i] = src->linesize[(uint)i];
        }
        _dstData[0] = (byte*)dst.Data;
        _dstStride[0] = dst.Stride;

        ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, sh, _dstData, _dstStride);
        exchange.EndWrite();
        return true;
    }

    private void ApplyColorspace(AVPixelFormat fmt, int height)
    {
        bool fullRange = _srcRange == AVColorRange.AVCOL_RANGE_JPEG
                         || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P
                         || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ422P
                         || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ444P;
        int cs = _srcSpace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => ffmpeg.SWS_CS_ITU601,
            _ => height >= 720 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601,
        };
        int* srcCoef = ffmpeg.sws_getCoefficients(cs);
        int* dstCoef = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_DEFAULT);
        var inv = new int_array4();
        var tab = new int_array4();
        for (uint i = 0; i < 4; i++) { inv[i] = srcCoef[i]; tab[i] = dstCoef[i]; }
        ffmpeg.sws_setColorspaceDetails(_sws, inv, fullRange ? 1 : 0, tab, 1, 0, 1 << 16, 1 << 16);
    }

    public void Dispose()
    {
        _gpuScaler?.Dispose();
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_swFrame != null) { var f = _swFrame; ffmpeg.av_frame_free(&f); _swFrame = null; }
    }
}
