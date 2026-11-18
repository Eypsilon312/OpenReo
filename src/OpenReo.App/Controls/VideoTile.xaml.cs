using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenReo.App.Video;
using OpenReo.App.ViewModels;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App.Controls;

/// <summary>Callbacks from a tile to its multiview container.</summary>
public interface ITileHost
{
    StreamManager Streams { get; }
    AppSettings Settings { get; }
    IReadOnlyList<CameraViewModel> AllCameras { get; }
    bool IsMaximized { get; }
    void TileSelected(VideoTile tile);
    void TileDoubleClicked(VideoTile tile);
    void TileDrop(VideoTile target, Guid? cameraId, int? sourceSlot);
    void AssignCamera(VideoTile tile, Guid? cameraId);
    void SetCameraQuality(CameraViewModel camera, StreamQuality quality);
    void EditCamera(CameraViewModel camera);
    void OpenCameraSettings(CameraViewModel camera);
    Guid? AudioCameraId { get; }
    void ToggleAudio(CameraViewModel camera);
}

/// <summary>
/// One video tile: shows a camera stream with digital zoom (mouse wheel), pan
/// (drag while zoomed), double click to maximize and drag &amp; drop to move cameras.
/// </summary>
public partial class VideoTile : UserControl
{
    public const string SlotDataFormat = "OpenReo.Slot";
    public const string CameraDataFormat = "OpenReo.Camera";
    private const double MaxZoom = 16.0;

    private readonly ITileHost _host;
    private StreamLease? _lease;
    private VideoSurface? _surface;
    private bool _active = true;
    private bool _selected;

    private double _zoom = 1.0;
    private Point _dragStart;
    private Point _panStart;
    private double _panStartX, _panStartY;
    private bool _mouseDown;
    private bool _panning;

    public int SlotIndex { get; set; }
    public CameraViewModel? Camera { get; private set; }
    public StreamQuality? BoundQuality { get; private set; }
    public RtspStream? Stream => _lease?.Stream;

    public VideoTile(ITileHost host)
    {
        _host = host;
        InitializeComponent();
        SizeChanged += (_, _) => { ClampPan(); UpdateTargetSize(); };
        Root.MouseWheel += OnMouseWheel;
        Root.MouseLeftButtonDown += OnMouseLeftButtonDown;
        Root.MouseLeftButtonUp += OnMouseLeftButtonUp;
        Root.MouseMove += OnMouseMove;
        Root.MouseDown += OnMouseDownAny;
        Root.DragEnter += OnDragOver;
        Root.DragOver += OnDragOver;
        Root.DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Root.Drop += OnDrop;
        Root.ContextMenuOpening += OnContextMenuOpening;
        Root.ContextMenu = new ContextMenu();
        AudioButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // no selection / drag / maximize
            if (Camera != null) _host.ToggleAudio(Camera);
            RefreshOverlay();
        };
        UpdateStaticOverlay();
    }

    // ------------------------------------------------------------------ binding

    /// <summary>Shows the given camera (or nothing). Re-uses the stream if it is unchanged.</summary>
    public void Bind(CameraViewModel? camera)
    {
        var quality = camera?.Config.Quality;
        if (camera == Camera && quality == BoundQuality && (_lease != null || camera == null)) return;

        bool sameCamera = camera != null && camera == Camera;
        if (!sameCamera) ResetZoom();

        var oldLease = _lease;
        var oldSurface = _surface;
        _lease = null;
        _surface = null;

        Camera = camera;
        BoundQuality = quality;

        if (camera != null)
        {
            _lease = _host.Streams.Acquire(camera.Config, camera.Config.Quality);
            _surface = RenderLoop.Acquire(_lease.Stream);
            _surface.BitmapChanged += OnBitmapChanged;
            if (_surface.Bitmap != null) VideoImage.Source = _surface.Bitmap;
            else if (!sameCamera) VideoImage.Source = null; // keep the old picture while switching quality
            UpdateTargetSize();
        }
        else
        {
            VideoImage.Source = null;
        }

        if (oldSurface != null)
        {
            oldSurface.BitmapChanged -= OnBitmapChanged;
            RenderLoop.Release(oldSurface);
        }
        // A quality switch closes the old stream immediately (saves bandwidth); otherwise keep it for the release delay.
        oldLease?.Release(immediate: sameCamera);

        UpdateStaticOverlay();
        RefreshOverlay();
    }

    /// <summary>Releases the stream (tile is removed / window closes).</summary>
    public void Unbind() => Bind(null);

    private void OnBitmapChanged(VideoSurface surface)
    {
        if (surface == _surface) VideoImage.Source = surface.Bitmap;
    }

    /// <summary>Visible tiles request frames; hidden tiles keep the connection but stop conversion.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        UpdateTargetSize();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; SelectionBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>Requests frames in the actual on-screen pixel size (times zoom for sharp digital zoom).</summary>
    private void UpdateTargetSize()
    {
        if (_lease == null) return;
        if (!_active || ActualWidth < 2 || ActualHeight < 2)
        {
            _lease.SetTargetSize(0, 0);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Min(8192, ActualWidth * dpi.DpiScaleX * _zoom);
        int h = (int)Math.Min(8192, ActualHeight * dpi.DpiScaleY * _zoom);
        _lease.SetTargetSize(w, h);
    }

    // ------------------------------------------------------------------ overlay

    private void UpdateStaticOverlay()
    {
        bool hasCam = Camera != null;
        EmptyHint.Visibility = hasCam ? Visibility.Collapsed : Visibility.Visible;
        TopBar.Visibility = hasCam && _host.Settings.ShowTileOverlay ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Visibility = hasCam && _host.Settings.ShowStreamInfo ? Visibility.Visible : Visibility.Collapsed;
        if (!hasCam) StatusPanel.Visibility = Visibility.Collapsed;
        NameText.Text = Camera?.Name ?? "";
        QualityText.Text = Camera?.Config.Quality.DisplayName() ?? "";
        QualityBadge.Background = new SolidColorBrush(Camera?.Config.Quality switch
        {
            StreamQuality.Clear => Color.FromArgb(0xC0, 0x2F, 0x8C, 0xFF),
            StreamQuality.Mid => Color.FromArgb(0xC0, 0x3B, 0x9E, 0x6E),
            _ => Color.FromArgb(0xC0, 0x7A, 0x7F, 0x8C),
        });
    }

    /// <summary>Periodic overlay update (status + statistics).</summary>
    public void RefreshOverlay()
    {
        UpdateStaticOverlay();
        var s = _lease?.Stream;
        if (s == null || Camera == null) return;

        string? title = null, detail = null;
        Brush dot = (Brush)FindResource("IdleBrush");
        switch (s.Status)
        {
            case StreamStatus.Playing:
                dot = (Brush)FindResource("OkBrush");
                break;
            case StreamStatus.Idle:
            case StreamStatus.Connecting:
                dot = (Brush)FindResource("WarnBrush");
                title = "Verbinde …";
                detail = $"{Camera.Name} · {s.Quality.DisplayName()}";
                break;
            case StreamStatus.Reconnecting:
                dot = (Brush)FindResource("ErrorBrush");
                title = "Offline";
                var retry = s.NextRetryUtc is { } t ? Math.Max(0, (int)Math.Ceiling((t - DateTime.UtcNow).TotalSeconds)) : 0;
                detail = (s.LastError ?? "Verbindung verloren") + (retry > 0 ? $"\nNeuer Versuch in {retry} s" : "\nVerbinde erneut …");
                break;
            case StreamStatus.Stopped:
                title = "Gestoppt";
                break;
        }
        StatusDot.Fill = dot;
        StatusPanel.Visibility = title != null ? Visibility.Visible : Visibility.Collapsed;
        StatusTitle.Text = title ?? "";
        StatusDetail.Text = detail ?? "";

        if (_host.Settings.ShowStreamInfo)
        {
            var st = s.Statistics;
            InfoText.Text = st.Width > 0
                ? $"{st.Codec} {st.Width}×{st.Height} · {st.Fps:0} fps · {st.BitrateKbps / 1000:0.0} Mbit/s · {(st.IsHardware ? "GPU" : "CPU")}"
                : "";
            InfoText.ToolTip = st.Width > 0 ? $"Decoder: {st.Decoder}\nAnzeige: {st.OutputWidth}×{st.OutputHeight}\nFrames: {st.FramesDecoded}, Fehler: {st.DecodeErrors}" : null;
        }
        ZoomText.Visibility = _zoom > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{_zoom:0.0}×";
        RefreshAudioIcon(s);
    }

    private void RefreshAudioIcon(RtspStream s)
    {
        bool active = _host.AudioCameraId == Camera?.Id;
        bool hasAudio = s.AudioCodec != null;
        AudioIcon.Text = active ? "\uE767" : "\uE74F"; // Volume / Mute
        AudioIcon.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        AudioButton.Background = active ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        AudioButton.Opacity = hasAudio || s.Status != StreamStatus.Playing ? 1.0 : 0.45;
        AudioButton.ToolTip = s.Status == StreamStatus.Playing && !hasAudio
            ? "Diese Kamera sendet keinen Ton.\nIn den Kameraeinstellungen → Streams „Audio aufzeichnen“ aktivieren."
            : active ? $"Ton aus ({s.AudioCodec})" : "Ton dieser Kamera wiedergeben";
    }

    // ------------------------------------------------------------------ zoom & pan

    public void ResetZoom()
    {
        _zoom = 1.0;
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1.0;
        ZoomPan.X = ZoomPan.Y = 0;
        ZoomText.Visibility = Visibility.Collapsed;
        UpdateTargetSize();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Camera == null) return;
        e.Handled = true;
        var p = e.GetPosition(Root);
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        double newZoom = Math.Clamp(_zoom * factor, 1.0, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;

        // Keep the point under the mouse cursor fixed: t' = p - (p - t) * s'/s
        double ratio = newZoom / _zoom;
        ZoomPan.X = p.X - (p.X - ZoomPan.X) * ratio;
        ZoomPan.Y = p.Y - (p.Y - ZoomPan.Y) * ratio;
        _zoom = newZoom;
        if (_zoom <= 1.0001) { _zoom = 1; ZoomPan.X = ZoomPan.Y = 0; }
        ZoomScale.ScaleX = ZoomScale.ScaleY = _zoom;
        ClampPan();
        UpdateTargetSize();
        ZoomText.Visibility = _zoom > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{_zoom:0.0}×";
    }

    private void ClampPan()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        ZoomPan.X = Math.Clamp(ZoomPan.X, w - w * _zoom, 0);
        ZoomPan.Y = Math.Clamp(ZoomPan.Y, h - h * _zoom, 0);
    }

    private void OnMouseDownAny(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { ResetZoom(); e.Handled = true; }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _host.TileSelected(this);
        if (e.ClickCount == 2)
        {
            _mouseDown = false;
            _panning = false;
            Root.ReleaseMouseCapture();
            _host.TileDoubleClicked(this);
            e.Handled = true;
            return;
        }
        _mouseDown = true;
        _dragStart = e.GetPosition(Root);
        _panStart = _dragStart;
        _panStartX = ZoomPan.X;
        _panStartY = ZoomPan.Y;
        _panning = _zoom > 1.001;
        if (_panning)
        {
            Root.CaptureMouse();
            Root.Cursor = Cursors.SizeAll;
        }
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = false;
        if (_panning)
        {
            _panning = false;
            Root.ReleaseMouseCapture();
            Root.Cursor = null;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) { _mouseDown = false; return; }
        var p = e.GetPosition(Root);
        if (_panning)
        {
            // Pan the zoomed picture.
            ZoomPan.X = _panStartX + (p.X - _panStart.X);
            ZoomPan.Y = _panStartY + (p.Y - _panStart.Y);
            ClampPan();
            return;
        }
        // Not zoomed: start drag & drop to move the camera to another tile.
        if (Camera != null && !_host.IsMaximized &&
            (Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _mouseDown = false;
            var data = new DataObject(SlotDataFormat, SlotIndex);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }
    }

    // ------------------------------------------------------------------ drag & drop target

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = !_host.IsMaximized && (e.Data.GetDataPresent(CameraDataFormat) ||
                  (e.Data.GetDataPresent(SlotDataFormat) && (int)e.Data.GetData(SlotDataFormat) != SlotIndex));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (_host.IsMaximized) return;
        if (e.Data.GetDataPresent(CameraDataFormat) && Guid.TryParse(e.Data.GetData(CameraDataFormat) as string, out var id))
            _host.TileDrop(this, id, null);
        else if (e.Data.GetDataPresent(SlotDataFormat))
            _host.TileDrop(this, null, (int)e.Data.GetData(SlotDataFormat));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ context menu

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _host.TileSelected(this);
        var menu = Root.ContextMenu!;
        menu.Items.Clear();

        if (Camera != null)
        {
            var cam = Camera;
            menu.Items.Add(new MenuItem { Header = cam.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            var quality = new MenuItem { Header = "Qualität" };
            foreach (var q in new[] { StreamQuality.Clear, StreamQuality.Mid, StreamQuality.Low })
            {
                var item = new MenuItem { Header = q.DisplayName() + q switch { StreamQuality.Clear => "  (Hauptstream)", StreamQuality.Mid => "  (mittel)", _ => "  (Substream)" }, IsChecked = cam.Quality == q };
                item.Click += (_, _) => _host.SetCameraQuality(cam, q);
                quality.Items.Add(item);
            }
            menu.Items.Add(quality);
            menu.Items.Add(Item(_host.AudioCameraId == cam.Id ? "Ton aus" : "Ton wiedergeben", () => _host.ToggleAudio(cam)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(_host.IsMaximized ? "Zurück zur Mehrfachansicht" : "Maximieren", () => _host.TileDoubleClicked(this), "Doppelklick"));
            menu.Items.Add(Item("Zoom zurücksetzen", ResetZoom, "Mittlere Maustaste", _zoom > 1.001));
            menu.Items.Add(Item("Neu verbinden", () => _lease?.Stream.Reconnect()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Kameraeinstellungen …", () => _host.OpenCameraSettings(cam)));
            menu.Items.Add(Item("Verbindung bearbeiten …", () => _host.EditCamera(cam)));
            if (!_host.IsMaximized)
                menu.Items.Add(Item("Aus Ansicht entfernen", () => _host.AssignCamera(this, null)));
        }

        if (!_host.IsMaximized)
        {
            var assign = new MenuItem { Header = Camera == null ? "Kamera zuweisen" : "Andere Kamera anzeigen" };
            foreach (var c in _host.AllCameras)
            {
                var cam = c;
                var mi = new MenuItem { Header = cam.Name, IsChecked = cam == Camera };
                mi.Click += (_, _) => _host.AssignCamera(this, cam.Id);
                assign.Items.Add(mi);
            }
            if (assign.Items.Count == 0) assign.Items.Add(new MenuItem { Header = "(keine Kameras angelegt)", IsEnabled = false });
            if (Camera != null) menu.Items.Insert(menu.Items.Count - 1, assign);
            else menu.Items.Add(assign);
        }
    }

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }
}
