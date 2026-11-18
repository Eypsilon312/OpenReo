using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App.Audio;

/// <summary>
/// Decides which camera is audible. Only one camera plays at a time; its audio follows the
/// stream of the currently selected quality (e.g. after switching Clear → Mid).
/// UI thread only.
/// </summary>
public sealed class AudioController : IDisposable
{
    private readonly StreamManager _streams;
    private RtspStream? _attached;

    public AudioPlayer Player { get; } = new();

    /// <summary>Camera whose sound is played (null = silent).</summary>
    public Guid? CameraId { get; private set; }

    public event Action? Changed;

    public AudioController(StreamManager streams)
    {
        _streams = streams;
    }

    /// <summary>Switches the sound to the camera, or off if it is already playing.</summary>
    public void Toggle(Guid cameraId) => SetCamera(CameraId == cameraId ? null : cameraId);

    public void SetCamera(Guid? cameraId)
    {
        CameraId = cameraId;
        Changed?.Invoke();
    }

    /// <summary>Attaches the player to the right stream (call periodically and after quality changes).</summary>
    public void Update(Func<Guid, CameraConfig?> findCamera)
    {
        RtspStream? target = null;
        if (CameraId is Guid id && findCamera(id) is { } cam)
            target = _streams.TryGet(id, cam.Quality);
        else if (CameraId != null)
            CameraId = null; // camera was removed

        if (target == _attached) return;
        if (_attached != null) _attached.AudioSink = null;
        _attached = target;
        if (target != null)
        {
            target.AudioSink = Player;
            Player.Start();
            Log.Info($"Audio: {target.Name} [{target.Quality}]");
        }
        else
        {
            Player.Stop();
        }
    }

    /// <summary>Audio codec of the audible stream, or null.</summary>
    public string? CurrentCodec => _attached?.AudioCodec;

    public void Dispose()
    {
        if (_attached != null) _attached.AudioSink = null;
        Player.Dispose();
    }
}
