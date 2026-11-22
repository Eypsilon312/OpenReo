namespace OpenReo.Core.Models;

/// <summary>General application settings.</summary>
public sealed class AppSettings
{
    /// <summary>UI language: "auto" (Windows display language), "de" or "en".</summary>
    public string Language { get; set; } = "auto";

    public HardwareAccelerationMode HardwareAcceleration { get; set; } = HardwareAccelerationMode.Auto;
    public RtspTransport Transport { get; set; } = RtspTransport.Tcp;

    /// <summary>
    /// Maximum delay of the adaptive de-jitter buffer for live streams in ms (0 = off: show frames on
    /// arrival, lowest latency). The buffer only grows as far as the measured delivery jitter requires.
    /// </summary>
    public int LiveBufferMaxMs { get; set; } = 1500;

    /// <summary>Minimal buffering for lowest latency.</summary>
    public bool LowLatency { get; set; } = true;

    /// <summary>Show camera name / status overlay on tiles.</summary>
    public bool ShowTileOverlay { get; set; } = true;

    /// <summary>Show resolution / fps / decoder information on tiles.</summary>
    public bool ShowStreamInfo { get; set; } = true;

    /// <summary>
    /// Seconds an unused stream stays connected after it disappeared from the view.
    /// Prevents reconnects when switching layouts.
    /// </summary>
    public int StreamReleaseDelaySeconds { get; set; } = 15;

    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>When true the per camera quality is restored on start, otherwise all cameras start with Mid.</summary>
    public bool RestoreCameraQuality { get; set; } = true;

    /// <summary>Last quality chosen with the global quality switch (null = never used / mixed).</summary>
    public StreamQuality? GlobalQuality { get; set; }

    public bool StartInFullscreen { get; set; }

    /// <summary>Folder with the recordings of all cameras (e.g. \truenas\cameras) – one sub folder per camera.</summary>
    public string? RecordingsRoot { get; set; }

    /// <summary>Audio volume 0..1 and mute state (the audible camera itself is not restored – start silent).</summary>
    public double AudioVolume { get; set; } = 0.8;
    public bool AudioMuted { get; set; }

    /// <summary>Remember the last used camera credentials and pre-fill them for further cameras.</summary>
    public bool RememberCredentials { get; set; } = true;
    public string? DefaultUsername { get; set; }

    /// <summary>Remembered password – only in memory; stored encrypted in <see cref="ProtectedDefaultPassword"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DefaultPassword { get; set; }
    public string? ProtectedDefaultPassword { get; set; }

    /// <summary>Stores the credentials for the next camera (if enabled).</summary>
    public void RememberCredentialsFrom(CameraConfig cam)
    {
        if (!RememberCredentials) return;
        DefaultUsername = cam.Username;
        DefaultPassword = cam.Password;
    }

    public double SidebarWidth { get; set; } = 270;
    public bool SidebarVisible { get; set; } = true;

    public WindowPlacement? Window { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Root object of the persisted configuration file.</summary>
public sealed class AppConfiguration
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<CameraConfig> Cameras { get; set; } = new();
    public List<LayoutDefinition> CustomLayouts { get; set; } = new();
    public List<SavedView> SavedViews { get; set; } = new();
    public ViewState LastView { get; set; } = new();
}
