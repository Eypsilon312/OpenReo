using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenReo.App.Controls;
using OpenReo.App.Dialogs;
using OpenReo.App.Services;
using OpenReo.App.ViewModels;
using OpenReo.Core.Cameras;
using OpenReo.Core.Layouts;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App;

public partial class MainWindow : Window, IViewerActions
{
    private readonly AppServices _svc;
    private readonly ObservableCollection<CameraViewModel> _cameras = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Dictionary<string, ToggleButton> _layoutButtons = new();

    private bool _fullscreen;
    private WindowState _preFullscreenState;
    private WindowStyle _preFullscreenStyle;
    private ResizeMode _preFullscreenResize;

    private Point _listDragStart;
    private CameraViewModel? _listDragItem;

    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private int _tick;

    public MainWindow(AppServices services)
    {
        _svc = services;
        InitializeComponent();

        foreach (var cam in _svc.Cameras.Cameras) _cameras.Add(new CameraViewModel(cam));
        CameraList.ItemsSource = _cameras;
        _svc.Cameras.CameraChanged += OnCameraChanged;

        MultiView.Initialize(_svc.Streams, _svc.Settings, this, () => _cameras);
        MultiView.ViewChanged += OnViewChanged;

        VolumeSlider.Value = _svc.Settings.AudioVolume * 100;
        UpdateMuteIcon();
        _svc.Audio.Changed += () => { _svc.Audio.Update(id => _svc.Cameras.Find(id)); MultiView.RefreshOverlays(); };

        BuildLayoutButtons();
        RestoreWindowPlacement();
        RestoreView();
        UpdateCameraPanel();
        UpdateGlobalQualityButtons();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTimer.Tick += OnUiTimer;
        _uiTimer.Start();

        // Apply before the window is first painted to avoid a light title bar flash.
        SourceInitialized += (_, _) => Infrastructure.DarkTitleBar.Apply(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Loaded += (_, _) => { if (_svc.Settings.StartInFullscreen) SetFullscreen(true); };
    }

    // =====================================================================
    // View / layout
    // =====================================================================

    private void RestoreView()
    {
        var last = _svc.Configuration.LastView;
        var layout = _svc.Layouts.Find(last.LayoutId) ?? last.Layout ?? _svc.Layouts.Default;
        MultiView.SetView(layout, last.Slots);
        if (last.Slots.All(s => s == null) && _cameras.Count > 0)
            MultiView.FillWithCameras(_cameras.Select(c => c.Id)); // first start: show all cameras
        UpdateLayoutButtons();
    }

    private void OnViewChanged()
    {
        var last = _svc.Configuration.LastView;
        last.LayoutId = MultiView.Layout.Id;
        last.Layout = MultiView.Layout.Clone();
        last.Slots = MultiView.Slots;
        UpdateLayoutButtons();
        _svc.RequestSave();
    }

    private void BuildLayoutButtons()
    {
        LayoutButtons.Children.Clear();
        _layoutButtons.Clear();
        foreach (var id in LayoutManager.PrimaryLayoutIds)
        {
            var layout = _svc.Layouts.Find(id)!;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(LayoutIcon.Create(layout));
            content.Children.Add(new TextBlock { Text = layout.TileCount.ToString(), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("SegmentToggle"),
                Content = content,
                ToolTip = layout.Name,
                Margin = new Thickness(1, 0, 1, 0),
            };
            btn.Click += (_, _) => { MultiView.SetLayout(layout); };
            _layoutButtons[id] = btn;
            LayoutButtons.Children.Add(btn);
        }
    }

    private void UpdateLayoutButtons()
    {
        foreach (var (id, btn) in _layoutButtons) btn.IsChecked = MultiView.Layout.Id == id;
    }

    private void OnMoreLayoutsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreLayoutsButton, Placement = PlacementMode.Bottom };
        foreach (var l in _svc.Layouts.BuiltIn)
            menu.Items.Add(LayoutMenuItem(l));
        if (_svc.Layouts.Custom.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var l in _svc.Layouts.Custom) menu.Items.Add(LayoutMenuItem(l));
        }
        menu.Items.Add(new Separator());
        var editor = new MenuItem { Header = "Eigene Layouts bearbeiten …" };
        editor.Click += (_, _) => OpenLayoutEditor();
        menu.Items.Add(editor);
        menu.IsOpen = true;
    }

    private MenuItem LayoutMenuItem(LayoutDefinition l)
    {
        var mi = new MenuItem { Header = l.Name, IsChecked = MultiView.Layout.Id == l.Id, Icon = LayoutIcon.Create(l, 16, 12) };
        mi.Click += (_, _) => MultiView.SetLayout(l);
        return mi;
    }

    private void OpenLayoutEditor()
    {
        var dlg = new LayoutEditorDialog(_svc.Layouts) { Owner = this };
        dlg.ShowDialog();
        _svc.RequestSave();
        if (dlg.LayoutToApply != null) MultiView.SetLayout(dlg.LayoutToApply);
        else if (_svc.Layouts.Find(MultiView.Layout.Id) is { } current && !current.IsBuiltIn) MultiView.SetLayout(current); // edited
    }

    private void OnViewsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewsButton, Placement = PlacementMode.Bottom };
        foreach (var v in _svc.Layouts.SavedViews)
        {
            var view = v;
            var mi = new MenuItem { Header = view.Name, Icon = LayoutIcon.Create(view.Layout, 16, 12) };
            mi.Click += (_, _) => LoadView(view);
            menu.Items.Add(mi);
        }
        if (_svc.Layouts.SavedViews.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(keine gespeicherten Ansichten)", IsEnabled = false });
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Aktuelle Ansicht speichern …" };
        save.Click += (_, _) => SaveCurrentView();
        menu.Items.Add(save);
        if (_svc.Layouts.SavedViews.Count > 0)
        {
            var del = new MenuItem { Header = "Ansicht löschen" };
            foreach (var v in _svc.Layouts.SavedViews.ToList())
            {
                var view = v;
                var mi = new MenuItem { Header = view.Name };
                mi.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"Ansicht „{view.Name}“ löschen?", "OpenReo", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        _svc.Layouts.DeleteView(view.Id);
                        _svc.RequestSave();
                    }
                };
                del.Items.Add(mi);
            }
            menu.Items.Add(del);
        }
        menu.IsOpen = true;
    }

    private void SaveCurrentView()
    {
        var name = InputDialog.Ask(this, "Ansicht speichern", "Name der Ansicht:", $"Ansicht {_svc.Layouts.SavedViews.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _svc.Layouts.SaveView(name.Trim(), MultiView.Layout, MultiView.Slots);
        _svc.RequestSave();
    }

    private void LoadView(SavedView view)
    {
        var layout = _svc.Layouts.Find(view.Layout.Id) ?? view.Layout.Clone();
        MultiView.SetView(layout, view.Slots.ToList());
        OnViewChanged();
    }

    // =====================================================================
    // Cameras
    // =====================================================================

    private CameraViewModel? FindVm(Guid id) => _cameras.FirstOrDefault(c => c.Id == id);

    private void OnCameraChanged(CameraConfig cam, CameraChangeKind kind)
    {
        switch (kind)
        {
            case CameraChangeKind.Added:
                _cameras.Add(new CameraViewModel(cam));
                MultiView.FillWithCameras(new[] { cam.Id }); // only fills an empty tile, never replaces one
                break;
            case CameraChangeKind.Removed:
                if (FindVm(cam.Id) is { } vm) _cameras.Remove(vm);
                MultiView.RemoveCamera(cam.Id);
                break;
            case CameraChangeKind.QualityChanged:
                FindVm(cam.Id)?.Refresh();
                MultiView.RefreshCamera(cam.Id);
                _svc.Audio.Update(id => _svc.Cameras.Find(id));
                UpdateGlobalQualityButtons();
                break;
            default:
                FindVm(cam.Id)?.Refresh();
                MultiView.RefreshOverlays();
                break;
        }
        UpdateCameraPanel();
    }

    private void UpdateCameraPanel()
    {
        CameraCountText.Text = _cameras.Count == 1 ? "1 Kamera" : $"{_cameras.Count} Kameras";
        NoCamerasPanel.Visibility = _cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraList.Visibility = _cameras.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetCameraQuality(CameraViewModel camera, StreamQuality quality)
    {
        _svc.Cameras.SetQuality(camera.Id, quality);
        _svc.Settings.GlobalQuality = null;
        _svc.RequestSave();
    }

    private void OnGlobalQualityClick(object sender, RoutedEventArgs e)
    {
        var q = Enum.Parse<StreamQuality>((string)((FrameworkElement)sender).Tag);
        _svc.Cameras.SetQualityForAll(q); // overrides all individual settings
        _svc.Settings.GlobalQuality = q;
        _svc.RequestSave();
        UpdateGlobalQualityButtons();
    }

    private void UpdateGlobalQualityButtons()
    {
        StreamQuality? common = _cameras.Count > 0 && _cameras.All(c => c.Quality == _cameras[0].Quality) ? _cameras[0].Quality : null;
        GlobalClear.IsChecked = common == StreamQuality.Clear;
        GlobalMid.IsChecked = common == StreamQuality.Mid;
        GlobalLow.IsChecked = common == StreamQuality.Low;
    }

    private void OnAddCameraClick(object sender, RoutedEventArgs e)
    {
        // Pre-fill the remembered credentials of the last camera.
        var template = new CameraConfig { Name = "" };
        if (_svc.Settings.RememberCredentials && _svc.Settings.DefaultUsername != null)
        {
            template.Username = _svc.Settings.DefaultUsername;
            template.Password = _svc.Settings.DefaultPassword ?? "";
        }
        var dlg = new CameraDialog(template, _svc.Settings.Transport, _svc.Cameras.Cameras) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _svc.Settings.RememberCredentialsFrom(dlg.Result);
            _svc.Cameras.Add(dlg.Result);
        }
    }

    public void EditCamera(CameraViewModel camera)
    {
        var dlg = new CameraDialog(camera.Config, _svc.Settings.Transport, _svc.Cameras.Cameras) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _svc.Settings.RememberCredentialsFrom(dlg.Result);
            _svc.Cameras.Update(dlg.Result);
        }
    }

    // ---------------------------------------------------------------- audio

    public Guid? AudioCameraId => _svc.Audio.CameraId;

    public void ToggleAudio(CameraViewModel camera)
    {
        if (!_svc.Audio.Player.IsAvailable)
        {
            MessageBox.Show(this, "Es wurde kein Audio-Ausgabegerät gefunden.", "Ton", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _svc.Audio.Toggle(camera.Id);
        if (_svc.Audio.CameraId != null && _svc.Audio.Player.Muted)
        {
            _svc.Audio.Player.Muted = false; // choosing a camera to listen to un-mutes
            _svc.Settings.AudioMuted = false;
            UpdateMuteIcon();
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_svc == null) return;
        _svc.Audio.Player.Volume = (float)(VolumeSlider.Value / 100.0);
        _svc.Settings.AudioVolume = VolumeSlider.Value / 100.0;
        _svc.RequestSave();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _svc.Audio.Player.Muted = !_svc.Audio.Player.Muted;
        _svc.Settings.AudioMuted = _svc.Audio.Player.Muted;
        _svc.RequestSave();
        UpdateMuteIcon();
    }

    private void UpdateMuteIcon()
    {
        MuteIcon.Text = _svc.Audio.Player.Muted ? "\uE74F" : "\uE767";
        MuteButton.ToolTip = _svc.Audio.Player.Muted ? "Ton einschalten" : "Ton stumm schalten";
    }

    private readonly Dictionary<Guid, CameraSettings.CameraSettingsWindow> _settingsWindows = new();

    /// <summary>Opens the camera settings (Reolink API) – non modal, so the live image stays visible.</summary>
    public void OpenCameraSettings(CameraViewModel camera)
    {
        if (_settingsWindows.TryGetValue(camera.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var win = new CameraSettings.CameraSettingsWindow(camera.Config, name =>
        {
            var edited = camera.Config.Clone();
            edited.Name = name;
            _svc.Cameras.Update(edited);
        }) { Owner = this };
        _settingsWindows[camera.Id] = win;
        win.Closed += (_, _) => _settingsWindows.Remove(camera.Id);
        win.Show();
    }

    private static void OpenWebUi(CameraViewModel camera)
    {
        var c = camera.Config;
        var host = c.Host.Contains(':') && !c.Host.StartsWith('[') ? $"[{c.Host}]" : c.Host;
        var url = c.HttpPort == 443 ? $"https://{host}" : c.HttpPort == 80 ? $"http://{host}" : $"http://{host}:{c.HttpPort}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Core.Diagnostics.Log.Warn($"Cannot open {url}: {ex.Message}"); }
    }

    /// <summary>Replaces the camera names with the names configured in the cameras (Reolink HTTP API).</summary>
    private async void FetchNamesFromCameras(IReadOnlyList<CameraViewModel> cameras)
    {
        int ok = 0, failed = 0;
        foreach (var vm in cameras)
        {
            StatusLeft.Text = $"Lese Namen von {vm.Host} …";
            try
            {
                var c = vm.Config;
                var name = await Core.Reolink.ReolinkApiClient.GetDeviceNameAsync(c.Host, c.HttpPort, c.Username, c.Password, c.Channel, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(name)) { failed++; continue; }
                if (name != c.Name)
                {
                    var edited = c.Clone();
                    edited.Name = name;
                    _svc.Cameras.Update(edited);
                }
                ok++;
            }
            catch { failed++; }
        }
        StatusLeft.Text = $"Kameranamen übernommen: {ok}" + (failed > 0 ? $", nicht lesbar: {failed} (HTTP-Port / Zugangsdaten prüfen)" : "");
    }

    private void RemoveCamera(CameraViewModel camera)
    {
        if (MessageBox.Show(this, $"Kamera „{camera.Name}“ wirklich entfernen?", "Kamera entfernen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _svc.Cameras.Remove(camera.Id);
    }

    private void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        var dlg = new DiscoveryDialog(_svc.Cameras, _svc.Settings) { Owner = this };
        dlg.ShowDialog();
        _svc.RequestSave();
    }

    // Sidebar list: drag & drop, double click, context menu ----------------------

    private void OnCameraListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDragStart = e.GetPosition(CameraList);
        _listDragItem = (e.OriginalSource as FrameworkElement)?.DataContext as CameraViewModel;
    }

    private void OnCameraListMouseMove(object sender, MouseEventArgs e)
    {
        if (_listDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(CameraList);
        if (Math.Abs(p.X - _listDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _listDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _listDragItem;
        _listDragItem = null;
        DragDrop.DoDragDrop(CameraList, new DataObject(VideoTile.CameraDataFormat, item.Id.ToString()), DragDropEffects.Move);
    }

    private void OnCameraListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is CameraViewModel vm)
            MultiView.ShowCamera(vm.Id);
    }

    private void OnCameraListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(VideoTile.SlotDataFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCameraListDrop(object sender, DragEventArgs e)
    {
        // Tile dragged onto the camera list: remove it from the view.
        if (e.Data.GetDataPresent(VideoTile.SlotDataFormat))
            MultiView.ClearSlot((int)e.Data.GetData(VideoTile.SlotDataFormat));
    }

    private void OnCameraListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var vm = (e.OriginalSource as FrameworkElement)?.DataContext as CameraViewModel;
        var menu = CameraList.ContextMenu!;
        menu.Items.Clear();
        if (vm == null)
        {
            menu.Items.Add(MenuItem("Kamera hinzufügen …", () => OnAddCameraClick(this, new RoutedEventArgs())));
            menu.Items.Add(MenuItem("Kameras suchen (ONVIF) …", () => OnDiscoverClick(this, new RoutedEventArgs())));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Alle Namen von den Kameras übernehmen", () => FetchNamesFromCameras(_cameras.ToList()), _cameras.Count > 0));
            return;
        }
        CameraList.SelectedItem = vm;
        menu.Items.Add(MenuItem("In Ansicht anzeigen", () => MultiView.ShowCamera(vm.Id)));
        var quality = new MenuItem { Header = "Qualität" };
        foreach (var q in new[] { StreamQuality.Clear, StreamQuality.Mid, StreamQuality.Low })
        {
            var mi = new MenuItem { Header = q.DisplayName(), IsChecked = vm.Quality == q };
            mi.Click += (_, _) => SetCameraQuality(vm, q);
            quality.Items.Add(mi);
        }
        menu.Items.Add(quality);
        menu.Items.Add(MenuItem("Neu verbinden", () =>
        {
            foreach (var s in _svc.Streams.Streams.Where(s => s.CameraId == vm.Id)) s.Reconnect();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Kameraeinstellungen …", () => OpenCameraSettings(vm)));
        menu.Items.Add(MenuItem("Weboberfläche öffnen", () => OpenWebUi(vm)));
        menu.Items.Add(MenuItem("Verbindung bearbeiten …", () => EditCamera(vm)));
        menu.Items.Add(MenuItem("Namen von Kamera übernehmen", () => FetchNamesFromCameras(new[] { vm })));
        menu.Items.Add(MenuItem("Alle Namen von den Kameras übernehmen", () => FetchNamesFromCameras(_cameras.ToList())));
        int idx = _cameras.IndexOf(vm);
        menu.Items.Add(MenuItem("Nach oben", () => MoveCamera(vm, -1), idx > 0));
        menu.Items.Add(MenuItem("Nach unten", () => MoveCamera(vm, +1), idx < _cameras.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Entfernen", () => RemoveCamera(vm)));
    }

    private void MoveCamera(CameraViewModel vm, int delta)
    {
        int idx = _cameras.IndexOf(vm);
        int target = idx + delta;
        if (target < 0 || target >= _cameras.Count) return;
        _cameras.Move(idx, target);
        _svc.Cameras.Move(idx, target);
    }

    private static MenuItem MenuItem(string header, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    // =====================================================================
    // Settings / fullscreen / window
    // =====================================================================

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_svc.Settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _svc.ApplySettings();
            MultiView.RefreshOverlays();
        }
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => SetSidebarVisible(SidebarColumn.ActualWidth < 1);

    private void SetSidebarVisible(bool visible)
    {
        if (!visible && SidebarColumn.ActualWidth > 1) _svc.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        SidebarColumn.Width = new GridLength(visible ? _svc.Settings.SidebarWidth : 0);
        Splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _svc.Settings.SidebarVisible = visible;
        _svc.RequestSave();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResize = ResizeMode;
            WindowState = WindowState.Normal; // required so that Maximized covers the task bar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            SidebarColumn.Width = new GridLength(0);
            Splitter.Visibility = Visibility.Collapsed;
            ToolbarRow.Height = new GridLength(0);
            StatusRow.Height = new GridLength(0);
            FullscreenHint.Visibility = Visibility.Visible;
            var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hide.Tick += (_, _) => { hide.Stop(); FullscreenHint.Visibility = Visibility.Collapsed; };
            hide.Start();
        }
        else
        {
            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResize;
            WindowState = _preFullscreenState;
            ToolbarRow.Height = new GridLength(44);
            StatusRow.Height = new GridLength(26);
            FullscreenHint.Visibility = Visibility.Collapsed;
            SetSidebarVisible(_svc.Settings.SidebarVisible);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.F11:
                SetFullscreen(!_fullscreen);
                e.Handled = true;
                break;
            case Key.Escape:
                if (MultiView.IsMaximized) MultiView.Restore();
                else if (_fullscreen) SetFullscreen(false);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D6 when Keyboard.Modifiers == ModifierKeys.Control:
                var id = LayoutManager.PrimaryLayoutIds[e.Key - Key.D1];
                MultiView.SetLayout(_svc.Layouts.Find(id)!);
                e.Handled = true;
                break;
        }
    }

    private void RestoreWindowPlacement()
    {
        var p = _svc.Settings.Window;
        if (p != null && p.Width > 200 && p.Height > 200)
        {
            // Only restore if the window is visible on the current virtual screen.
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = p.Left; Top = p.Top; Width = p.Width; Height = p.Height;
            }
            if (p.Maximized) WindowState = WindowState.Maximized;
        }
        SidebarColumn.Width = new GridLength(_svc.Settings.SidebarVisible ? _svc.Settings.SidebarWidth : 0);
        Splitter.Visibility = _svc.Settings.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_fullscreen) SetFullscreen(false);
        var b = RestoreBounds;
        _svc.Settings.Window = new WindowPlacement
        {
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        if (SidebarColumn.ActualWidth > 1) _svc.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        OnViewChanged();
        _uiTimer.Stop();
        MultiView.ReleaseAll();
        _svc.SaveNow();
    }

    // =====================================================================
    // Periodic UI update (status, statistics)
    // =====================================================================

    private void OnUiTimer(object? sender, EventArgs e)
    {
        _svc.Audio.Update(id => _svc.Cameras.Find(id));
        MultiView.RefreshOverlays();
        foreach (var vm in _cameras)
            vm.UpdateStatus(_svc.Streams.TryGet(vm.Id, vm.Quality));

        if (++_tick % 2 != 0) return; // status bar once per second
        var streams = _svc.Streams.Streams;
        int playing = streams.Count(s => s.Status == StreamStatus.Playing);
        int offline = streams.Count(s => s.Status == StreamStatus.Reconnecting);
        int gpu = streams.Count(s => s.Status == StreamStatus.Playing && s.Statistics.IsHardware);
        int cpu = playing - gpu;
        double mbit = streams.Sum(s => s.Statistics.BitrateKbps) / 1000.0;
        StatusLeft.Text = $"Streams: {playing} aktiv" + (offline > 0 ? $", {offline} offline" : "") +
                          $"   ·   Decoder: {gpu} GPU / {cpu} CPU   ·   {mbit:0.0} Mbit/s   ·   Layout: {MultiView.Layout.Name}" +
                          (_svc.Audio.CameraId is Guid aid && FindVm(aid) is { } avm ? $"   ·   Ton: {avm.Name}{(_svc.Audio.Player.Muted ? " (stumm)" : "")}" : "");

        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpuTime = _process.TotalProcessorTime;
        double cpuPct = (cpuTime - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastCpuSample).TotalMilliseconds) / Environment.ProcessorCount * 100;
        _lastCpu = cpuTime;
        _lastCpuSample = now;
        StatusRight.Text = $"CPU {cpuPct:0.0} %   ·   RAM {_process.WorkingSet64 / 1048576} MB   ·   FFmpeg {FFmpegLoader.Version?.Split('-')[0].TrimStart('n')}";

        // Diagnostics line in the log every 30 s.
        if (_tick % 60 == 0)
            Core.Diagnostics.Log.Info($"Stats: {playing} playing, {offline} offline, {gpu} GPU / {cpu} CPU decoders, {mbit:0.0} Mbit/s, CPU {cpuPct:0.0}%, RAM {_process.WorkingSet64 / 1048576} MB, layout {MultiView.Layout.Id}");
    }
}
