using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Video;

/// <summary>
/// Scales decoded GPU surfaces on the GPU (D3D11 video processor / CUDA) before they are
/// downloaded to system memory. A 4K Clear frame shown in a 5x5 tile is thus reduced from
/// ~12 MB to ~0.5 MB per frame before it crosses the PCIe bus. The decoded stream itself
/// is untouched – this only affects the size of the picture that is displayed.
/// </summary>
internal sealed unsafe class GpuScaler : IDisposable
{
    private readonly string _filterName;
    private AVFilterGraph* _graph;
    private AVFilterContext* _src;
    private AVFilterContext* _sink;
    private AVFrame* _out;
    private int _inW, _inH, _outW, _outH, _inFormat;
    private IntPtr _framesCtxData;
    private int _failures;

    /// <summary>True after repeated errors – the converter then uses CPU scaling only.</summary>
    public bool Disabled { get; private set; }

    private GpuScaler(string filterName)
    {
        _filterName = filterName;
        _out = ffmpeg.av_frame_alloc();
    }

    /// <summary>Creates a scaler for the device type, or null if none is available.</summary>
    public static GpuScaler? Create(AVHWDeviceType type)
    {
        // Note: scale_d3d11 / scale_d3d12 (FFmpeg 8.1) cannot allocate their output textures for
        // decoder surfaces on current drivers (E_INVALIDARG), so only CUDA is supported here.
        string? name = type switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "scale_cuda",
            _ => null,
        };
        if (name == null || ffmpeg.avfilter_get_by_name(name) == null) return null;
        return new GpuScaler(name);
    }

    /// <summary>
    /// Scales <paramref name="hwFrame"/> to the given size. Returns a GPU frame owned by the
    /// scaler (valid until the next call) or null if scaling failed.
    /// </summary>
    public AVFrame* Scale(AVFrame* hwFrame, int width, int height)
    {
        if (Disabled) return null;
        try
        {
            if (_graph == null || hwFrame->width != _inW || hwFrame->height != _inH || hwFrame->format != _inFormat
                || width != _outW || height != _outH || (IntPtr)hwFrame->hw_frames_ctx->data != _framesCtxData)
            {
                Build(hwFrame, width, height);
            }

            int ret = ffmpeg.av_buffersrc_add_frame_flags(_src, hwFrame, 8 /* AV_BUFFERSRC_FLAG_KEEP_REF */);
            if (ret < 0) return Fail($"buffersrc: {FFmpegHelper.ErrorString(ret)}");
            ffmpeg.av_frame_unref(_out);
            ret = ffmpeg.av_buffersink_get_frame(_sink, _out);
            if (ret < 0) return ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) ? null : Fail($"buffersink: {FFmpegHelper.ErrorString(ret)}");
            _failures = 0;
            return _out;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private AVFrame* Fail(string message)
    {
        FreeGraph();
        if (++_failures >= 3)
        {
            Disabled = true;
            Log.Warn($"GPU scaling ({_filterName}) disabled, falling back to CPU scaling: {message}");
        }
        return null;
    }

    private void Build(AVFrame* frame, int width, int height)
    {
        FreeGraph();
        _graph = ffmpeg.avfilter_graph_alloc();
        if (_graph == null) throw new FFmpegException("avfilter_graph_alloc");
        _graph->nb_threads = 1;

        _src = ffmpeg.avfilter_graph_alloc_filter(_graph, ffmpeg.avfilter_get_by_name("buffer"), "in");
        if (_src == null) throw new FFmpegException("buffer filter");
        var par = ffmpeg.av_buffersrc_parameters_alloc();
        par->format = frame->format;
        par->width = frame->width;
        par->height = frame->height;
        par->time_base = new AVRational { num = 1, den = 90000 };
        par->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        par->hw_frames_ctx = frame->hw_frames_ctx;
        int ret = ffmpeg.av_buffersrc_parameters_set(_src, par);
        ffmpeg.av_free(par);
        FFmpegHelper.ThrowIfError(ret, "buffersrc parameters");
        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_init_str(_src, null), "buffersrc init");

        AVFilterContext* scale = null;
        var args = _filterName == "scale_cuda" ? $"w={width}:h={height}:interp_algo=bilinear" : $"width={width}:height={height}";
        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_graph_create_filter(&scale, ffmpeg.avfilter_get_by_name(_filterName), "scale", args, null, _graph), _filterName);

        AVFilterContext* sink = null;
        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "buffersink");
        _sink = sink;

        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_link(_src, 0, scale, 0), "link");
        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_link(scale, 0, _sink, 0), "link");
        FFmpegHelper.ThrowIfError(ffmpeg.avfilter_graph_config(_graph, null), "graph config");

        _inW = frame->width; _inH = frame->height; _inFormat = frame->format;
        _outW = width; _outH = height;
        _framesCtxData = (IntPtr)frame->hw_frames_ctx->data;
    }

    private void FreeGraph()
    {
        if (_graph != null)
        {
            var g = _graph;
            ffmpeg.avfilter_graph_free(&g);
            _graph = null;
        }
        _src = null;
        _sink = null;
        _framesCtxData = IntPtr.Zero;
    }

    public void Dispose()
    {
        FreeGraph();
        if (_out != null) { var f = _out; ffmpeg.av_frame_free(&f); _out = null; }
    }
}
