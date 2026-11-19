using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenReo.Video;

namespace OpenReo.App.Video;

/// <summary>
/// Display surface of one stream. All tiles showing the same stream share this bitmap,
/// so a frame is copied to the GPU only once regardless of the number of tiles.
/// </summary>
public sealed class VideoSurface
{
    internal int RefCount;

    public FrameExchange Frames { get; }
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Raised (UI thread) when a new bitmap instance was created (first frame / size change).</summary>
    public event Action<VideoSurface>? BitmapChanged;

    public VideoSurface(FrameExchange frames) { Frames = frames; }

    /// <summary>Copies the newest decoded frame into the bitmap. UI thread only.</summary>
    internal unsafe void Update()
    {
        if (!Frames.TryAcquireLatest(out var frame) || frame.Data == IntPtr.Zero) return;

        bool created = false;
        if (Bitmap == null || Bitmap.PixelWidth != frame.Width || Bitmap.PixelHeight != frame.Height)
        {
            Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            created = true;
        }

        var bmp = Bitmap;
        bmp.Lock();
        try
        {
            int rowBytes = frame.Width * 4;
            if (bmp.BackBufferStride == frame.Stride)
            {
                Buffer.MemoryCopy((void*)frame.Data, (void*)bmp.BackBuffer, (long)bmp.BackBufferStride * bmp.PixelHeight, (long)frame.Stride * frame.Height);
            }
            else
            {
                for (int y = 0; y < frame.Height; y++)
                    Buffer.MemoryCopy((byte*)frame.Data + (long)y * frame.Stride, (byte*)bmp.BackBuffer + (long)y * bmp.BackBufferStride, bmp.BackBufferStride, rowBytes);
            }
            bmp.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
        }
        finally
        {
            bmp.Unlock();
        }

        if (created) BitmapChanged?.Invoke(this);
    }
}

/// <summary>
/// Pulls new frames from all active streams once per display refresh
/// (CompositionTarget.Rendering) and pushes them into their surfaces.
/// </summary>
public static class RenderLoop
{
    private static readonly Dictionary<FrameExchange, VideoSurface> Surfaces = new();
    private static bool _hooked;

    public static VideoSurface Acquire(RtspStream stream) => Acquire(stream.Frames);

    /// <summary>Surface for any frame source (live stream or recording playback).</summary>
    public static VideoSurface Acquire(FrameExchange frames)
    {
        if (!Surfaces.TryGetValue(frames, out var s))
        {
            s = new VideoSurface(frames);
            Surfaces[frames] = s;
        }
        s.RefCount++;
        Hook();
        return s;
    }

    public static void Release(VideoSurface surface)
    {
        if (--surface.RefCount <= 0)
        {
            Surfaces.Remove(surface.Frames);
            if (Surfaces.Count == 0) Unhook();
        }
    }

    private static void Hook()
    {
        if (_hooked) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private static void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        foreach (var s in Surfaces.Values)
        {
            try { s.Update(); }
            catch (Exception ex) { Core.Diagnostics.Log.Error("Surface update failed", ex); }
        }
    }
}
