using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenReo.App.Dialogs;
using OpenReo.Core.Localization;

namespace OpenReo.App.CameraSettings;

/// <summary>
/// PTZ control (pan/tilt, zoom, focus, presets) via the Reolink "PtzCtrl" command.
/// Buttons move while pressed and send "Stop" on release.
/// </summary>
public sealed class PtzPanel : StackPanel
{
    private readonly SettingsContext _ctx;
    private readonly Slider _speed;
    private readonly ListBox _presets = new() { Height = 160, Margin = new Thickness(0, 6, 0, 0) };

    public PtzPanel(SettingsContext ctx)
    {
        _ctx = ctx;

        Children.Add(Header("Steuerung"));
        var pad = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++)
        {
            pad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            pad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        }
        void Add(string glyph, string op, int r, int c)
        {
            var b = MoveButton(glyph, op);
            Grid.SetRow(b, r);
            Grid.SetColumn(b, c);
            pad.Children.Add(b);
        }
        Add("↖", "LeftUp", 0, 0); Add("↑", "Up", 0, 1); Add("↗", "RightUp", 0, 2);
        Add("←", "Left", 1, 0); Add("→", "Right", 1, 2);
        Add("↙", "LeftDown", 2, 0); Add("↓", "Down", 2, 1); Add("↘", "RightDown", 2, 2);

        var side = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        side.Children.Add(Row("Zoom", MoveButton("−", "ZoomDec"), MoveButton("+", "ZoomInc")));
        side.Children.Add(Row("Fokus", MoveButton("−", "FocusDec"), MoveButton("+", "FocusInc")));
        _speed = new Slider { Minimum = 1, Maximum = 64, Value = 32, Width = 160, Margin = new Thickness(0, 10, 0, 0) };
        side.Children.Add(new TextBlock { Text = "Geschwindigkeit", Margin = new Thickness(0, 10, 0, 0), Style = (Style)Application.Current.Resources["DimText"] });
        side.Children.Add(_speed);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(pad);
        row.Children.Add(side);
        Children.Add(row);

        Children.Add(Header("Presets"));
        Children.Add(_presets);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var go = new Button { Content = "Anfahren", Margin = new Thickness(0, 0, 8, 0) };
        go.Click += async (_, _) =>
        {
            if (_presets.SelectedItem is ListBoxItem { Tag: int id })
                await Ptz("ToPos", new JsonObject { ["id"] = id });
        };
        var save = new Button { Content = Lang.T("Aktuelle Position als Preset speichern …") };
        save.Click += async (_, _) => await SavePresetAsync();
        buttons.Children.Add(go);
        buttons.Children.Add(save);
        Children.Add(buttons);

        Loaded += async (_, _) => await LoadPresetsAsync();
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text.ToUpperInvariant(), Style = (Style)Application.Current.Resources["SectionHeader"],
    };

    private static StackPanel Row(string label, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        p.Children.Add(new TextBlock { Text = label, Width = 60, VerticalAlignment = VerticalAlignment.Center });
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private Button MoveButton(string glyph, string op)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = 44, Height = 36, Margin = new Thickness(2), Padding = new Thickness(0),
        };
        b.PreviewMouseLeftButtonDown += async (_, e) =>
        {
            b.CaptureMouse();
            e.Handled = true;
            await Ptz(op);
        };
        b.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            b.ReleaseMouseCapture();
            e.Handled = true;
            await Ptz("Stop");
        };
        return b;
    }

    private async Task Ptz(string op, JsonObject? extra = null)
    {
        try
        {
            var p = new JsonObject { ["channel"] = _ctx.Channel, ["op"] = op, ["speed"] = (int)_speed.Value };
            if (extra != null) foreach (var (k, v) in extra) p[k] = v?.DeepClone();
            var r = await _ctx.Session.ExecuteAsync("PtzCtrl", p);
            if (!r.Ok) await _ctx.Report(Lang.F("PTZ-Befehl {0} fehlgeschlagen: {1}", op, r.ErrorDetail));
        }
        catch (Exception ex)
        {
            await _ctx.Report("PTZ: " + ex.Message);
        }
    }

    private async Task LoadPresetsAsync()
    {
        _presets.Items.Clear();
        try
        {
            var r = await _ctx.Session.ExecuteAsync("GetPtzPreset", new JsonObject { ["channel"] = _ctx.Channel });
            if (JsonUtil.Get(r.Value, "PtzPreset") is JsonArray list)
                foreach (var p in list.OfType<JsonObject>())
                {
                    if (JsonUtil.AsInt(p["enable"]) != 1) continue;
                    var id = JsonUtil.AsInt(p["id"]) ?? 0;
                    _presets.Items.Add(new ListBoxItem { Content = $"{id}: {JsonUtil.AsString(p["name"])}", Tag = id });
                }
            if (_presets.Items.Count == 0) _presets.Items.Add(new ListBoxItem { Content = Lang.T("(keine Presets gespeichert)"), IsEnabled = false });
        }
        catch (Exception ex)
        {
            await _ctx.Report("Presets: " + ex.Message);
        }
    }

    private async Task SavePresetAsync()
    {
        var name = InputDialog.Ask(_ctx.Owner, "Preset speichern", Lang.T("Name des Presets:"), "Position");
        if (string.IsNullOrWhiteSpace(name)) return;
        var used = _presets.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<int>().ToHashSet();
        int id = Enumerable.Range(1, 64).FirstOrDefault(i => !used.Contains(i));
        if (id == 0) { await _ctx.Report("Keine freie Preset-Nummer."); return; }
        var r = await _ctx.Session.ExecuteAsync("SetPtzPreset", new JsonObject
        {
            ["PtzPreset"] = new JsonObject { ["channel"] = _ctx.Channel, ["enable"] = 1, ["id"] = id, ["name"] = name.Trim() },
        });
        await _ctx.Report(r.Ok ? Lang.F("Preset {0} gespeichert.", id) : Lang.F("Preset konnte nicht gespeichert werden: {0}", r.ErrorDetail));
        await LoadPresetsAsync();
    }
}
