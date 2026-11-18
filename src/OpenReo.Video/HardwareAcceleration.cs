using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Video;

/// <summary>
/// Manages GPU decoding devices. One device per type is created once and shared by all
/// decoders (FFmpeg device contexts are thread safe), which keeps GPU memory usage low
/// even with 25 parallel streams.
/// </summary>
public sealed unsafe class HardwareAcceleration : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<AVHWDeviceType, IntPtr> _devices = new();
    private readonly HashSet<AVHWDeviceType> _failed = new();

    public static HardwareAcceleration Shared { get; } = new();

    /// <summary>
    /// Device types to try for the given mode, in order of preference.
    /// Auto prefers CUDA/NVDEC (NVIDIA) because it supports GPU side scaling (scale_cuda),
    /// which measured ~25% more throughput and ~70% less CPU than D3D11VA with 25 x 4K streams.
    /// On Intel / AMD GPUs the CUDA device cannot be created and D3D11VA is used.
    /// </summary>
    public static IReadOnlyList<AVHWDeviceType> CandidateTypes(HardwareAccelerationMode mode) => mode switch
    {
        HardwareAccelerationMode.Auto => new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 },
        HardwareAccelerationMode.D3D11VA => new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        HardwareAccelerationMode.DXVA2 => new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 },
        HardwareAccelerationMode.Cuda => new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA },
        _ => Array.Empty<AVHWDeviceType>(),
    };

    public static string DisplayName(AVHWDeviceType type) => type switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "D3D11VA",
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "DXVA2",
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "CUDA/NVDEC",
        _ => ffmpeg.av_hwdevice_get_type_name(type) ?? type.ToString(),
    };

    /// <summary>
    /// Returns a new reference to the shared device of the given type, or null if the
    /// device cannot be created on this machine. The caller must unref the returned buffer.
    /// </summary>
    public AVBufferRef* AcquireDevice(AVHWDeviceType type)
    {
        lock (_sync)
        {
            if (_failed.Contains(type)) return null;
            if (!_devices.TryGetValue(type, out var ptr))
            {
                AVBufferRef* dev = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&dev, type, null, null, 0);
                if (ret < 0 || dev == null)
                {
                    _failed.Add(type);
                    Log.Warn($"Hardware device {DisplayName(type)} not available: {FFmpegHelper.ErrorString(ret)}");
                    return null;
                }
                ptr = (IntPtr)dev;
                _devices[type] = ptr;
                Log.Info($"Hardware device {DisplayName(type)} created.");
            }
            return ffmpeg.av_buffer_ref((AVBufferRef*)ptr);
        }
    }

    /// <summary>Returns the hardware pixel format a codec uses with the given device type.</summary>
    public static AVPixelFormat GetHwPixelFormat(AVCodec* codec, AVHWDeviceType type)
    {
        for (int i = 0; ; i++)
        {
            var cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null) return AVPixelFormat.AV_PIX_FMT_NONE;
            const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
            if ((cfg->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 && cfg->device_type == type)
                return cfg->pix_fmt;
        }
    }

    /// <summary>Checks which GPU decoders are usable (for the settings dialog).</summary>
    public IReadOnlyList<string> ProbeAvailable()
    {
        var list = new List<string>();
        foreach (var t in new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA })
        {
            var d = AcquireDevice(t);
            if (d != null) { list.Add(DisplayName(t)); ffmpeg.av_buffer_unref(&d); }
        }
        return list;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var p in _devices.Values)
            {
                var b = (AVBufferRef*)p;
                ffmpeg.av_buffer_unref(&b);
            }
            _devices.Clear();
        }
    }
}
