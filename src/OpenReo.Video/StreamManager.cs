using OpenReo.Core.Cameras;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Video;

/// <summary>
/// RTSP stream manager. Streams are shared per (camera, quality): if the same camera is
/// shown in several tiles or the layout changes, the existing connection is reused.
/// Unused streams are kept alive for a configurable delay so that switching layouts or
/// maximizing a tile never causes a reconnect.
/// </summary>
public sealed class StreamManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid, StreamQuality), RtspStream> _streams = new();
    private readonly Timer _cleanupTimer;
    private StreamOptions _options;
    private bool _disposed;

    /// <summary>Raised on a worker thread whenever a stream changes its status.</summary>
    public event Action<RtspStream>? StreamStatusChanged;

    public TimeSpan ReleaseDelay { get; set; }

    public StreamManager(StreamOptions options, TimeSpan releaseDelay)
    {
        _options = options;
        ReleaseDelay = releaseDelay;
        _cleanupTimer = new Timer(_ => Cleanup(), null, 1000, 1000);
    }

    public StreamOptions Options => _options;

    /// <summary>Gets (or opens) the stream for camera + quality and returns a lease on it.</summary>
    public StreamLease Acquire(CameraConfig camera, StreamQuality quality)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (camera.Id, quality);
            if (!_streams.TryGetValue(key, out var stream))
            {
                stream = new RtspStream(camera.Id, quality, camera.Name,
                    RtspUrlBuilder.Build(camera, quality), RtspUrlBuilder.BuildDisplay(camera, quality), _options);
                stream.StatusChanged += OnStreamStatusChanged;
                _streams[key] = stream;
                stream.Start();
            }
            var lease = new StreamLease(this, stream);
            stream.AddLease(lease);
            return lease;
        }
    }

    internal void Release(StreamLease lease, bool immediate)
    {
        RtspStream? toStop = null;
        lock (_sync)
        {
            var remaining = lease.Stream.RemoveLease(lease);
            if (remaining == 0 && (immediate || ReleaseDelay <= TimeSpan.Zero))
            {
                toStop = lease.Stream;
                _streams.Remove((toStop.CameraId, toStop.Quality));
            }
        }
        if (toStop != null) StopStream(toStop);
    }

    public RtspStream? TryGet(Guid cameraId, StreamQuality quality)
    {
        lock (_sync) return _streams.TryGetValue((cameraId, quality), out var s) ? s : null;
    }

    public IReadOnlyList<RtspStream> Streams
    {
        get { lock (_sync) return _streams.Values.ToList(); }
    }

    /// <summary>Camera connection data changed: reconnect all its streams with the new URL.</summary>
    public void RestartCamera(CameraConfig camera)
    {
        lock (_sync)
        {
            foreach (var s in _streams.Values.Where(s => s.CameraId == camera.Id))
                s.Restart(RtspUrlBuilder.Build(camera, s.Quality), RtspUrlBuilder.BuildDisplay(camera, s.Quality), _options);
        }
    }

    /// <summary>Camera removed: close its streams right away.</summary>
    public void CloseCamera(Guid cameraId)
    {
        List<RtspStream> stop;
        lock (_sync)
        {
            stop = _streams.Values.Where(s => s.CameraId == cameraId).ToList();
            foreach (var s in stop) _streams.Remove((s.CameraId, s.Quality));
        }
        foreach (var s in stop) StopStream(s);
    }

    /// <summary>Global options changed (decoder, transport...): reconnect all streams.</summary>
    public void UpdateOptions(StreamOptions options, Func<Guid, CameraConfig?> cameraLookup)
    {
        lock (_sync)
        {
            if (options == _options) return;
            _options = options;
            foreach (var s in _streams.Values)
            {
                var cam = cameraLookup(s.CameraId);
                if (cam != null)
                    s.Restart(RtspUrlBuilder.Build(cam, s.Quality), RtspUrlBuilder.BuildDisplay(cam, s.Quality), options);
            }
        }
    }

    private void Cleanup()
    {
        List<RtspStream> stop = new();
        lock (_sync)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            foreach (var s in _streams.Values)
                if (s.LeaseCount == 0 && s.UnusedSinceUtc is { } since && now - since >= ReleaseDelay)
                    stop.Add(s);
            foreach (var s in stop) _streams.Remove((s.CameraId, s.Quality));
        }
        foreach (var s in stop) StopStream(s);
    }

    private void StopStream(RtspStream stream)
    {
        stream.StatusChanged -= OnStreamStatusChanged;
        // Dispose on a pool thread: joining the worker must never block the UI.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { stream.Dispose(); }
            catch (Exception ex) { Log.Error($"Disposing stream {stream.Name} failed", ex); }
        });
    }

    private void OnStreamStatusChanged(RtspStream s) => StreamStatusChanged?.Invoke(s);

    public void Dispose()
    {
        List<RtspStream> all;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            all = _streams.Values.ToList();
            _streams.Clear();
        }
        _cleanupTimer.Dispose();
        foreach (var s in all) s.Stop();
        foreach (var s in all)
        {
            try { s.Dispose(); } catch (Exception ex) { Log.Error("Stream dispose failed", ex); }
        }
    }
}
