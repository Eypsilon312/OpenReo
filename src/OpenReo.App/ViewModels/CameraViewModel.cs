using System.Windows;
using System.Windows.Media;
using OpenReo.App.Infrastructure;
using OpenReo.Core.Models;
using OpenReo.Video;
using OpenReo.Core.Localization;

namespace OpenReo.App.ViewModels;

public enum CameraUiState
{
    Inactive,
    Connecting,
    Online,
    Offline,
}

/// <summary>Sidebar / tile representation of a camera.</summary>
public sealed class CameraViewModel : ObservableObject
{
    private CameraUiState _state = CameraUiState.Inactive;
    private string _statusText = Lang.T("Nicht in Ansicht");
    private bool _isInView;

    public CameraConfig Config { get; }

    public CameraViewModel(CameraConfig config) { Config = config; }

    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string Host => Config.Host;
    public StreamQuality Quality => Config.Quality;
    public string QualityText => Config.Quality.DisplayName();
    public string ModelText => string.IsNullOrWhiteSpace(Config.Model) ? Config.Host : $"{Config.Host} · {Config.Model}";

    public CameraUiState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(StatusBrush)); }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsInView
    {
        get => _isInView;
        set => Set(ref _isInView, value);
    }

    public Brush StatusBrush => (Brush)Application.Current.Resources[State switch
    {
        CameraUiState.Online => "OkBrush",
        CameraUiState.Connecting => "WarnBrush",
        CameraUiState.Offline => "ErrorBrush",
        _ => "IdleBrush",
    }];

    /// <summary>Updates the state from the stream of the currently selected quality.</summary>
    public void UpdateStatus(RtspStream? stream)
    {
        if (stream == null)
        {
            State = CameraUiState.Inactive;
            StatusText = Lang.T("Nicht in Ansicht");
            return;
        }
        switch (stream.Status)
        {
            case StreamStatus.Playing:
                State = CameraUiState.Online;
                var st = stream.Statistics;
                StatusText = st.Width > 0 ? Lang.F("Online · {0} {1}×{2}", QualityText, st.Width, st.Height) : "Online";
                break;
            case StreamStatus.Connecting:
            case StreamStatus.Idle:
                State = CameraUiState.Connecting;
                StatusText = Lang.T("Verbinde …");
                break;
            case StreamStatus.Reconnecting:
                State = CameraUiState.Offline;
                StatusText = Lang.T("Offline – ") + (stream.LastError ?? Lang.T("Verbindung verloren"));
                break;
            default:
                State = CameraUiState.Inactive;
                StatusText = Lang.T("Gestoppt");
                break;
        }
    }

    /// <summary>Call after the configuration was edited.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(ModelText));
    }
}
