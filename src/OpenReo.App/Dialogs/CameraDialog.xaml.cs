using System.Text;
using System.Windows;
using System.Windows.Controls;
using OpenReo.Core.Cameras;
using OpenReo.Core.Models;
using OpenReo.Video;
using OpenReo.Core.Localization;

namespace OpenReo.App.Dialogs;

/// <summary>Add / edit a camera, including automatic stream detection and a stream test.</summary>
public partial class CameraDialog : Window
{
    private readonly RtspTransport _transport;
    private readonly IReadOnlyList<CameraConfig> _existing;
    private readonly bool _isNew;
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>The edited (or new) camera – only valid when DialogResult is true.</summary>
    public CameraConfig Result { get; private set; }

    public CameraDialog(CameraConfig? camera, RtspTransport transport, IReadOnlyList<CameraConfig> existing, bool runDetectionOnOpen = false)
    {
        InitializeComponent();
        _transport = transport;
        _existing = existing;
        _isNew = camera == null || !existing.Any(c => c.Id == camera.Id);
        Result = camera?.Clone() ?? new CameraConfig();
        Title = _isNew ? Lang.T("Kamera hinzufügen") : Lang.F("Kamera bearbeiten – {0}", Result.Name);

        NameBox.Text = _isNew && Result.Name == "Kamera" ? "" : Result.Name;
        HostBox.Text = Result.Host;
        RtspPortBox.Text = Result.RtspPort.ToString();
        OnvifPortBox.Text = Result.OnvifPort.ToString();
        HttpPortBox.Text = Result.HttpPort.ToString();
        UserBox.Text = Result.Username;
        PasswordBox.Password = Result.Password;
        ChannelBox.Text = Result.Channel.ToString();
        QualityBox.SelectedIndex = (int)Result.Quality;
        ClearPathBox.Text = Result.ClearPath;
        MidPathBox.Text = Result.MidPath;
        LowPathBox.Text = Result.LowPath;
        UpdateDeviceInfo();

        Loaded += (_, _) =>
        {
            (string.IsNullOrEmpty(HostBox.Text) ? HostBox : string.IsNullOrEmpty(PasswordBox.Password) ? (Control)PasswordBox : NameBox).Focus();
            if (runDetectionOnOpen) OnDetectClick(this, new RoutedEventArgs());
        };
        Closing += (_, e) =>
        {
            if (_busy) { _cts?.Cancel(); }
        };
    }

    private void UpdateDeviceInfo()
    {
        var parts = new[] { Result.Manufacturer, Result.Model, Result.FirmwareVersion != null ? "Firmware " + Result.FirmwareVersion : null }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var text = string.Join(" · ", parts);
        DeviceInfoText.Text = text;
        DeviceInfoText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Reads the form into <see cref="Result"/>. Returns an error message or null.</summary>
    private string? ReadForm()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) return Lang.T("Bitte IP-Adresse oder Hostnamen eingeben.");
        if (host.Contains("://")) return Lang.T("Bitte nur die IP-Adresse bzw. den Hostnamen eingeben (ohne rtsp:// oder http://).");
        if (!int.TryParse(RtspPortBox.Text, out var rtsp) || rtsp is < 1 or > 65535) return Lang.T("Ungültiger RTSP-Port.");
        if (!int.TryParse(OnvifPortBox.Text, out var onvif) || onvif is < 1 or > 65535) return Lang.T("Ungültiger ONVIF-Port.");
        if (!int.TryParse(HttpPortBox.Text, out var http) || http is < 1 or > 65535) return Lang.T("Ungültiger HTTP-Port.");
        if (!int.TryParse(ChannelBox.Text, out var ch) || ch is < 1 or > 128) return Lang.T("Ungültige Kanalnummer.");
        Result.HttpPort = http;

        Result.Host = host;
        Result.RtspPort = rtsp;
        Result.OnvifPort = onvif;
        Result.Channel = ch;
        Result.Username = UserBox.Text.Trim();
        Result.Password = PasswordBox.Password;
        Result.Quality = (StreamQuality)Math.Max(0, QualityBox.SelectedIndex);
        Result.ClearPath = ClearPathBox.Text.Trim();
        Result.MidPath = MidPathBox.Text.Trim();
        Result.LowPath = LowPathBox.Text.Trim();
        var name = NameBox.Text.Trim();
        Result.Name = name.Length > 0 ? name : (Result.Model ?? host);
        return null;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        DetectButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = !busy;
        BusyText.Text = text ?? "";
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultPanel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs ONVIF / Reolink path detection and fills the empty (or all, if forced) path boxes.</summary>
    private async Task<bool> DetectAsync(bool overwrite)
    {
        var err = ReadForm();
        if (err != null) { ShowResult(err); return false; }

        _cts = new CancellationTokenSource();
        SetBusy(true, Lang.T("Ermittle Streams …"));
        try
        {
            var progress = new Progress<string>(s => BusyText.Text = s);
            var r = await CameraStreamDetector.DetectAsync(Result.Clone(), _transport, progress, _cts.Token);

            if (r.DeviceInfo != null)
            {
                Result.Manufacturer = r.DeviceInfo.Manufacturer;
                Result.Model = r.DeviceInfo.Model;
                Result.FirmwareVersion = r.DeviceInfo.FirmwareVersion;
                Result.SerialNumber = r.DeviceInfo.SerialNumber;
                UpdateDeviceInfo();
            }
            // Prefer the name configured in the camera over placeholders such as "IPC_..." or the model.
            if (IsGenericName(NameBox.Text))
            {
                if (!string.IsNullOrWhiteSpace(r.DeviceName)) NameBox.Text = r.DeviceName;
                else if (string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(r.DeviceInfo?.Model)) NameBox.Text = r.DeviceInfo!.Model;
            }
            if (r.RtspPort is int port && overwrite) RtspPortBox.Text = port.ToString();

            void Fill(TextBox box, StreamQuality q)
            {
                if (r.Paths.TryGetValue(q, out var p) && (overwrite || string.IsNullOrWhiteSpace(box.Text))) box.Text = p;
            }
            Fill(ClearPathBox, StreamQuality.Clear);
            Fill(MidPathBox, StreamQuality.Mid);
            Fill(LowPathBox, StreamQuality.Low);

            var sb = new StringBuilder();
            sb.AppendLine(r.OnvifSucceeded ? "ONVIF: Profile gelesen." : Lang.T("ONVIF: nicht verfügbar – Reolink-Standardpfade geprüft."));
            foreach (var q in new[] { StreamQuality.Clear, StreamQuality.Mid, StreamQuality.Low })
            {
                var path = r.Paths.TryGetValue(q, out var p) ? p : Lang.T("(nicht gefunden)");
                var probe = r.Probes.TryGetValue(q, out var pr) ? "  " + pr : "";
                sb.AppendLine($"{q.DisplayName(),-5}: {path}{probe}");
            }
            foreach (var m in r.Messages.Distinct()) sb.AppendLine("• " + m);
            ShowResult(sb.ToString().TrimEnd());
            return r.Paths.Count > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ShowResult(Lang.T("Fehler bei der Erkennung: ") + ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e) => await DetectAsync(overwrite: true);

    private bool IsGenericName(string name)
    {
        var probe = Result.Clone();
        probe.Name = name.Trim();
        return probe.HasGenericName;
    }

    /// <summary>Reads the name configured in the camera via the Reolink HTTP API.</summary>
    private async void OnFetchNameClick(object sender, RoutedEventArgs e)
    {
        var err = ReadForm();
        if (err != null) { ShowResult(err); return; }
        _cts = new CancellationTokenSource();
        SetBusy(true, Lang.T("Lese Kameranamen …"));
        FetchNameButton.IsEnabled = false;
        try
        {
            var name = await Core.Reolink.ReolinkApiClient.GetDeviceNameAsync(Result.Host, Result.HttpPort, Result.Username, Result.Password, Result.Channel, _cts.Token);
            if (!string.IsNullOrWhiteSpace(name)) NameBox.Text = name;
            else ShowResult(Lang.T("Der Kameraname konnte nicht gelesen werden (Reolink-HTTP-API nicht erreichbar, HTTP-Port oder Zugangsdaten prüfen)."));
        }
        catch (OperationCanceledException) { }
        finally
        {
            FetchNameButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var err = ReadForm();
        if (err != null) { ShowResult(err); return; }
        _cts = new CancellationTokenSource();
        SetBusy(true, Lang.T("Teste Streams …"));
        var sb = new StringBuilder();
        try
        {
            foreach (var q in new[] { StreamQuality.Clear, StreamQuality.Mid, StreamQuality.Low })
            {
                var path = Result.GetPath(q);
                if (string.IsNullOrWhiteSpace(path)) { sb.AppendLine($"{q.DisplayName(),-5}: " + Lang.T("(kein Pfad – wird automatisch ermittelt)")); continue; }
                BusyText.Text = Lang.F("Teste {0} …", q.DisplayName());
                var r = await StreamProbe.ProbeAsync(RtspUrlBuilder.Build(Result, q), _transport, TimeSpan.FromSeconds(8), _cts.Token);
                sb.AppendLine($"{q.DisplayName(),-5}: {r}");
                ShowResult(sb.ToString().TrimEnd());
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            SetBusy(false);
        }
        ShowResult(sb.ToString().TrimEnd());
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var err = ReadForm();
        if (err != null) { ShowResult(err); return; }

        if (_existing.Any(c => c.Id != Result.Id && string.Equals(c.Host, Result.Host, StringComparison.OrdinalIgnoreCase) && c.Channel == Result.Channel))
        {
            if (MessageBox.Show(this, Lang.T("Eine Kamera mit dieser Adresse und diesem Kanal existiert bereits. Trotzdem speichern?"), "OpenReo",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }

        // Empty stream paths: detect automatically.
        if (string.IsNullOrWhiteSpace(Result.ClearPath) || string.IsNullOrWhiteSpace(Result.MidPath) || string.IsNullOrWhiteSpace(Result.LowPath))
        {
            await DetectAsync(overwrite: false);
            if (!IsLoaded) return;
            ReadForm();
            if (string.IsNullOrWhiteSpace(Result.ClearPath) || string.IsNullOrWhiteSpace(Result.LowPath))
            {
                var answer = MessageBox.Show(this,
                    Lang.T("Die Streams konnten nicht automatisch ermittelt werden (Kamera nicht erreichbar oder Zugangsdaten falsch).\n\n") +
                    Lang.T("Mit den Reolink-Standardpfaden speichern? Die Verbindung wird automatisch wiederholt, sobald die Kamera erreichbar ist."),
                    "OpenReo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                if (string.IsNullOrWhiteSpace(Result.ClearPath)) Result.ClearPath = RtspUrlBuilder.ReolinkDefault(StreamQuality.Clear, Result.Channel);
                if (string.IsNullOrWhiteSpace(Result.LowPath)) Result.LowPath = RtspUrlBuilder.ReolinkDefault(StreamQuality.Low, Result.Channel);
            }
            if (string.IsNullOrWhiteSpace(Result.MidPath)) Result.MidPath = Result.LowPath;
        }

        DialogResult = true;
    }
}
