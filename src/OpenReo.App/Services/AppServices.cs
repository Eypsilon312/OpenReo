using System.Windows.Threading;
using OpenReo.Core.Cameras;
using OpenReo.Core.Configuration;
using OpenReo.Core.Layouts;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App.Services;

/// <summary>
/// Composition root: holds the long living services of the application.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly DispatcherTimer _saveTimer;

    public ConfigurationManager Config { get; }
    public AppConfiguration Configuration => Config.Current;
    public AppSettings Settings => Config.Current.Settings;
    public CameraManager Cameras { get; }
    public LayoutManager Layouts { get; }
    public StreamManager Streams { get; }
    public Audio.AudioController Audio { get; }

    public AppServices(ConfigurationManager config)
    {
        Config = config;
        Cameras = new CameraManager(config.Current.Cameras);
        Layouts = new LayoutManager(config.Current);
        Streams = new StreamManager(StreamOptions.From(Settings), TimeSpan.FromSeconds(Settings.StreamReleaseDelaySeconds));

        Audio = new Audio.AudioController(Streams);
        Audio.Player.Volume = (float)Settings.AudioVolume;
        Audio.Player.Muted = Settings.AudioMuted;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Config.Save(); };

        // Keep streams in sync with camera configuration changes.
        Cameras.CameraChanged += (cam, kind) =>
        {
            switch (kind)
            {
                case CameraChangeKind.ConnectionChanged: Streams.RestartCamera(cam); break;
                case CameraChangeKind.Removed: Streams.CloseCamera(cam.Id); break;
            }
            RequestSave();
        };
    }

    /// <summary>Saves the configuration shortly after the last change (debounced).</summary>
    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        Config.Save();
    }

    /// <summary>Applies changed settings to the running stream layer.</summary>
    public void ApplySettings()
    {
        Streams.ReleaseDelay = TimeSpan.FromSeconds(Settings.StreamReleaseDelaySeconds);
        Streams.UpdateOptions(StreamOptions.From(Settings), id => Cameras.Find(id));
        RequestSave();
    }

    public void Dispose()
    {
        SaveNow();
        Audio.Dispose();
        Streams.Dispose();
    }
}
