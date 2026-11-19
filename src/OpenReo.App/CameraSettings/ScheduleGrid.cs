using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenReo.Core.Localization;

namespace OpenReo.App.CameraSettings;

/// <summary>
/// Weekly schedule editor (7 days x 24 hours).
///
/// Reolink stores a schedule as a string of 168 characters, one per hour. Two formats exist:
/// - Older firmware: one string; each character is a MODE: '0' off, '1' motion/alarm (blue in the Reolink app),
///   '2' motion/alarm (orange in the Reolink app). Recording and FTP use all three, e-mail/push use 0/1.
/// - Newer firmware (V20): one 0/1 string per trigger (MD, TIMING, AI_PEOPLE ...).
/// Unknown characters are preserved – only the cells the user paints are changed.
/// </summary>
public sealed class ScheduleGrid : StackPanel
{
    private static readonly string[] Days = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };
    private static readonly Dictionary<string, string> TriggerLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TIMING"] = "Dauerhaft (Zeitplan)", ["MD"] = "Bewegung", ["AI_PEOPLE"] = "Person", ["AI_VEHICLE"] = "Fahrzeug",
        ["AI_DOG_CAT"] = "Tier", ["AI_FACE"] = "Gesicht", ["AI_PACKAGE"] = "Paket", ["VISITOR"] = "Besucher (Klingel)",
        ["AI_OTHER"] = "Sonstiges", ["ALARM_IN"] = "Alarmeingang",
    };

    private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x39, 0x46));
    // Colors as in the Reolink app: motion = blue, timer (continuous) = orange.
    private static readonly Brush MotionBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x8C, 0xFF));
    private static readonly Brush TimerBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush OtherBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7));

    private readonly SettingsSection _section;
    private readonly Action _changed;
    private readonly Border[,] _cells = new Border[7, 24];
    private readonly char[] _values = new char[168];
    private readonly ComboBox _modeBox = new() { Width = 200, Margin = new Thickness(8, 0, 0, 0) };
    private string _currentPath;
    private bool _painting;
    private char _paintValue;

    /// <param name="modes">Old format: allow timer + motion modes (recording / FTP).</param>
    public ScheduleGrid(SettingsSection section, string path, Action changed)
    {
        _section = section;
        _changed = changed;
        _currentPath = path;
        Margin = new Thickness(0, 12, 0, 0);

        var node = JsonUtil.Get(section.Edited, path);
        var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = "Wochenplan", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        Children.Add(header);

        if (node is JsonObject triggers)
        {
            // V20: one on/off plan per trigger.
            var keys = triggers.Where(kv => IsSchedule(kv.Value)).Select(kv => kv.Key).ToList();
            if (keys.Count == 0) { Children.Add(Info(Lang.T("Kein bearbeitbarer Zeitplan vorhanden."))); return; }
            var combo = new ComboBox { Width = 200, Margin = new Thickness(8, 0, 16, 0) };
            foreach (var k in keys)
            {
                var plan = JsonUtil.AsString(triggers[k]) ?? "";
                int on = plan.Count(c => c != '0');
                combo.Items.Add(new ComboBoxItem { Content = $"{(TriggerLabels.TryGetValue(k, out var l) ? Lang.T(l) : k)}  ({on} h)", Tag = k });
            }
            header.Children.Add(Dim(Lang.T("Auslöser:")));
            header.Children.Add(combo);
            SetModes(('1', "An"));
            BuildGrid();
            combo.SelectionChanged += (_, _) =>
            {
                _currentPath = $"{path}.{((ComboBoxItem)combo.SelectedItem).Tag}";
                Load();
            };
            // Start with the first trigger that actually has active hours.
            int first = keys.FindIndex(k => (JsonUtil.AsString(triggers[k]) ?? "").Any(c => c != '0'));
            combo.SelectedIndex = Math.Max(0, first);
            AddLegend(("An", MotionBrush), ("Aus", OffBrush));
        }
        else if (IsSchedule(node))
        {
            // Old format: one plan, characters are modes.
            bool withModes = path.StartsWith("Rec.", StringComparison.Ordinal) || path.StartsWith("Ftp.", StringComparison.Ordinal)
                             || (JsonUtil.AsString(node) ?? "").Contains('2');
            if (withModes)
            {
                // '2' = timer (continuous, orange), '1' = motion/alarm (blue) – as in the Reolink app.
                SetModes(('2', Lang.T("Dauerhaft (Timer)")), ('1', Lang.T("Bewegung (Alarm)")));
                AddLegend(("Dauerhaft (Timer)", TimerBrush), ("Bewegung (Alarm)", MotionBrush), ("Aus", OffBrush));
            }
            else
            {
                SetModes(('1', "An"));
                AddLegend(("An", MotionBrush), ("Aus", OffBrush));
            }
            BuildGrid();
            Load();
        }
        else
        {
            Children.Add(Info(Lang.T("Zeitplan-Format dieser Kamera wird nicht unterstützt – bitte in der Weboberfläche einstellen.")));
            return;
        }

        if (_modeBox.Items.Count > 1)
        {
            header.Children.Add(Dim(Lang.T("Malen mit:")));
            header.Children.Add(_modeBox);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Button(Lang.T("Alles mit gewähltem Modus"), () => Fill(_ => PaintChar())));
        buttons.Children.Add(Button(Lang.T("Alles aus"), () => Fill(_ => '0')));
        buttons.Children.Add(Button(Lang.T("Mo–Fr an, Wochenende aus"), () => Fill(d => d is >= 1 and <= 5 ? PaintChar() : '0')));
        Children.Add(buttons);
        Children.Add(Info(Lang.T("Stunden anklicken oder mit gedrückter Maustaste überstreichen (ein erneuter Klick schaltet aus). ") +
                          Lang.T("Reihenfolge der Tage wie in der Reolink-Oberfläche, beginnend mit Sonntag.")));
    }

    private static bool IsSchedule(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 168;

    private void SetModes(params (char Value, string Label)[] modes)
    {
        foreach (var (value, label) in modes)
            _modeBox.Items.Add(new ComboBoxItem { Content = Lang.T(label), Tag = value });
        _modeBox.SelectedIndex = 0;
    }

    private char PaintChar() => _modeBox.SelectedItem is ComboBoxItem { Tag: char c } ? c : '1';

    private void AddLegend(params (string Label, Brush Brush)[] items)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (label, brush) in items)
        {
            legend.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = Lang.T(label), FontSize = 11.5, Margin = new Thickness(0, 0, 14, 0), Style = (Style)Application.Current.Resources["DimText"] });
        }
        Children.Add(legend);
    }

    private static TextBlock Dim(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["DimText"],
    };

    private static TextBlock Info(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0),
        Style = (Style)Application.Current.Resources["DimText"],
    };

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 4, 10, 4) };
        b.Click += (_, _) => click();
        return b;
    }

    private static Brush BrushFor(char c) => c switch
    {
        '0' => OffBrush,
        '1' => MotionBrush,
        '2' => TimerBrush,
        _ => OtherBrush,
    };

    private void BuildGrid()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        for (int h = 0; h < 24; h++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        for (int r = 0; r < 8; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r == 0 ? 16 : 17) });

        for (int h = 0; h < 24; h += 3)
        {
            var t = new TextBlock { Text = h.ToString(), FontSize = 10, Style = (Style)Application.Current.Resources["DimText"] };
            Grid.SetColumn(t, h + 1);
            Grid.SetColumnSpan(t, 3);
            grid.Children.Add(t);
        }
        for (int d = 0; d < 7; d++)
        {
            var label = new TextBlock { Text = Lang.T(Days[d]), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["DimText"] };
            Grid.SetRow(label, d + 1);
            grid.Children.Add(label);
            for (int h = 0; h < 24; h++)
            {
                var cell = new Border { Margin = new Thickness(1), CornerRadius = new CornerRadius(2), Background = OffBrush, Tag = (d, h), Cursor = Cursors.Hand };
                Grid.SetRow(cell, d + 1);
                Grid.SetColumn(cell, h + 1);
                grid.Children.Add(cell);
                _cells[d, h] = cell;
            }
        }

        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Border { Tag: ValueTuple<int, int> pos }) return;
            _painting = true;
            var mode = PaintChar();
            // Clicking a cell that already has the selected mode switches it off.
            _paintValue = _values[pos.Item1 * 24 + pos.Item2] == mode ? '0' : mode;
            SetCell(pos.Item1, pos.Item2, _paintValue);
            grid.CaptureMouse();
        };
        grid.MouseMove += (_, e) =>
        {
            if (!_painting) return;
            if (grid.InputHitTest(e.GetPosition(grid)) is Border { Tag: ValueTuple<int, int> pos })
                SetCell(pos.Item1, pos.Item2, _paintValue);
        };
        grid.MouseLeftButtonUp += (_, _) =>
        {
            if (!_painting) return;
            _painting = false;
            grid.ReleaseMouseCapture();
            Save();
        };
        Children.Add(grid);
    }

    private void Load()
    {
        var s = JsonUtil.AsString(JsonUtil.Get(_section.Edited, _currentPath));
        if (s == null || s.Length != 168) s = new string('0', 168);
        for (int i = 0; i < 168; i++)
        {
            _values[i] = s[i];
            _cells[i / 24, i % 24].Background = BrushFor(s[i]);
        }
    }

    private void SetCell(int d, int h, char value)
    {
        _values[d * 24 + h] = value;
        _cells[d, h].Background = BrushFor(value);
    }

    private void Save()
    {
        JsonUtil.Set(_section.Edited, _currentPath, new string(_values));
        _changed();
    }

    private void Fill(Func<int, char> valueForDay)
    {
        for (int d = 0; d < 7; d++)
        {
            var v = valueForDay(d);
            for (int h = 0; h < 24; h++) SetCell(d, h, v);
        }
        Save();
    }
}
