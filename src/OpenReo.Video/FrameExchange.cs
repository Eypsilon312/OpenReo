using System.Runtime.InteropServices;

namespace OpenReo.Video;

/// <summary>
/// A decoded, converted video frame in BGRA (32 bit) format stored in unmanaged memory.
/// </summary>
public sealed unsafe class VideoFrame : IDisposable
{
    private nuint _capacity;

    public IntPtr Data { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public int SizeInBytes => Stride * Height;

    /// <summary>Incremented for every new frame written into this buffer.</summary>
    public long Sequence { get; internal set; }

    /// <summary>Makes sure the buffer can hold a frame of the given size.</summary>
    internal void Ensure(int width, int height)
    {
        var needed = (nuint)(width * 4 * height);
        if (needed > _capacity)
        {
            if (Data != IntPtr.Zero) NativeMemory.AlignedFree((void*)Data);
            Data = (IntPtr)NativeMemory.AlignedAlloc(needed, 64);
            _capacity = needed;
        }
        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        if (Data != IntPtr.Zero)
        {
            NativeMemory.AlignedFree((void*)Data);
            Data = IntPtr.Zero;
            _capacity = 0;
        }
    }
}

/// <summary>
/// Lock based triple buffer between the decoder thread (single writer) and the UI
/// thread (single reader). The writer never blocks the reader and the reader always
/// gets the most recent frame; outdated frames are dropped.
/// </summary>
public sealed class FrameExchange : IDisposable
{
    private readonly object _sync = new();
    private VideoFrame _write = new();
    private VideoFrame _ready = new();
    private VideoFrame _read = new();
    private bool _hasNew;
    private long _sequence;
    private bool _disposed;

    /// <summary>True when the last published frame has not been picked up yet.</summary>
    public bool HasUnconsumedFrame
    {
        get { lock (_sync) return _hasNew; }
    }

    /// <summary>Writer: returns the buffer to fill (sized width x height).</summary>
    internal VideoFrame BeginWrite(int width, int height)
    {
        _write.Ensure(width, height);
        return _write;
    }

    /// <summary>Writer: publishes the buffer obtained by <see cref="BeginWrite"/>.</summary>
    internal void EndWrite()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _write.Sequence = ++_sequence;
            (_write, _ready) = (_ready, _write);
            _hasNew = true;
        }
    }

    /// <summary>
    /// Reader (UI thread): returns the newest frame if a new one is available.
    /// The returned frame stays valid until the next call.
    /// </summary>
    public bool TryAcquireLatest(out VideoFrame frame)
    {
        lock (_sync)
        {
            if (!_hasNew || _disposed)
            {
                frame = _read;
                return false;
            }
            (_read, _ready) = (_ready, _read);
            _hasNew = false;
            frame = _read;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _write.Dispose();
            _ready.Dispose();
            _read.Dispose();
        }
    }
}
