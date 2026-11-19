using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenReo.Core.Localization;
using OpenReo.Core.Models;
using OpenReo.Video.Recordings;

namespace OpenReo.App.Dialogs;

/// <summary>
/// Chooses the recordings root (e.g. \\truenas\cameras) and assigns its sub folders to cameras.
/// Folders are matched to cameras by name automatically; the assignment can be changed.
/// </summary>
public partial class RecordingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<CameraConfig> _cameras;
    private readonly List<(string Folder, ComboBox Combo)> _rows = new();

    public RecordingsDialog(AppSettings settings, IReadOnlyList<CameraConfig> cameras)
    {
        InitializeComponent();
        _settings = settings;
        _cameras = cameras;
        RootBox.Text = settings.RecordingsRoot ?? "";
        EmptyText.Text = Lang.T("Noch kein Ordner gewählt.");
        Loaded += async (_, _) => { if (!string.IsNullOrWhiteSpace(RootBox.Text)) await ScanAsync(); };
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Lang.T("Aufnahme-Ordner wählen") };
        if (Directory.Exists(RootBox.Text)) dlg.InitialDirectory = RootBox.Text;
        if (dlg.ShowDialog(this) == true)
        {
            RootBox.Text = dlg.FolderName;
            _ = ScanAsync();
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        var root = RootBox.Text.Trim();
        FolderList.Items.Clear();
        _rows.Clear();
        if (root.Length == 0) { EmptyText.Text = Lang.T("Noch kein Ordner gewählt."); EmptyText.Visibility = Visibility.Visible; return; }
        StatusText.Text = Lang.T("Lese Ordner …");
        IReadOnlyList<string> folders;
        try
        {
            folders = await Task.Run(() => RecordingFolders.SubFolders(root));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            EmptyText.Text = Lang.F("Kein Zugriff auf „{0}“: {1}\n\nNetzlaufwerke bitte einmal im Explorer öffnen und anmelden (Anmeldedaten speichern).", root, ex.Message);
            EmptyText.Visibility = Visibility.Visible;
            StatusText.Text = "";
            return;
        }

        var used = new HashSet<Guid>();
        foreach (var folder in folders)
        {
            var combo = new ComboBox { Width = 270 };
            combo.Items.Add(new ComboBoxItem { Content = Lang.T("(keine Zuordnung)"), Tag = null });
            foreach (var c in _cameras) combo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });

            // existing assignment, otherwise match by name
            var cam = _cameras.FirstOrDefault(c => string.Equals(c.RecordingFolder, folder, StringComparison.OrdinalIgnoreCase))
                      ?? _cameras.FirstOrDefault(c => !used.Contains(c.Id) && string.IsNullOrEmpty(c.RecordingFolder)
                                                      && RecordingFolders.Match(c.Name, new[] { folder }) != null);
            combo.SelectedIndex = cam == null ? 0 : _cameras.ToList().IndexOf(cam) + 1;
            if (cam != null) used.Add(cam.Id);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            row.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            FolderList.Items.Add(new ListBoxItem { Content = row, Focusable = false });
            _rows.Add((folder, combo));
        }
        EmptyText.Text = Lang.T("Der Ordner enthält keine Unterordner.");
        EmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = Lang.F("{0} Ordner gefunden, {1} automatisch zugeordnet.", folders.Count, used.Count);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        _settings.RecordingsRoot = root.Length > 0 ? root : null;
        if (_rows.Count > 0)
        {
            foreach (var c in _cameras)
                if (!string.IsNullOrEmpty(c.RecordingFolder) && !Path.IsPathFullyQualified(c.RecordingFolder))
                    c.RecordingFolder = null; // reassigned below
            foreach (var (folder, combo) in _rows)
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is Guid id && _cameras.FirstOrDefault(c => c.Id == id) is { } cam)
                    cam.RecordingFolder = folder;
        }
        DialogResult = true;
    }
}
