using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenReo.App.Infrastructure;
using OpenReo.Core.Cameras;
using OpenReo.Core.Models;
using OpenReo.Core.Onvif;
using OpenReo.Video;

namespace OpenReo.App.Dialogs;

public sealed class DiscoveryItem : ObservableObject
{
    private bool _isSelected;
    private string _status = "";
    private bool _canAdd = true;
    private Brush _statusBrush = Brushes.Gray;

    public DiscoveredDevice Device { get; }
    public DiscoveryItem(DiscoveredDevice d) { Device = d; }

    public string Address => Device.Address;
    public string Name => Device.Name ?? "";
    public string Hardware => Device.Hardware ?? "";
    public int Port => Device.OnvifPort;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool CanAdd { get => _canAdd; set => Set(ref _canAdd, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public void SetStatus(string text, string brushKey)
    {
        Status = text;
        StatusBrush = (Brush)Application.Current.Resources[brushKey];
    }
}

/// <summary>ONVIF WS-Discovery search with bulk adding of the found cameras.</summary>
public partial class DiscoveryDialog : Window
{
    private readonly CameraManager _cameras;
    private readonly RtspTransport _transport;
    private readonly ObservableCollection<DiscoveryItem> _items = new();
    private CancellationTokenSource _cts = new();
    private bool _busy;

    private readonly AppSettings _settings;

    public DiscoveryDialog(CameraManager cameras, AppSettings settings)
    {
        InitializeComponent();
        _cameras = cameras;
        _settings = settings;
        _transport = settings.Transport;
        if (settings.RememberCredentials && settings.DefaultUsername != null)
        {
            UserBox.Text = settings.DefaultUsername;
            PasswordBox.Password = settings.DefaultPassword ?? "";
        }
        DeviceList.ItemsSource = _items;
        Loaded += (_, _) => OnSearchClick(this, new RoutedEventArgs());
        Closing += (_, _) => _cts.Cancel();
    }

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        SearchButton.IsEnabled = AddButton.IsEnabled = !busy;
        BusyText.Text = text;
        Cursor = busy ? Cursors.AppStarting : null;
    }

    private bool IsAlreadyAdded(DiscoveredDevice d) =>
        _cameras.Cameras.Any(c => string.Equals(c.Host, d.Address, StringComparison.OrdinalIgnoreCase));

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Suche läuft …");
        _items.Clear();
        EmptyText.Text = "Suche nach ONVIF-Geräten …";
        EmptyText.Visibility = Visibility.Visible;
        try
        {
            var found = await OnvifDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3.5), _cts.Token);
            foreach (var d in found)
            {
                var item = new DiscoveryItem(d);
                if (IsAlreadyAdded(d))
                {
                    item.CanAdd = false;
                    item.SetStatus("Bereits hinzugefügt", "TextDimBrush");
                }
                else
                {
                    item.IsSelected = true;
                    item.SetStatus(d.IsLikelyReolink ? "Reolink-Gerät" : "ONVIF-Gerät", "TextDimBrush");
                }
                _items.Add(item);
            }
            EmptyText.Text = "Keine Geräte gefunden.\n\nPrüfen Sie, ob ONVIF in der Kamera aktiviert ist und sich Kamera und PC im selben Netzwerk befinden. " +
                             "Kameras können auch manuell über ihre IP-Adresse hinzugefügt werden.";
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetBusy(false, $"{_items.Count} Gerät(e) gefunden.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EmptyText.Text = "Suche fehlgeschlagen: " + ex.Message;
            SetBusy(false);
        }
    }

    private CameraConfig CreateConfig(DiscoveredDevice d) => new()
    {
        Name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name! : d.Hardware ?? d.Address,
        Host = d.Address,
        OnvifPort = d.OnvifPort,
        Username = UserBox.Text.Trim(),
        Password = PasswordBox.Password,
        Model = d.Hardware,
    };

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected && i.CanAdd).ToList();
        if (selected.Count == 0)
        {
            BusyText.Text = "Keine Geräte ausgewählt.";
            return;
        }
        SetBusy(true);
        int added = 0;
        try
        {
            foreach (var item in selected)
            {
                var cfg = CreateConfig(item.Device);
                item.SetStatus("Ermittle Streams …", "WarnBrush");
                var progress = new Progress<string>(s => BusyText.Text = $"{item.Address}: {s}");
                var r = await CameraStreamDetector.DetectAsync(cfg, _transport, progress, _cts.Token);
                if (!r.Paths.ContainsKey(StreamQuality.Clear))
                {
                    item.SetStatus(r.AuthFailed ? "Anmeldung fehlgeschlagen" : "Keine Streams gefunden", "ErrorBrush");
                    continue;
                }
                foreach (var (q, path) in r.Paths) cfg.SetPath(q, path);
                if (string.IsNullOrEmpty(cfg.MidPath)) cfg.MidPath = cfg.LowPath;
                if (string.IsNullOrEmpty(cfg.LowPath)) cfg.LowPath = cfg.ClearPath;
                if (string.IsNullOrEmpty(cfg.MidPath)) cfg.MidPath = cfg.ClearPath;
                if (r.RtspPort is int port) cfg.RtspPort = port;
                if (!string.IsNullOrWhiteSpace(r.DeviceName)) cfg.Name = r.DeviceName!;
                if (r.DeviceInfo != null)
                {
                    cfg.Manufacturer = r.DeviceInfo.Manufacturer;
                    cfg.Model = r.DeviceInfo.Model ?? cfg.Model;
                    cfg.FirmwareVersion = r.DeviceInfo.FirmwareVersion;
                    cfg.SerialNumber = r.DeviceInfo.SerialNumber;
                }
                _settings.RememberCredentialsFrom(cfg);
                _cameras.Add(cfg);
                added++;
                item.CanAdd = false;
                item.IsSelected = false;
                item.SetStatus($"Hinzugefügt ✓ ({cfg.Name})", "OkBrush");
            }
        }
        catch (OperationCanceledException) { return; }
        SetBusy(false, $"{added} Kamera(s) hinzugefügt.");
    }

    private void OnDeviceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DiscoveryItem item || !item.CanAdd || _busy) return;
        var dlg = new CameraDialog(CreateConfig(item.Device), _transport, _cameras.Cameras, runDetectionOnOpen: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings.RememberCredentialsFrom(dlg.Result);
            _cameras.Add(dlg.Result);
            item.CanAdd = false;
            item.IsSelected = false;
            item.SetStatus("Hinzugefügt ✓", "OkBrush");
        }
    }
}
