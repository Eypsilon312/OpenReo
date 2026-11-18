// OpenReo.UiTest – automated test of the camera settings window against a simulated Reolink API.
// Usage: OpenReo.UiTest.exe [screenshot-folder]
// Exit code = number of failed checks.

using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenReo.App.CameraSettings;
using OpenReo.Core.Models;
using OpenReo.UiTest;

internal static class Program
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) _failures++;
    }

    private static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            DoEvents();
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static void Screenshot(Window win, string? folder, string name)
    {
        if (folder == null) return;
        var root = (FrameworkElement)win.Content;
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(folder, name + ".png"));
        enc.Save(fs);
    }

    private static T Private<T>(object o, string field) =>
        (T)o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static string Sched(char c) => new(c, 168);

    [STAThread]
    private static int Main(string[] args)
    {
        OpenReo.Core.Localization.Lang.Initialize("de"); // the checks below use the German texts
        string? shots = args.Length > 0 ? args[0] : null;
        if (shots != null) Directory.CreateDirectory(shots);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // As in the real app: async continuations must return to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/OpenReo;component/Themes/Dark.xaml") });

        using var cam = new FakeReolinkCamera();

        // ---------------------------------------------------------------- wrong password
        {
            var cfg = new CameraConfig { Name = "Test", Host = "localhost", HttpPort = cam.Port, Username = "admin", Password = "falsch" };
            var w = new CameraSettingsWindow(cfg);
            w.Show();
            var loading = (TextBlock)w.FindName("LoadingText");
            var detail = (TextBlock)w.FindName("LoadingDetail");
            Check(WaitUntil(() => loading.Text.StartsWith("Keine Verbindung")), "Falsches Passwort: Fehlermeldung statt Absturz");
            Check(detail.Text.Contains("Anmeldung"), $"Falsches Passwort: Meldung nennt Anmeldung ({detail.Text.Split('\n')[0]})");
            Screenshot(w, shots, "00-falsches-passwort");
            w.Close();
        }

        // ---------------------------------------------------------------- load
        string? renamed = null;
        var camCfg = new CameraConfig { Name = "IPC_523128M8MP", Host = "localhost", HttpPort = cam.Port, Username = "admin", Password = "pw" };
        var win = new CameraSettingsWindow(camCfg, n => renamed = n);
        win.Show();
        var pageList = (ListBox)win.FindName("PageList");
        Check(WaitUntil(() => pageList.Items.Count > 0), "Einstellungen geladen");

        var titles = pageList.Items.OfType<ListBoxItem>().Select(i => ((SettingsPage)i.Tag).Title).ToList();
        Console.WriteLine("      Seiten: " + string.Join(", ", titles));
        foreach (var expected in new[] { "Allgemein", "Bild", "Anzeige (OSD)", "Streams", "Datum & Zeit", "Erkennung", "Aufnahme", "FTP", "E-Mail", "Push", "PTZ" })
            Check(titles.Contains(expected), $"Seite vorhanden: {expected}");

        var pages = Private<List<SettingsPage>>(win, "_pages");
        SettingsSection Sec(string getCmd) => pages.SelectMany(p => p.Sections).First(s => s.GetCmd == getCmd && s.Supported);
        Check(!pages.SelectMany(p => p.Sections).Any(s => s.GetCmd == "GetPowerLed" && s.Supported), "Nicht unterstützte Status-LED ausgeblendet");
        Check(!pages.SelectMany(p => p.Sections).Any(s => s.GetCmd == "GetWhiteLed" && s.Supported), "Nicht vorhandener Scheinwerfer ausgeblendet");
        var rec = pages.SelectMany(p => p.Sections).First(s => s.Title.StartsWith("Aufnahme"));
        Check(rec.ActiveGetCmd == "GetRec" && rec.ActiveSetCmd == "SetRec", "Aufnahme: Fallback auf alte Firmware-Befehle (GetRec/SetRec)");
        var ftp = Sec("GetFtpV20");
        Check(ftp.ActiveSetCmd == "SetFtpV20", "FTP: V20-Befehle erkannt");

        // ---------------------------------------------------------------- render every page
        int idx = 1;
        foreach (ListBoxItem item in pageList.Items)
        {
            pageList.SelectedItem = item;
            DoEvents();
            var content = (StackPanel)win.FindName("ContentPanel");
            Check(content.Children.Count > 1, $"Seite „{((SettingsPage)item.Tag).Title}“ hat Inhalt ({content.Children.Count} Elemente)");
            Screenshot(win, shots, $"{idx++:00}-{((SettingsPage)item.Tag).Title.Replace(' ', '_').Replace('&', 'u').Replace("(", "").Replace(")", "")}");
        }

        // Encoder options come from the camera's range (depend on the main resolution).
        pageList.SelectedItem = pageList.Items.OfType<ListBoxItem>().First(i => ((SettingsPage)i.Tag).Title == "Streams");
        DoEvents();
        var combos = FindAll<ComboBox>((DependencyObject)win.FindName("ContentPanel")).ToList();
        var sizeCombo = combos.FirstOrDefault(c => c.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == "2560*1440"));
        Check(sizeCombo != null && sizeCombo.Items.Count == 2, "Streams: Auflösungen aus der Kamera-Range (3840*2160, 2560*1440)");

        // PTZ presets
        pageList.SelectedItem = pageList.Items.OfType<ListBoxItem>().First(i => ((SettingsPage)i.Tag).Title == "PTZ");
        WaitUntil(() => FindAll<ListBoxItem>((DependencyObject)win.FindName("ContentPanel")).Any(i => (i.Content as string)?.Contains("Tor") == true), 3000);
        var presetItems = FindAll<ListBoxItem>((DependencyObject)win.FindName("ContentPanel")).Select(i => i.Content as string).ToList();
        Check(presetItems.Contains("1: Tor") && presetItems.Count == 1, "PTZ: nur aktive Presets gelistet (1: Tor)");

        // ---------------------------------------------------------------- edit via the real UI control (Bild → Tag/Nacht)
        pageList.SelectedItem = pageList.Items.OfType<ListBoxItem>().First(i => ((SettingsPage)i.Tag).Title == "Bild");
        DoEvents();
        var dayNight = FindAll<ComboBox>((DependencyObject)win.FindName("ContentPanel"))
            .First(c => c.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == "Black&White"));
        dayNight.SelectedItem = dayNight.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "Color");
        DoEvents();

        // ---------------------------------------------------------------- edits on the data model
        JsonUtil.Set(ftp.Edited, "Ftp.schedule.table.MD", Sched('0'));
        JsonUtil.Set(Sec("GetEnc").Edited, "Enc.mainStream.bitRate", 8192);
        JsonUtil.Set(Sec("GetDevName").Edited, "DevName.name", "Garage");
        var people = pages.SelectMany(p => p.Sections).First(s => s.Title.Contains("Personen"));
        JsonUtil.Set(people.Edited, "AiAlarm.sensitivity", 75);
        win.GetType().GetMethod("UpdateDirty", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(win, null);

        var apply = (Button)win.FindName("ApplyButton");
        Check(apply.IsEnabled, "Änderungen erkannt (Übernehmen aktiv)");
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WaitUntil(() => cam.SetCalls.Count >= 5 && !Private<bool>(win, "_busy"));

        string P(string cmd) => cam.SetCalls.FirstOrDefault(c => c.Cmd == cmd).Param?.ToJsonString() ?? "(nicht gesendet)";
        bool Same(string cmd, string expected) => JsonNode.DeepEquals(JsonNode.Parse(P(cmd) == "(nicht gesendet)" ? "null" : P(cmd)), JsonNode.Parse(expected));

        Check(Same("SetIsp", """{"Isp":{"dayNight":"Color","channel":0}}"""), $"SetIsp sendet nur die Änderung: {P("SetIsp")}");
        Check(Same("SetFtpV20", """{"Ftp":{"schedule":{"table":{"MD":"__S0__"},"channel":0}}}""".Replace("__S0__", Sched('0'))), "SetFtpV20 sendet nur den geänderten Zeitplan");
        Check(Same("SetEnc", """{"Enc":{"mainStream":{"bitRate":8192},"channel":0}}"""), $"SetEnc: {P("SetEnc")}");
        Check(Same("SetDevName", """{"DevName":{"name":"Garage"}}"""), $"SetDevName: {P("SetDevName")}");
        Check(Same("SetAiAlarm", """{"AiAlarm":{"sensitivity":75,"ai_type":"people"},"channel":0}"""), $"SetAiAlarm: {P("SetAiAlarm")}");
        Check(cam.SetCalls.Count == 5, $"Keine unnötigen Set-Befehle (gesendet: {string.Join(", ", cam.SetCalls.Select(c => c.Cmd))})");
        Check(renamed == "Garage", "Neuer Gerätename wird in OpenReo übernommen");
        Check(!pages.SelectMany(p => p.Sections).Any(s => s.IsDirty), "Nach dem Speichern nichts mehr als geändert markiert");
        Screenshot(win, shots, "99-nach-speichern");

        // ---------------------------------------------------------------- close → logout
        var closeButton = FindAll<Button>((FrameworkElement)win.Content).First(b => (b.Content as string) == "Schließen");
        closeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(WaitUntil(() => !win.IsVisible, 3000), "„Schließen“ schließt das Fenster");
        Check(WaitUntil(() => cam.Logouts >= 1, 5000), $"Sitzungen abgemeldet (Logins {cam.Logins}, Logouts {cam.Logouts})");

        // ---------------------------------------------------------------- older firmware (real camera format)
        using (var old = new FakeReolinkCamera(legacy: true))
        {
            var cfg = new CameraConfig { Name = "Legacy-Kamera", Host = "localhost", HttpPort = old.Port, Username = "admin", Password = "pw" };
            var w = new CameraSettingsWindow(cfg);
            w.Show();
            var list = (ListBox)w.FindName("PageList");
            Check(WaitUntil(() => list.Items.Count > 0), "Alte Firmware: Einstellungen geladen");
            var oldPages = Private<List<SettingsPage>>(w, "_pages");
            var ftpOld = oldPages.SelectMany(p => p.Sections).First(s => s.Title == "FTP-Upload");
            Check(ftpOld.ActiveSetCmd == "SetFtp", "Alte Firmware: FTP über GetFtp/SetFtp");

            list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => ((SettingsPage)i.Tag).Title == "FTP");
            DoEvents();
            var content = (DependencyObject)w.FindName("ContentPanel");
            int orange = FindAll<Border>(content).Count(b => b.Background is SolidColorBrush { Color: { R: 0xF5, G: 0x9E, B: 0x0B } } && b.Width is double.NaN);
            Check(orange >= 168, $"FTP-Plan „Bewegung“ (Wert 2) wird orange angezeigt ({orange} Felder)");
            var interval = FindAll<ComboBox>(content).FirstOrDefault(c => c.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == "1800"));
            Check(interval != null, "FTP-Intervall als Auswahl aus der Kamera-Liste (15 … 1800 s)");
            var stream = FindAll<ComboBox>(content).FirstOrDefault(c => c.Items.OfType<ComboBoxItem>().Any(i => (string)i.Content == "Ext-Stream (Mid)"));
            Check(stream != null, "FTP-Stream: drei Optionen aus Range {min 0, max 2}");
            Screenshot(w, shots, "20-alte-firmware-ftp");

            // Unrelated change must not touch the schedule.
            interval!.SelectedItem = interval.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "60");
            DoEvents();
            var applyOld = (Button)w.FindName("ApplyButton");
            applyOld.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WaitUntil(() => old.SetCalls.Count >= 1 && !Private<bool>(w, "_busy"));
            string Param(string cmd) => old.SetCalls.LastOrDefault(c => c.Cmd == cmd).Param?.ToJsonString() ?? "null";
            Check(JsonNode.DeepEquals(JsonNode.Parse(Param("SetFtp")), JsonNode.Parse("""{"Ftp":{"interval":60}}""")), $"Intervall-Änderung sendet nur das Intervall: {Param("SetFtp")}");

            // Paint with mode "Dauerhaft": Mon–Fri timer, weekend off.
            DoEvents();
            var modeBox = FindAll<ComboBox>(content).First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is char));
            modeBox.SelectedItem = modeBox.Items.OfType<ComboBoxItem>().First(i => i.Tag is '1');
            var workdays = FindAll<Button>(content).First(b => (b.Content as string)?.StartsWith("Mo–Fr") == true);
            workdays.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DoEvents();
            applyOld.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WaitUntil(() => old.SetCalls.Count >= 2 && !Private<bool>(w, "_busy"));
            var expected = new string('0', 24) + new string('1', 24 * 5) + new string('0', 24);
            Check(JsonNode.DeepEquals(JsonNode.Parse(Param("SetFtp")), JsonNode.Parse($"{{\"Ftp\":{{\"schedule\":{{\"table\":\"{expected}\"}}}}}}")),
                "Zeitplan „Dauerhaft Mo–Fr“ wird korrekt als 0/1-Plan gesendet");
            w.Close();
        }

        // ---------------------------------------------------------------- audio chain (optional: args[1] = video file with audio track)
        if (args.Length > 1 && File.Exists(args[1]))
        {
            OpenReo.Video.FFmpegLoader.Initialize();
            using var streams = new OpenReo.Video.StreamManager(new OpenReo.Video.StreamOptions(HardwareAccelerationMode.Auto, RtspTransport.Tcp, true, 10, 10), TimeSpan.Zero);
            var audioCam = new CameraConfig { Name = "Ton-Test", Username = "", ClearPath = args[1], MidPath = args[1], LowPath = args[1], Quality = StreamQuality.Mid };
            var lease = streams.Acquire(audioCam, StreamQuality.Mid);
            lease.SetTargetSize(640, 360);
            using var audio = new OpenReo.App.Audio.AudioController(streams);
            audio.Player.Muted = true; // test silently
            audio.SetCamera(audioCam.Id);
            audio.Update(id => id == audioCam.Id ? audioCam : null);
            Check(lease.Stream.AudioSink == audio.Player, "Ton: Player hängt am Stream der gewählten Kamera");
            var buffer = (NAudio.Wave.BufferedWaveProvider)typeof(OpenReo.App.Audio.AudioPlayer).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio.Player)!;
            Check(WaitUntil(() => buffer.BufferedBytes > 0, 8000), $"Ton: PCM-Daten kommen an (Codec {lease.Stream.AudioCodec})");
            var output = (NAudio.Wave.WaveOutEvent?)typeof(OpenReo.App.Audio.AudioPlayer).GetField("_output", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio.Player);
            Check(output?.PlaybackState == NAudio.Wave.PlaybackState.Playing, $"Ton: Ausgabe läuft ({output?.PlaybackState})");
            Thread.Sleep(1500);
            Check(buffer.BufferedDuration <= TimeSpan.FromMilliseconds(600), $"Ton: Latenz begrenzt (Puffer {buffer.BufferedDuration.TotalMilliseconds:0} ms)");
            audio.Toggle(audioCam.Id);
            audio.Update(id => id == audioCam.Id ? audioCam : null);
            Check(lease.Stream.AudioSink == null && output?.PlaybackState != NAudio.Wave.PlaybackState.Playing, "Ton aus: Stream entkoppelt, Ausgabe gestoppt");
            lease.Release(true);
        }
        else Console.WriteLine("SKIP  Ton-Test (keine Testdatei mit Audiospur angegeben)");

        // ---------------------------------------------------------------- English UI
        OpenReo.Core.Localization.Lang.Initialize("en");
        using (var enCam = new FakeReolinkCamera(legacy: true))
        {
            var cfg = new CameraConfig { Name = "Gate", Host = "localhost", HttpPort = enCam.Port, Username = "admin", Password = "pw" };
            var w = new CameraSettingsWindow(cfg);
            w.Show();
            var list = (ListBox)w.FindName("PageList");
            Check(WaitUntil(() => list.Items.Count > 0), "English: settings loaded");
            var german = new List<string>();
            int i = 1;
            foreach (ListBoxItem item in list.Items)
            {
                list.SelectedItem = item;
                DoEvents();
                var texts = FindAll<TextBlock>((FrameworkElement)w.Content).Select(t => t.Text)
                    .Concat(FindAll<ContentControl>((FrameworkElement)w.Content).Select(c => c.Content as string))
                    .Where(t => !string.IsNullOrEmpty(t)).ToList();
                german.AddRange(texts!.Where(t => t!.IndexOfAny(new[] { 'ä', 'ö', 'ü', 'Ä', 'Ö', 'Ü', 'ß', '„' }) >= 0)!);
                Screenshot(w, shots, $"en-{i++:00}");
            }
            Check(german.Count == 0, "English: no German text visible" + (german.Count > 0 ? ": " + string.Join(" | ", german.Distinct().Take(8)) : ""));
            var enTitles = list.Items.OfType<ListBoxItem>().Select(x => ((SettingsPage)x.Tag).Title).ToList();
            Check(enTitles.Contains("General") && enTitles.Contains("Recording") && enTitles.Contains("E-mail"), "English: page titles translated (" + string.Join(", ", enTitles) + ")");
            w.Close();
        }
        OpenReo.Core.Localization.Lang.Initialize("de");

        Console.WriteLine(_failures == 0 ? "\nALLE TESTS BESTANDEN" : $"\n{_failures} TEST(S) FEHLGESCHLAGEN");
        app.Shutdown();
        return _failures;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindAll<T>(child)) yield return c;
        }
    }
}
