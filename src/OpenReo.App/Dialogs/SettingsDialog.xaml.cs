using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        HwBox.SelectedIndex = (int)settings.HardwareAcceleration;
        TransportBox.SelectedIndex = (int)settings.Transport;
        LowLatencyBox.IsChecked = settings.LowLatency;
        TimeoutBox.Text = settings.ConnectTimeoutSeconds.ToString();
        ReconnectBox.Text = settings.ReconnectMaxDelaySeconds.ToString();
        ReleaseBox.Text = settings.StreamReleaseDelaySeconds.ToString();
        OverlayBox.IsChecked = settings.ShowTileOverlay;
        InfoBox.IsChecked = settings.ShowStreamInfo;
        RestoreQualityBox.IsChecked = settings.RestoreCameraQuality;
        FullscreenBox.IsChecked = settings.StartInFullscreen;
        RememberCredentialsBox.IsChecked = settings.RememberCredentials;
        RememberedText.Text = settings.RememberCredentials && !string.IsNullOrEmpty(settings.DefaultUsername)
            ? $"Gemerkt: Benutzer „{settings.DefaultUsername}“ (wird beim Hinzufügen vorausgefüllt). Abwählen löscht die gemerkten Daten."
            : "Benutzer und Passwort der zuletzt hinzugefügten Kamera werden bei weiteren Kameras vorausgefüllt.";

        var available = HardwareAcceleration.Shared.ProbeAvailable();
        HwInfoText.Text = available.Count > 0
            ? $"Verfügbare GPU-Decoder: {string.Join(", ", available)}. „Automatisch“ verwendet CUDA/NVDEC (NVIDIA, inkl. GPU-Skalierung), sonst D3D11VA."
            : "Keine GPU-Decodierung verfügbar – es wird die CPU verwendet.";
        InfoText.Text = $"OpenReo {typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)} · FFmpeg {FFmpegLoader.Version}\n" +
                        $"Konfiguration: {AppPaths.ConfigFile}";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 3 or > 60) { ErrorText.Text = "Timeout: 3–60 s"; return; }
        if (!int.TryParse(ReconnectBox.Text, out var reconnect) || reconnect is < 2 or > 300) { ErrorText.Text = "Wiederholintervall: 2–300 s"; return; }
        if (!int.TryParse(ReleaseBox.Text, out var release) || release is < 0 or > 600) { ErrorText.Text = "Offen halten: 0–600 s"; return; }

        _settings.HardwareAcceleration = (HardwareAccelerationMode)Math.Max(0, HwBox.SelectedIndex);
        _settings.Transport = (RtspTransport)Math.Max(0, TransportBox.SelectedIndex);
        _settings.LowLatency = LowLatencyBox.IsChecked == true;
        _settings.ConnectTimeoutSeconds = timeout;
        _settings.ReconnectMaxDelaySeconds = reconnect;
        _settings.StreamReleaseDelaySeconds = release;
        _settings.ShowTileOverlay = OverlayBox.IsChecked == true;
        _settings.ShowStreamInfo = InfoBox.IsChecked == true;
        _settings.RestoreCameraQuality = RestoreQualityBox.IsChecked == true;
        _settings.StartInFullscreen = FullscreenBox.IsChecked == true;
        _settings.RememberCredentials = RememberCredentialsBox.IsChecked == true;
        if (!_settings.RememberCredentials) { _settings.DefaultUsername = null; _settings.DefaultPassword = null; }
        DialogResult = true;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Cannot open folder {path}: {ex.Message}"); }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDirectory);
    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogDirectory);
}
