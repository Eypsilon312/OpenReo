using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Core.Reolink;

namespace OpenReo.App.CameraSettings;

/// <summary>
/// Edits the settings of a Reolink camera via its HTTP API. Each page shows only what the
/// connected camera reports; "Übernehmen" sends only the changed values.
/// </summary>
public partial class CameraSettingsWindow : Window
{
    private readonly CameraConfig _camera;
    private readonly Action<string>? _onNameChanged;
    private readonly int _ch;
    private readonly List<SettingsPage> _pages;
    private readonly Dictionary<SettingsPage, TextBlock> _dirtyMarks = new();
    private ReolinkSession? _session;
    private SettingsContext? _ctx;
    private SettingsPage? _current;
    private bool _busy;

    public CameraSettingsWindow(CameraConfig camera, Action<string>? onNameChanged = null)
    {
        InitializeComponent();
        _camera = camera;
        _onNameChanged = onNameChanged;
        _ch = Math.Max(0, camera.Channel - 1);
        _pages = SettingsCatalog.Create(_ch);
        Title = $"Kameraeinstellungen – {camera.Name}";
        CameraNameText.Text = camera.Name;
        CameraInfoText.Text = camera.Channel > 1 ? $"{camera.Host} · Kanal {camera.Channel}" : camera.Host;
        ContentScroller.Visibility = Visibility.Collapsed;

        Loaded += async (_, _) => await ConnectAndLoadAsync();
        Closing += OnClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Closed += async (_, _) => { if (_session != null) await _session.DisposeAsync(); };
    }

    private IEnumerable<SettingsSection> AllSections => _pages.SelectMany(p => p.Sections);

    private SettingsSection? FindSection(string key) => AllSections.FirstOrDefault(s =>
        s.Supported && (s.GetCmd == key || s.ActiveGetCmd == key || s.Edited!.ContainsKey(key)));

    // =====================================================================
    // Loading
    // =====================================================================

    private async Task ConnectAndLoadAsync()
    {
        SetBusy(true);
        try
        {
            LoadingText.Text = "Verbinde mit der Kamera …";
            _session = await ReolinkSession.ConnectAsync(_camera.Host, _camera.HttpPort, _camera.Username, _camera.Password, CancellationToken.None);
            _ctx = new SettingsContext
            {
                Session = _session, Channel = _ch, Owner = this,
                Report = msg => { StatusText.Text = msg; return Task.CompletedTask; },
                Rebuild = () => { if (_current != null) RenderPage(_current); UpdateDirty(); },
                FindSection = FindSection,
            };
            await LoadAllAsync();
            BuildNavigation();
            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentScroller.Visibility = Visibility.Visible;
            StatusText.Text = $"Verbunden über {_session.BaseUrl}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Camera settings for {_camera.Host}: {ex.Message}");
            LoadingText.Text = "Keine Verbindung zur Kamera-API";
            LoadingDetail.Text = ex.Message + "\n\nDie Einstellungen verwenden die Reolink-HTTP-API (Port " + _camera.HttpPort +
                                 "). Prüfen Sie, ob HTTP/HTTPS in der Kamera aktiviert ist und der HTTP-Port in den Kamera-Eigenschaften stimmt. " +
                                 "Akku-/WLAN-Modelle ohne Weboberfläche werden nicht unterstützt.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadAllAsync()
    {
        var sections = AllSections.ToList();
        for (int i = 0; i < sections.Count; i++)
        {
            LoadingDetail.Text = $"Lese Einstellungen ({i + 1}/{sections.Count}): {sections[i].Title}";
            await LoadSectionAsync(sections[i]);
        }
        var ptz = _pages.FirstOrDefault(p => p.CustomContent != null);
        if (ptz != null) ptz.CustomSupported = await DetectPtzAsync();
    }

    private async Task LoadSectionAsync(SettingsSection s)
    {
        var get = s.GetCmd;
        var set = s.SetCmd;
        var r = await _session!.ExecuteAsync(get, s.GetParam, 1);
        if (!r.Ok) r = await _session.ExecuteAsync(get, s.GetParam, 0);
        if (!r.Ok && s.Fallback is { } fb)
        {
            get = fb.Get;
            set = fb.Set;
            r = await _session.ExecuteAsync(get, s.GetParam, 1);
            if (!r.Ok) r = await _session.ExecuteAsync(get, s.GetParam, 0);
        }

        bool hasContent = r.Ok && r.Value != null &&
                          (s.Fields.Any(f => JsonUtil.Get(r.Value, f.Path) is JsonValue) || s.SchedulePaths.Any(p => JsonUtil.Get(r.Value, p) != null));
        if (!hasContent)
        {
            s.Original = s.Edited = null;
            if (!r.Ok) Log.Debug($"Camera {_camera.Host}: {get} not supported ({r.ErrorDetail})");
            return;
        }
        // Diagnostics: raw answer of the camera (passwords removed) – helps adapting to new models.
        Log.Debug($"Camera {_camera.Host}: {get} value={Redact(r.Value!)} range={(r.Range != null ? Redact(r.Range) : "-")}");
        s.ActiveGetCmd = get;
        s.ActiveSetCmd = set;
        s.Original = (JsonObject)r.Value!.DeepClone();
        s.Edited = (JsonObject)r.Value.DeepClone();
        s.Range = r.Range;
    }

    private static string Redact(JsonObject o)
    {
        var copy = (JsonObject)o.DeepClone();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject obj)
            {
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase)) obj[key] = "***";
                    else Walk(obj[key]);
                }
            }
            else if (n is JsonArray a) foreach (var i in a) Walk(i);
        }
        Walk(copy);
        var json = copy.ToJsonString();
        return json.Length > 4000 ? json[..4000] + "…" : json;
    }

    private async Task<bool> DetectPtzAsync()
    {
        try
        {
            var ability = await _session!.ExecuteAsync("GetAbility", new JsonObject { ["User"] = new JsonObject { ["userName"] = _camera.Username } });
            if (ability.Ok && JsonUtil.Get(ability.Value, $"Ability.abilityChn.{_ch}") is JsonObject chn)
            {
                int Ver(string key) => JsonUtil.AsInt(JsonUtil.Get(chn, key + ".ver")) ?? 0;
                return Ver("ptzCtrl") > 0 || Ver("ptzType") > 0 || Ver("ptzPreset") > 0;
            }
            var presets = await _session.ExecuteAsync("GetPtzPreset", new JsonObject { ["channel"] = _ch });
            return presets.Ok && JsonUtil.Get(presets.Value, "PtzPreset") is JsonArray { Count: > 0 };
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    // Navigation / rendering
    // =====================================================================

    private void BuildNavigation()
    {
        var selected = _current;
        PageList.Items.Clear();
        _dirtyMarks.Clear();
        foreach (var page in _pages.Where(p => p.Visible))
        {
            var dot = new TextBlock { Text = "●", Foreground = (Brush)FindResource("WarnBrush"), FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = page.Icon, Style = (Style)FindResource("Icon"), Width = 20, Margin = new Thickness(0, 0, 10, 0) });
            content.Children.Add(new TextBlock { Text = page.Title, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(dot);
            _dirtyMarks[page] = dot;
            PageList.Items.Add(new ListBoxItem { Content = content, Tag = page, Padding = new Thickness(10, 8, 10, 8) });
        }
        if (PageList.Items.Count == 0)
        {
            ContentPanel.Children.Clear();
            ContentPanel.Children.Add(new TextBlock { Text = "Die Kamera meldet keine bearbeitbaren Einstellungen.", Margin = new Thickness(0, 20, 0, 0) });
            return;
        }
        var item = PageList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == selected) ?? (ListBoxItem)PageList.Items[0];
        PageList.SelectedItem = item;
        UpdateDirty();
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is ListBoxItem { Tag: SettingsPage page })
        {
            _current = page;
            RenderPage(page);
            ContentScroller.ScrollToTop();
        }
    }

    private void RenderPage(SettingsPage page)
    {
        ContentPanel.Children.Clear();
        ContentPanel.Children.Add(new TextBlock { Text = page.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });

        if (page.Actions.Count > 0)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            foreach (var a in page.Actions)
            {
                var action = a;
                var b = new Button { Content = action.Label, Margin = new Thickness(0, 0, 8, 6) };
                if (action.Destructive) b.Foreground = (Brush)FindResource("ErrorBrush");
                b.Click += async (_, _) =>
                {
                    if (_ctx == null || _busy) return;
                    SetBusy(true);
                    try { await action.Run(_ctx); }
                    catch (Exception ex) { StatusText.Text = $"{action.Label}: {ex.Message}"; }
                    finally { SetBusy(false); }
                };
                bar.Children.Add(b);
            }
            ContentPanel.Children.Add(bar);
        }

        foreach (var s in page.Sections.Where(s => s.Supported))
            ContentPanel.Children.Add(RenderSection(s));

        if (page.CustomContent != null && page.CustomSupported && _ctx != null)
        {
            var custom = page.CustomContent(_ctx);
            if (custom != null) ContentPanel.Children.Add(Card(custom));
        }
    }

    private Border Card(UIElement child) => new()
    {
        Background = (Brush)FindResource("Panel2Brush"),
        BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(18, 12, 18, 16),
        Margin = new Thickness(0, 12, 0, 0),
        Child = child,
    };

    private Border RenderSection(SettingsSection s)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        if (s.Note != null)
            panel.Children.Add(new TextBlock { Text = s.Note, Style = (Style)FindResource("DimText"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var seen = new HashSet<string>();
        foreach (var f in s.Fields)
        {
            FrameworkElement? control;
            try
            {
                control = CreateControl(s, f);
            }
            catch (Exception ex)
            {
                // An unexpected value format of one field must never break the whole page.
                Log.Warn($"Camera settings field {f.Path} skipped: {ex.Message}");
                control = null;
            }
            if (control == null) continue;
            if (!seen.Add(f.Label + f.Kind) && f.Kind == FieldKind.Bool01) continue; // e.g. scheduleEnable vs schedule.enable
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5), TextWrapping = TextWrapping.Wrap };
            if (f.Hint != null)
            {
                label.ToolTip = f.Hint;
                label.Text += "  ⓘ";
            }
            Grid.SetRow(label, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(label);
            grid.Children.Add(control);
        }
        panel.Children.Add(grid);

        foreach (var path in s.SchedulePaths.Where(p => JsonUtil.Get(s.Edited, p) != null))
            panel.Children.Add(new ScheduleGrid(s, path, UpdateDirty));

        return Card(panel);
    }

    /// <summary>Range given as {min,max} for a selection field (e.g. FTP stream 0..2) → all values.</summary>
    private static IEnumerable<string>? MinMaxOptions(JsonNode? range)
    {
        if (JsonUtil.AsInt(JsonUtil.Get(range, "min")) is int lo && JsonUtil.AsInt(JsonUtil.Get(range, "max")) is int hi && hi >= lo && hi - lo <= 20)
            return Enumerable.Range(lo, hi - lo + 1).Select(i => i.ToString(CultureInfo.InvariantCulture));
        return null;
    }

    private static string Display(FieldSpec f, string value) =>
        f.Labels != null && f.Labels.TryGetValue(value, out var l) ? l
        : SettingsCatalog.ValueLabels.TryGetValue(value, out var g) ? g
        : value;

    private FrameworkElement? CreateControl(SettingsSection s, FieldSpec f)
    {
        var node = JsonUtil.Get(s.Edited, f.Path);
        if (node is not JsonValue current) return null; // not reported by this camera
        var range = JsonUtil.GetRange(s.Range, f.Path);

        void Write(JsonNode value)
        {
            JsonUtil.Set(s.Edited, f.Path, value);
            if (f.RebuildOnChange && _current != null) Dispatcher.BeginInvoke(() => RenderPage(_current));
            UpdateDirty();
        }

        switch (f.Kind)
        {
            case FieldKind.ReadOnly:
                return new TextBlock { Text = JsonUtil.AsString(current) ?? "", VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("DimText"), TextWrapping = TextWrapping.Wrap };

            case FieldKind.Bool01:
            {
                bool isBool = current.TryGetValue<bool>(out var bv);
                var cb = new CheckBox { IsChecked = isBool ? bv : JsonUtil.AsInt(current) == 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 0, 5) };
                cb.Click += (_, _) => Write(isBool ? JsonValue.Create(cb.IsChecked == true) : JsonValue.Create(cb.IsChecked == true ? 1 : 0));
                return cb;
            }

            case FieldKind.Enum:
            case FieldKind.IntEnum:
            {
                var value = JsonUtil.AsString(current) ?? "";
                var options = (f.OptionsProvider?.Invoke(s)
                               ?? (range as JsonArray)?.Select(JsonUtil.AsString).Where(o => o != null).Cast<string>()
                               ?? MinMaxOptions(range)
                               ?? f.Options
                               ?? Enumerable.Empty<string>()).ToList();
                if (!options.Contains(value)) options.Insert(0, value);
                var combo = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                foreach (var o in options) combo.Items.Add(new ComboBoxItem { Content = Display(f, o), Tag = o });
                combo.SelectedIndex = options.IndexOf(value);
                bool isNumber = f.Kind == FieldKind.IntEnum || current.TryGetValue<int>(out _);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not ComboBoxItem { Tag: string v }) return;
                    Write(isNumber && int.TryParse(v, out var iv) ? JsonValue.Create(iv) : JsonValue.Create(v));
                };
                return combo;
            }

            case FieldKind.Int when range is JsonArray list:
            {
                // The camera only allows certain values (e.g. FTP interval 15/30/60 …) → drop-down.
                var value = JsonUtil.AsString(current) ?? "";
                var options = list.Select(JsonUtil.AsString).OfType<string>().ToList();
                if (!options.Contains(value)) options.Insert(0, value);
                var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                foreach (var o in options) combo.Items.Add(new ComboBoxItem { Content = Display(f, o), Tag = o });
                combo.SelectedIndex = options.IndexOf(value);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string v } && int.TryParse(v, out var iv)) Write(JsonValue.Create(iv));
                };
                return combo;
            }

            case FieldKind.Int:
            {
                int value = JsonUtil.AsInt(current) ?? 0;
                int? min = JsonUtil.AsInt(JsonUtil.Get(range, "min")) ?? f.Min;
                int? max = JsonUtil.AsInt(JsonUtil.Get(range, "max")) ?? f.Max;
                if (min is int lo && max is int hi && hi > lo && hi - lo <= 1000)
                {
                    var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                    var slider = new Slider { Minimum = lo, Maximum = hi, Value = Math.Clamp(value, lo, hi), Width = 260, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
                    var text = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), Width = 50, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    slider.ValueChanged += (_, _) =>
                    {
                        text.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
                        Write(JsonValue.Create((int)slider.Value));
                    };
                    p.Children.Add(slider);
                    p.Children.Add(text);
                    return p;
                }
                var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                box.TextChanged += (_, _) =>
                {
                    if (int.TryParse(box.Text, out var v) && (min == null || v >= min) && (max == null || v <= max))
                    {
                        box.ClearValue(BorderBrushProperty);
                        Write(JsonValue.Create(v));
                    }
                    else box.BorderBrush = (Brush)FindResource("ErrorBrush");
                };
                return box;
            }

            case FieldKind.Text:
            {
                var box = new TextBox { Text = JsonUtil.AsString(current) ?? "", Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                if (JsonUtil.AsInt(JsonUtil.Get(range, "maxLen")) is int maxLen && maxLen > 0) box.MaxLength = maxLen;
                box.TextChanged += (_, _) => Write(JsonValue.Create(box.Text));
                return box;
            }

            case FieldKind.Password:
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                var box = new PasswordBox { Width = 240 };
                box.PasswordChanged += (_, _) =>
                {
                    if (box.Password.Length > 0) Write(JsonValue.Create(box.Password));
                    else { JsonUtil.Set(s.Edited, f.Path, JsonUtil.Get(s.Original, f.Path)?.DeepClone()); UpdateDirty(); }
                };
                p.Children.Add(box);
                p.Children.Add(new TextBlock { Text = "leer = unverändert", Style = (Style)FindResource("DimText"), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                return p;
            }
        }
        return null;
    }

    // =====================================================================
    // Apply / revert / reload
    // =====================================================================

    private void UpdateDirty()
    {
        bool any = false;
        foreach (var (page, dot) in _dirtyMarks)
        {
            bool dirty = page.Sections.Any(s => s.IsDirty);
            dot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            any |= dirty;
        }
        ApplyButton.IsEnabled = any && !_busy;
        RevertButton.IsEnabled = any && !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
        ReloadButton.IsEnabled = !busy && _session != null;
        UpdateDirty();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_session == null || _busy) return;
        SetBusy(true);
        var errors = new List<string>();
        int ok = 0;
        try
        {
            foreach (var s in AllSections.Where(s => s.IsDirty).ToList())
            {
                StatusText.Text = $"Speichere {s.Title} …";
                var param = s.BuildSetParam();
                if (param == null || s.ActiveSetCmd == null) continue;
                var r = await _session.ExecuteAsync(s.ActiveSetCmd, param);
                if (!r.Ok)
                {
                    errors.Add($"{s.Title}: {r.ErrorDetail ?? "Fehler"}");
                    Log.Warn($"Camera {_camera.Host}: {s.ActiveSetCmd} failed: {r.ErrorDetail} ({r.RspCode})");
                    continue;
                }
                ok++;
                NotifyNameChange(s);
                await LoadSectionAsync(s); // read back what the camera actually stored
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        BuildNavigation();
        StatusText.Text = errors.Count == 0
            ? $"{ok} Bereich(e) gespeichert."
            : $"{ok} gespeichert, {errors.Count} fehlgeschlagen: {string.Join("; ", errors)}";
        if (errors.Count > 0)
            MessageBox.Show(this, "Einige Einstellungen wurden von der Kamera nicht übernommen:\n\n" + string.Join("\n", errors),
                "Kameraeinstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Keeps the OpenReo camera name in sync when the name in the camera was changed.</summary>
    private void NotifyNameChange(SettingsSection s)
    {
        string? name = null;
        if (s.ActiveGetCmd == "GetDevName" && _camera.Channel <= 1) name = JsonUtil.AsString(JsonUtil.Get(s.Edited, "DevName.name"));
        if (s.ActiveGetCmd == "GetOsd" && _camera.Channel > 1) name = JsonUtil.AsString(JsonUtil.Get(s.Edited, "Osd.osdChannel.name"));
        if (!string.IsNullOrWhiteSpace(name) && name != _camera.Name)
        {
            _onNameChanged?.Invoke(name);
            CameraNameText.Text = name;
            Title = $"Kameraeinstellungen – {name}";
        }
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        foreach (var s in AllSections) s.RevertChanges();
        if (_current != null) RenderPage(_current);
        UpdateDirty();
        StatusText.Text = "Änderungen verworfen.";
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_session == null || _busy) return;
        if (AllSections.Any(s => s.IsDirty) &&
            MessageBox.Show(this, "Nicht gespeicherte Änderungen verwerfen und neu laden?", "Kameraeinstellungen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SetBusy(true);
        try
        {
            foreach (var s in AllSections)
            {
                StatusText.Text = $"Lese {s.Title} …";
                await LoadSectionAsync(s);
            }
            BuildNavigation();
            StatusText.Text = "Einstellungen neu geladen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Neu laden fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Non-modal window: IsCancel alone does not close it.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AllSections.Any(s => s.IsDirty)) return;
        var r = MessageBox.Show(this, "Es gibt nicht übernommene Änderungen. Trotzdem schließen?", "Kameraeinstellungen",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) e.Cancel = true;
    }
}
