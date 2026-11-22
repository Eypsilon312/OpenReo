using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenReo.App.Audio;
using OpenReo.App.Video;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Localization;
using OpenReo.Core.Models;
using OpenReo.Video;
using OpenReo.Video.Recordings;

namespace OpenReo.App.Playback;

/// <summary>
/// Playback of the recordings of one camera from its recording folder (e.g. FTP uploads on a NAS):
/// list of days, time line (zoom/scroll/click), play/pause, ±10 s, 0.25× … 16×, seamless
/// continuation into the next file.
/// </summary>
public partial class PlaybackWindow : Window
{
    private static readonly TimeSpan MaxContinuationGap = TimeSpan.FromMinutes(10);

    private readonly CameraConfig _camera;
    private readonly string _folder;
    private readonly PlaybackSession _session;
    private readonly VideoSurface _surface;
    private readonly AudioPlayer _audio = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private RecordingIndex? _index;
    private RecordingFile? _current;
    private bool _soundOn = true;
    private bool _suppressDayChange;

    public PlaybackWindow(CameraConfig camera, string folder, AppSettings settings)
    {
        InitializeComponent();
        _camera = camera;
        _folder = folder;
        Title = Lang.F("Wiedergabe – {0}", camera.Name);
        CameraNameText.Text = camera.Name;
        FolderText.Text = folder;
        FolderText.ToolTip = folder;

        _session = new PlaybackSession(settings.HardwareAcceleration) { Paused = true };
        _session.Ended += (s, path) => Dispatcher.BeginInvoke(() => OnFileEnded(path));
        _audio.Volume = (float)settings.AudioVolume;
        _audio.MaxBacklog = TimeSpan.FromSeconds(1); // playback releases audio on time itself
        _session.AudioSink = _audio;

        _surface = RenderLoop.Acquire(_session.Frames);
        _surface.BitmapChanged += s => VideoImage.Source = s.Bitmap;

        SpeedBox.SelectedIndex = 2; // 1×
        Timeline.SeekRequested += t => SeekTo(t);
        Timeline.MarkerHover += OnMarkerHover;
        VideoHost.SizeChanged += (_, _) => UpdateTargetSize();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();

        PreviewKeyDown += OnKeyDown;
        Loaded += async (_, _) => { UpdateTargetSize(); await LoadIndexAsync(); };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cts.Cancel();
            _session.AudioSink = null;
            _audio.Dispose();
            RenderLoop.Release(_surface);
            _session.Dispose();
        };
    }

    // =====================================================================
    // Index / days
    // =====================================================================

    private async Task LoadIndexAsync()
    {
        ShowStatus(Lang.T("Lese Aufnahmen …"));
        try
        {
            _index = await RecordingIndex.ScanAsync(_folder, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ShowStatus(Lang.F("Aufnahme-Ordner nicht lesbar: {0}", ex.Message));
            return;
        }

        DayList.Items.Clear();
        foreach (var day in _index.Days)
        {
            int count = _index.Files.Count(f => f.Start.Date == day);
            DayList.Items.Add(new ListBoxItem
            {
                Content = $"{day.ToString("ddd, dd.MM.yyyy", CultureInfo.CurrentCulture)}  ({count})",
                Tag = day,
            });
        }
        if (_index.Files.Count == 0)
        {
            ShowStatus(Lang.T("Keine Aufnahmen in diesem Ordner gefunden."));
            return;
        }
        ShowStatus(null);
        DayList.SelectedIndex = 0; // newest day

        // start at the beginning of the most recent recording (paused)
        var last = _index.Files[^1];
        OpenFile(last, 0);
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        var day = (DayList.SelectedItem as ListBoxItem)?.Tag as DateTime?;
        await LoadIndexAsync();
        if (day != null) SelectDay(day.Value);
    }

    private void OnDaySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDayChange || _index == null || DayList.SelectedItem is not ListBoxItem { Tag: DateTime day }) return;
        ShowDay(day);
        // jump to the first recording of the day when the user picks another day
        if (_current == null || _current.Start.Date != day)
        {
            // the first file may have started before midnight: open it at 00:00 of the chosen day
            var first = _index.FilesOn(day).FirstOrDefault();
            if (first != null) OpenFile(first, Math.Max(0, (day - first.Start).TotalSeconds));
        }
    }

    private void SelectDay(DateTime day)
    {
        _suppressDayChange = true;
        DayList.SelectedItem = DayList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (DateTime)i.Tag == day.Date);
        _suppressDayChange = false;
        ShowDay(day);
    }

    private void ShowDay(DateTime day)
    {
        if (_index == null) return;
        var files = _index.FilesOn(day);
        Timeline.SetDay(day, Segments(files));
        _daySnapshots = _index.SnapshotsOn(day);
        Timeline.SetMarkers(_daySnapshots.Select(s => s.Time).ToList());
        // durations that are not known from the file times yet: read in the background
        var progress = new Progress<RecordingFile>(_ => { if (Timeline.Day == day.Date) Timeline.SetSegments(Segments(files)); });
        _ = RecordingIndex.LoadDurationsAsync(files, progress, _cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static List<(DateTime, DateTime, bool)> Segments(IEnumerable<RecordingFile> files) =>
        files.Select(f => (f.Start, f.End, f.HasReliableDuration)).ToList();

    // =====================================================================
    // Event markers (snapshots uploaded by the camera)
    // =====================================================================

    private IReadOnlyList<RecordingSnapshot> _daySnapshots = Array.Empty<RecordingSnapshot>();
    private readonly Dictionary<string, BitmapSource> _thumbnails = new();
    private int? _previewIndex;

    private async void OnMarkerHover(int? index, double x)
    {
        _previewIndex = index;
        if (index is not int i || i >= _daySnapshots.Count)
        {
            PreviewPopup.IsOpen = false;
            return;
        }
        var snap = _daySnapshots[i];
        PreviewText.Text = Lang.F("Ereignis {0}", snap.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
        PreviewPopup.HorizontalOffset = Math.Clamp(x - 164, 0, Math.Max(0, Timeline.ActualWidth - 328));
        PreviewPopup.IsOpen = true;
        if (_thumbnails.TryGetValue(snap.Path, out var cached))
        {
            ShowPreview(cached);
            return;
        }
        ShowPreview(null);
        BitmapSource? image = null;
        try { image = await Task.Run(() => LoadThumbnail(snap.Path)); }
        catch (Exception ex) { Log.Debug($"Snapshot {snap.Path}: {ex.Message}"); }
        if (image == null) { if (_previewIndex == index) PreviewLoading.Text = "–"; return; }
        if (_thumbnails.Count > 200) _thumbnails.Clear();
        _thumbnails[snap.Path] = image;
        if (_previewIndex == index) ShowPreview(image); // still on the same marker
    }

    private void ShowPreview(BitmapSource? image)
    {
        PreviewImage.Source = image;
        PreviewLoading.Text = "…";
        PreviewLoading.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapSource LoadThumbnail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 320;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // =====================================================================
    // Playback control
    // =====================================================================

    private void OpenFile(RecordingFile file, double offset)
    {
        _current = file;
        _session.Open(file.Path, offset);
        FileText.Text = file.FileName;
        FileText.ToolTip = file.Path;
        var day = file.Start.AddSeconds(offset).Date; // day of the playback position, not of the file start
        if (Timeline.Day != day) SelectDay(day);
        ShowStatus(null);
    }

    /// <summary>Jumps to a point in time (file is chosen automatically).</summary>
    private void SeekTo(DateTime time)
    {
        if (_index == null) return;
        var file = _index.FindAt(time);
        if (file == null)
        {
            ShowStatus(Lang.T("Keine Aufnahme zu dieser Zeit."));
            return;
        }
        bool gap = file.Start > time;
        double offset = gap ? 0 : Math.Max(0, (time - file.Start).TotalSeconds);
        if (file == _current) _session.Seek(offset);
        else OpenFile(file, offset);
        if (gap) ShowStatus(Lang.F("Keine Aufnahme zu dieser Zeit – weiter bei {0:HH:mm:ss}.", file.Start), autoHide: true);
    }

    private void OnFileEnded(string path)
    {
        if (_index == null || _current == null || _current.Path != path || _session.Paused) return;
        var next = _index.Next(_current);
        if (next != null && next.Start - _current.End <= MaxContinuationGap)
            OpenFile(next, 0); // seamless continuation
        else
        {
            SetPaused(true);
            ShowStatus(next == null ? Lang.T("Ende der Aufnahmen.") : Lang.F("Lücke in den Aufnahmen – nächste Aufnahme um {0:HH:mm:ss}.", next.Start), autoHide: next != null);
        }
    }

    private void SetPaused(bool paused)
    {
        _session.Paused = paused;
        PlayIcon.Text = paused ? "" : ""; // Play / Pause
        if (paused) _audio.Stop(); else if (_soundOn) _audio.Start();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SetPaused(!_session.Paused);
    }

    private DateTime? CurrentTime => _current == null ? null : _current.Start + TimeSpan.FromSeconds(_session.Position);

    private void Jump(double seconds)
    {
        if (CurrentTime is DateTime t) SeekTo(t + TimeSpan.FromSeconds(seconds));
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Jump(-10);
    private void OnForwardClick(object sender, RoutedEventArgs e) => Jump(10);

    private void OnPrevFileClick(object sender, RoutedEventArgs e)
    {
        if (_index == null || _current == null) return;
        // within the first 3 s: previous file, otherwise back to the start of the current one
        if (_session.Position > 3) _session.Seek(0);
        else if (_index.Previous(_current) is { } prev) OpenFile(prev, 0);
    }

    private void OnNextFileClick(object sender, RoutedEventArgs e)
    {
        if (_index?.Next(_current!) is { } next) OpenFile(next, 0);
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string s } && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            _session.Speed = speed;
    }

    private void OnSoundClick(object sender, RoutedEventArgs e)
    {
        _soundOn = !_soundOn;
        _audio.Muted = !_soundOn;
        SoundIcon.Text = _soundOn ? "" : "";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: OnPlayPauseClick(this, new RoutedEventArgs()); break;
            case Key.Left: Jump(Keyboard.Modifiers == ModifierKeys.Shift ? -60 : -10); break;
            case Key.Right: Jump(Keyboard.Modifiers == ModifierKeys.Shift ? 60 : 10); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    // =====================================================================
    // Files
    // =====================================================================

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        var path = _current?.Path;
        try
        {
            if (path != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Explorer: {ex.Message}"); }
    }

    private async void OnSaveFileClick(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new SaveFileDialog
        {
            FileName = $"{_camera.Name}_{_current.Start:yyyy-MM-dd_HH-mm-ss}{Path.GetExtension(_current.Path)}",
            Filter = Lang.T("Videodatei") + $" (*{Path.GetExtension(_current.Path)})|*{Path.GetExtension(_current.Path)}",
        };
        if (dlg.ShowDialog(this) != true) return;
        var source = _current.Path;
        ShowStatus(Lang.T("Speichere Datei …"));
        try
        {
            await Task.Run(() => File.Copy(source, dlg.FileName, overwrite: true));
            ShowStatus(Lang.T("Datei gespeichert."), autoHide: true);
        }
        catch (Exception ex)
        {
            ShowStatus(Lang.F("Speichern fehlgeschlagen: {0}", ex.Message));
        }
    }

    // =====================================================================
    // Display
    // =====================================================================

    private void UpdatePosition()
    {
        if (CurrentTime is not DateTime t) return;
        // the player knows the exact length of the file → time line becomes exact for it
        if (_current is { Duration: null } cur && _session.CurrentFile == cur.Path && _session.Duration > 0)
        {
            cur.Duration = TimeSpan.FromSeconds(_session.Duration);
            if (_index != null && Timeline.Day == cur.Start.Date) Timeline.SetSegments(Segments(_index.FilesOn(cur.Start.Date)));
        }
        Timeline.Position = t;
        var text = t.ToString("ddd, dd.MM.yyyy  HH:mm:ss", CultureInfo.CurrentCulture);
        TimeText.Text = text;
        OverlayTime.Text = text;
        if (_session.Error != null) ShowStatus(_session.Error);
    }

    private void UpdateTargetSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _session.SetTargetSize((int)(VideoHost.ActualWidth * dpi.DpiScaleX), (int)(VideoHost.ActualHeight * dpi.DpiScaleY));
    }

    private DispatcherTimer? _statusTimer;

    private void ShowStatus(string? text, bool autoHide = false)
    {
        _statusTimer?.Stop();
        StatusPanel.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = text ?? "";
        if (text != null && autoHide)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusPanel.Visibility = Visibility.Collapsed; };
            _statusTimer.Start();
        }
    }
}
