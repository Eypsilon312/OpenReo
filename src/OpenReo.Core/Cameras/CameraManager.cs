using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Core.Cameras;

/// <summary>Kind of change for <see cref="CameraManager.CameraChanged"/>.</summary>
public enum CameraChangeKind
{
    Added,
    Removed,
    /// <summary>Only display related values changed (name, model...).</summary>
    Updated,
    /// <summary>Connection relevant values changed – streams must be restarted.</summary>
    ConnectionChanged,
    QualityChanged,
}

/// <summary>
/// Owns the camera list. All mutations go through this class so that the
/// stream layer and the UI get notified consistently.
/// Must be used from a single (UI) thread.
/// </summary>
public sealed class CameraManager
{
    private readonly List<CameraConfig> _cameras;

    public event Action<CameraConfig, CameraChangeKind>? CameraChanged;

    public CameraManager(List<CameraConfig> backingList)
    {
        _cameras = backingList;
    }

    public IReadOnlyList<CameraConfig> Cameras => _cameras;

    public CameraConfig? Find(Guid id) => _cameras.FirstOrDefault(c => c.Id == id);

    public void Add(CameraConfig camera)
    {
        if (_cameras.Any(c => c.Id == camera.Id)) camera.Id = Guid.NewGuid();
        _cameras.Add(camera);
        Log.Info($"Camera added: {camera.Name} ({camera.Host})");
        CameraChanged?.Invoke(camera, CameraChangeKind.Added);
    }

    /// <summary>Applies edited values to the existing camera with the same id.</summary>
    public void Update(CameraConfig edited)
    {
        var existing = Find(edited.Id) ?? throw new InvalidOperationException("Camera not found");
        bool connectionChanged = !existing.ConnectionEquals(edited);
        bool qualityChanged = existing.Quality != edited.Quality;
        existing.CopyFrom(edited);
        Log.Info($"Camera updated: {existing.Name} ({existing.Host}), connection changed: {connectionChanged}");
        if (connectionChanged) CameraChanged?.Invoke(existing, CameraChangeKind.ConnectionChanged);
        else if (qualityChanged) CameraChanged?.Invoke(existing, CameraChangeKind.QualityChanged);
        else CameraChanged?.Invoke(existing, CameraChangeKind.Updated);
    }

    public void Remove(Guid id)
    {
        var cam = Find(id);
        if (cam == null) return;
        _cameras.Remove(cam);
        Log.Info($"Camera removed: {cam.Name} ({cam.Host})");
        CameraChanged?.Invoke(cam, CameraChangeKind.Removed);
    }

    public void SetQuality(Guid id, StreamQuality quality)
    {
        var cam = Find(id);
        if (cam == null || cam.Quality == quality) return;
        cam.Quality = quality;
        CameraChanged?.Invoke(cam, CameraChangeKind.QualityChanged);
    }

    /// <summary>Global quality switch – overrides every individual setting.</summary>
    public void SetQualityForAll(StreamQuality quality)
    {
        foreach (var cam in _cameras.ToList())
            SetQuality(cam.Id, quality);
    }

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _cameras.Count || newIndex < 0 || newIndex >= _cameras.Count) return;
        var c = _cameras[oldIndex];
        _cameras.RemoveAt(oldIndex);
        _cameras.Insert(newIndex, c);
        CameraChanged?.Invoke(c, CameraChangeKind.Updated);
    }
}
