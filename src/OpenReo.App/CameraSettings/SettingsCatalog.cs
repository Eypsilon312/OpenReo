using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;

namespace OpenReo.App.CameraSettings;

/// <summary>
/// Definition of all camera settings OpenReo can edit via the Reolink HTTP API.
/// Fields that a camera does not report are skipped automatically, sections the camera does
/// not support are hidden – so one definition covers all models / firmware generations.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>German display names for common option values reported by Reolink cameras.</summary>
    public static readonly Dictionary<string, string> ValueLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto"] = "Automatisch", ["Color"] = "Farbe", ["Black&White"] = "Schwarzweiß",
        ["Off"] = "Aus", ["On"] = "An", ["Outdoor"] = "Außen (kein Flimmerschutz)",
        ["50HZ"] = "50 Hz", ["60HZ"] = "60 Hz", ["LowNoise"] = "Rauscharm", ["Anti-Smearing"] = "Gegen Bewegungsunschärfe",
        ["Manual"] = "Manuell", ["DynamicRangeControl"] = "Dynamikbereich (DRC/WDR)", ["BackLightControl"] = "Gegenlichtkompensation",
        ["Upper Left"] = "Oben links", ["Top Center"] = "Oben Mitte", ["Upper Right"] = "Oben rechts",
        ["Lower Left"] = "Unten links", ["Bottom Center"] = "Unten Mitte", ["Lower Right"] = "Unten rechts",
        ["Other Configuration"] = "Benutzerdefiniert",
        ["picture"] = "Bild", ["video"] = "Video", ["onlyPicture"] = "Nur Bild", ["onlyText"] = "Nur Text", ["picAndVideo"] = "Bild und Video",
    };

    public static List<SettingsPage> Create(int ch)
    {
        JsonObject P() => new() { ["channel"] = ch };

        static FieldSpec B(string path, string label, string? hint = null) => new(path, label, FieldKind.Bool01) { Hint = hint };
        static FieldSpec E(string path, string label, string? hint = null) => new(path, label, FieldKind.Enum) { Hint = hint };
        static FieldSpec I(string path, string label, int? min = null, int? max = null, string? hint = null) => new(path, label, FieldKind.Int) { Min = min, Max = max, Hint = hint };
        static FieldSpec T(string path, string label, string? hint = null) => new(path, label, FieldKind.Text) { Hint = hint };
        static FieldSpec R(string path, string label) => new(path, label, FieldKind.ReadOnly);

        var pages = new List<SettingsPage>();

        // ------------------------------------------------------------------ General
        pages.Add(new SettingsPage
        {
            Title = "Allgemein", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Geräteinformationen", GetCmd = "GetDevInfo",
                    Fields =
                    {
                        R("DevInfo.model", "Modell"), R("DevInfo.type", "Gerätetyp"), R("DevInfo.firmVer", "Firmware"),
                        R("DevInfo.hardVer", "Hardware"), R("DevInfo.serial", "Seriennummer"), R("DevInfo.channelNum", "Kanäle"),
                        R("DevInfo.buildDay", "Build"), R("DevInfo.detail", "Details"),
                    },
                },
                new SettingsSection
                {
                    Title = "Gerätename", GetCmd = "GetDevName", SetCmd = "SetDevName", GetParam = P(),
                    Fields = { T("DevName.name", "Name", "Bei einem NVR ist das der Name des Rekorders – der Kanalname steht unter „Anzeige“.") },
                },
                new SettingsSection
                {
                    Title = "Status-LED", GetCmd = "GetPowerLed", SetCmd = "SetPowerLed", GetParam = P(),
                    Fields = { new FieldSpec("PowerLed.state", "Status-LED", FieldKind.Enum) { Options = new[] { "On", "Off" } } },
                },
                new SettingsSection
                {
                    Title = "Speicherkarte / Festplatte", GetCmd = "GetHddInfo",
                    Fields =
                    {
                        R("HddInfo.0.capacity", "Kapazität (MB)"), R("HddInfo.0.size", "Belegt/Größe laut Gerät (MB)"),
                        R("HddInfo.0.format", "Formatiert (1 = ja)"), R("HddInfo.0.mount", "Eingebunden (1 = ja)"),
                    },
                    Note = "Formatieren ist bewusst nur über die Weboberfläche bzw. Reolink-App möglich.",
                },
            },
            Actions =
            {
                new SettingsAction("Weboberfläche öffnen", ctx =>
                {
                    Process.Start(new ProcessStartInfo(ctx.Session.BaseUrl) { UseShellExecute = true });
                    return Task.CompletedTask;
                }),
                new SettingsAction("Sirene testen", async ctx =>
                {
                    var r = await ctx.Session.ExecuteAsync("AudioAlarmPlay", new JsonObject
                    {
                        ["channel"] = ctx.Channel, ["alarm_mode"] = "times", ["manual_switch"] = 0, ["times"] = 1,
                    });
                    await ctx.Report(r.Ok ? "Sirene ausgelöst." : $"Sirene nicht verfügbar: {r.ErrorDetail}");
                }),
                new SettingsAction("Kamera neu starten", async ctx =>
                {
                    if (MessageBox.Show(ctx.Owner, "Kamera jetzt neu starten? Die Verbindung ist für ca. 1 Minute unterbrochen.", "Neustart",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    var r = await ctx.Session.ExecuteAsync("Reboot", new JsonObject());
                    await ctx.Report(r.Ok ? "Neustart ausgelöst." : $"Neustart fehlgeschlagen: {r.ErrorDetail}");
                }, Destructive: true),
            },
        });

        // ------------------------------------------------------------------ Image
        pages.Add(new SettingsPage
        {
            Title = "Bild", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Bildeinstellungen", GetCmd = "GetImage", SetCmd = "SetImage", GetParam = P(),
                    Fields =
                    {
                        I("Image.bright", "Helligkeit", 0, 255), I("Image.contrast", "Kontrast", 0, 255),
                        I("Image.saturation", "Sättigung", 0, 255), I("Image.hue", "Farbton", 0, 255), I("Image.sharpen", "Schärfe", 0, 255),
                    },
                },
                new SettingsSection
                {
                    Title = "Belichtung & Tag/Nacht", GetCmd = "GetIsp", SetCmd = "SetIsp", GetParam = P(),
                    Fields =
                    {
                        E("Isp.dayNight", "Tag/Nacht-Modus"), I("Isp.dayNightThreshold", "Tag/Nacht-Schwelle"),
                        E("Isp.antiFlicker", "Flimmerschutz"), E("Isp.exposure", "Belichtung"),
                        E("Isp.backLight", "Gegenlicht"), I("Isp.blc", "Gegenlicht-Stärke (BLC)"), I("Isp.drc", "Dynamikbereich (DRC)"),
                        E("Isp.whiteBalance", "Weißabgleich"), I("Isp.redGain", "Rotverstärkung"), I("Isp.blueGain", "Blauverstärkung"),
                        B("Isp.nr3d", "3D-Rauschunterdrückung"), B("Isp.mirroring", "Bild spiegeln"), B("Isp.rotation", "Bild drehen (180°)"),
                    },
                },
                new SettingsSection
                {
                    Title = "Infrarot", GetCmd = "GetIrLights", SetCmd = "SetIrLights", GetParam = P(),
                    Fields = { new FieldSpec("IrLights.state", "Infrarot-LEDs", FieldKind.Enum) { Options = new[] { "Auto", "On", "Off" } } },
                },
                new SettingsSection
                {
                    Title = "Scheinwerfer", GetCmd = "GetWhiteLed", SetCmd = "SetWhiteLed", GetParam = P(),
                    Fields =
                    {
                        B("WhiteLed.state", "Scheinwerfer jetzt an"),
                        new FieldSpec("WhiteLed.mode", "Modus", FieldKind.IntEnum)
                        {
                            Options = new[] { "0", "1", "2", "3" },
                            Labels = new() { ["0"] = "Aus", ["1"] = "Automatisch (Nacht/Bewegung)", ["2"] = "Modus 2", ["3"] = "Zeitplan" },
                            Hint = "Die Modusnummern unterscheiden sich je nach Modell – im Zweifel in der Weboberfläche prüfen.",
                        },
                        I("WhiteLed.bright", "Helligkeit", 0, 100),
                        I("WhiteLed.LightingSchedule.StartHour", "Zeitplan Start (Stunde)", 0, 23), I("WhiteLed.LightingSchedule.StartMin", "Zeitplan Start (Minute)", 0, 59),
                        I("WhiteLed.LightingSchedule.EndHour", "Zeitplan Ende (Stunde)", 0, 23), I("WhiteLed.LightingSchedule.EndMin", "Zeitplan Ende (Minute)", 0, 59),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ OSD
        pages.Add(new SettingsPage
        {
            Title = "Anzeige (OSD)", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Einblendungen im Bild", GetCmd = "GetOsd", SetCmd = "SetOsd", GetParam = P(),
                    Fields =
                    {
                        T("Osd.osdChannel.name", "Kamera-/Kanalname"), B("Osd.osdChannel.enable", "Namen einblenden"), E("Osd.osdChannel.pos", "Position Name"),
                        B("Osd.osdTime.enable", "Datum/Uhrzeit einblenden"), E("Osd.osdTime.pos", "Position Uhrzeit"),
                        B("Osd.watermark", "Reolink-Wasserzeichen"), B("Osd.bgcolor", "Hintergrund hinter Text"),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ Encoding
        FieldSpec Size(string stream, string q) => new($"Enc.{stream}.size", $"{q} – Auflösung", FieldKind.Enum)
        {
            OptionsProvider = s => EncOptions(s, stream, "size"), RebuildOnChange = true,
        };
        FieldSpec Rate(string stream, string key, string label) => new($"Enc.{stream}.{key}", label, FieldKind.IntEnum)
        {
            OptionsProvider = s => EncOptions(s, stream, key),
        };
        FieldSpec Prof(string stream, string q) => new($"Enc.{stream}.profile", $"{q} – Profil", FieldKind.Enum) { OptionsProvider = s => EncOptions(s, stream, "profile") };

        pages.Add(new SettingsPage
        {
            Title = "Streams", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Kodierung", GetCmd = "GetEnc", SetCmd = "SetEnc", GetParam = P(),
                    Note = "Clear = Hauptstream, Mid = Ext-Stream (falls vorhanden), Low = Substream. Nach dem Speichern verbinden sich die Streams kurz neu.",
                    Fields =
                    {
                        B("Enc.audio", "Audio aufzeichnen"),
                        R("Enc.mainStream.vType", "Clear – Codec"), Size("mainStream", "Clear"), Rate("mainStream", "frameRate", "Clear – Bildrate (fps)"),
                        Rate("mainStream", "bitRate", "Clear – Bitrate (kbit/s)"), I("Enc.mainStream.gop", "Clear – I-Frame-Intervall"), Prof("mainStream", "Clear"),
                        R("Enc.extStream.vType", "Mid – Codec"), Size("extStream", "Mid"), Rate("extStream", "frameRate", "Mid – Bildrate (fps)"),
                        Rate("extStream", "bitRate", "Mid – Bitrate (kbit/s)"), I("Enc.extStream.gop", "Mid – I-Frame-Intervall"), Prof("extStream", "Mid"),
                        R("Enc.subStream.vType", "Low – Codec"), Size("subStream", "Low"), Rate("subStream", "frameRate", "Low – Bildrate (fps)"),
                        Rate("subStream", "bitRate", "Low – Bitrate (kbit/s)"), I("Enc.subStream.gop", "Low – I-Frame-Intervall"), Prof("subStream", "Low"),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ Time
        pages.Add(new SettingsPage
        {
            Title = "Datum & Zeit", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Uhrzeit", GetCmd = "GetTime", SetCmd = "SetTime",
                    Fields =
                    {
                        R("Time.year", "Jahr"), R("Time.mon", "Monat"), R("Time.day", "Tag"), R("Time.hour", "Stunde"), R("Time.min", "Minute"),
                        E("Time.timeFmt", "Datumsformat"),
                        new FieldSpec("Time.hourFmt", "Zeitformat", FieldKind.IntEnum) { Options = new[] { "0", "1" }, Labels = new() { ["0"] = "24 Stunden", ["1"] = "12 Stunden" } },
                        B("Dst.enable", "Sommerzeit"),
                    },
                },
                new SettingsSection
                {
                    Title = "Zeitserver (NTP)", GetCmd = "GetNtp", SetCmd = "SetNtp",
                    Fields = { B("Ntp.enable", "NTP verwenden"), T("Ntp.server", "Server"), I("Ntp.port", "Port", 1, 65535), I("Ntp.interval", "Intervall (Minuten)") },
                },
            },
            Actions =
            {
                new SettingsAction("PC-Uhrzeit übernehmen", ctx =>
                {
                    var s = ctx.FindSection("GetTime");
                    if (s?.Edited == null) return ctx.Report("Uhrzeit nicht verfügbar.");
                    var now = DateTime.Now;
                    foreach (var (k, v) in new[] { ("year", now.Year), ("mon", now.Month), ("day", now.Day), ("hour", now.Hour), ("min", now.Minute), ("sec", now.Second) })
                        JsonUtil.Set(s.Edited, $"Time.{k}", v);
                    ctx.Rebuild();
                    return ctx.Report("PC-Uhrzeit eingetragen – mit „Übernehmen“ an die Kamera senden.");
                }),
            },
        });

        // ------------------------------------------------------------------ Detection
        SettingsSection Ai(string type, string title) => new()
        {
            Title = title, GetCmd = "GetAiAlarm", SetCmd = "SetAiAlarm",
            GetParam = new JsonObject { ["channel"] = ch, ["ai_type"] = type },
            KeepKeys = new[] { "channel", "ai_type" },
            SetExtras = new JsonObject { ["channel"] = ch },
            Fields = { I("AiAlarm.sensitivity", "Empfindlichkeit", 0, 100), I("AiAlarm.stay_time", "Verweildauer (s)", 0, 60) },
        };

        pages.Add(new SettingsPage
        {
            Title = "Erkennung", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Bewegungserkennung", GetCmd = "GetMdAlarm", SetCmd = "SetMdAlarm", GetParam = P(),
                    Fields =
                    {
                        I("MdAlarm.newSens.sensDef", "Empfindlichkeit"),
                        I("MdAlarm.sens.0.sensitivity", "Empfindlichkeit (Zeitfenster 1)", hint: "Ältere Firmware: kleinerer Wert = empfindlicher."),
                    },
                    Note = "Erkennungszonen werden in der Weboberfläche bzw. Reolink-App eingestellt.",
                },
                Ai("people", "KI-Erkennung: Personen"),
                Ai("vehicle", "KI-Erkennung: Fahrzeuge"),
                Ai("dog_cat", "KI-Erkennung: Tiere"),
            },
        });

        // ------------------------------------------------------------------ Recording
        pages.Add(new SettingsPage
        {
            Title = "Aufnahme", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Aufnahme (SD-Karte / NVR)", GetCmd = "GetRecV20", SetCmd = "SetRecV20", Fallback = ("GetRec", "SetRec"), GetParam = P(),
                    Fields =
                    {
                        B("Rec.enable", "Aufnahme aktiviert"), B("Rec.scheduleEnable", "Zeitplan aktiv"), B("Rec.schedule.enable", "Zeitplan aktiv"),
                        B("Rec.overwrite", "Älteste Aufnahmen überschreiben"), B("Rec.preRec", "Voraufnahme"),
                        E("Rec.postRec", "Nachlaufzeit"), E("Rec.packTime", "Dateilänge"), I("Rec.saveDay", "Aufbewahrung (Tage)"),
                    },
                    SchedulePaths = new[] { "Rec.schedule.table" },
                },
            },
        });

        // ------------------------------------------------------------------ FTP
        pages.Add(new SettingsPage
        {
            Title = "FTP", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "FTP-Upload", GetCmd = "GetFtpV20", SetCmd = "SetFtpV20", Fallback = ("GetFtp", "SetFtp"), GetParam = P(),
                    Fields =
                    {
                        B("Ftp.enable", "FTP aktiviert"), B("Ftp.scheduleEnable", "Upload-Zeitplan aktiv"), B("Ftp.schedule.enable", "Upload-Zeitplan aktiv"),
                        T("Ftp.server", "Server"), I("Ftp.port", "Port", 1, 65535), B("Ftp.anonymous", "Anonym anmelden"),
                        T("Ftp.userName", "Benutzername"), new FieldSpec("Ftp.password", "Passwort", FieldKind.Password),
                        T("Ftp.remoteDir", "Verzeichnis"), B("Ftp.autoDir", "Unterordner automatisch anlegen"), B("Ftp.onlyFtps", "Nur FTPS (verschlüsselt)"),
                        new FieldSpec("Ftp.mode", "Übertragungsmodus", FieldKind.IntEnum) { Options = new[] { "0", "1", "2" }, Labels = new() { ["0"] = "Automatisch", ["1"] = "Aktiv (PORT)", ["2"] = "Passiv (PASV)" } },
                        new FieldSpec("Ftp.streamType", "Video-Stream", FieldKind.IntEnum) { Options = new[] { "0", "1" }, Labels = new() { ["0"] = "Hauptstream (Clear)", ["1"] = "Substream (Low)", ["2"] = "Ext-Stream (Mid)" } },
                        E("Ftp.fileType", "Dateityp"), I("Ftp.maxSize", "Max. Videogröße (MB)"), I("Ftp.interval", "Hochladeintervall (s)"),
                        I("Ftp.picInterval", "Bildintervall (s)"), B("Ftp.bpicSingle", "Einzelbilder hochladen"), B("Ftp.bvideoSingle", "Einzelvideos hochladen"),
                        T("Ftp.picName", "Bild-Dateiname"), T("Ftp.videoName", "Video-Dateiname"),
                    },
                    SchedulePaths = new[] { "Ftp.schedule.table" },
                },
            },
            Actions =
            {
                new SettingsAction("FTP-Verbindung testen", async ctx =>
                {
                    var s = ctx.FindSection("Ftp");
                    var ftp = JsonUtil.Get(s?.Edited, "Ftp");
                    if (ftp == null) { await ctx.Report("FTP nicht verfügbar."); return; }
                    var r = await ctx.Session.ExecuteAsync("TestFtp", new JsonObject { ["Ftp"] = ftp.DeepClone() });
                    await ctx.Report(r.Ok ? "FTP-Test erfolgreich." : $"FTP-Test fehlgeschlagen: {r.ErrorDetail}");
                }),
            },
        });

        // ------------------------------------------------------------------ E-Mail
        pages.Add(new SettingsPage
        {
            Title = "E-Mail", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "E-Mail-Benachrichtigung", GetCmd = "GetEmailV20", SetCmd = "SetEmailV20", Fallback = ("GetEmail", "SetEmail"), GetParam = P(),
                    Fields =
                    {
                        B("Email.enable", "E-Mail aktiviert"), B("Email.scheduleEnable", "Zeitplan aktiv"), B("Email.schedule.enable", "Zeitplan aktiv"),
                        T("Email.smtpServer", "SMTP-Server"), I("Email.smtpPort", "SMTP-Port", 1, 65535), B("Email.ssl", "SSL/TLS"),
                        T("Email.userName", "Benutzername"), new FieldSpec("Email.password", "Passwort", FieldKind.Password),
                        T("Email.nickName", "Absendername"), T("Email.addr1", "Empfänger 1"), T("Email.addr2", "Empfänger 2"), T("Email.addr3", "Empfänger 3"),
                        new FieldSpec("Email.attachmentType", "Anhang", FieldKind.IntEnum) { Options = new[] { "0", "1", "2" }, Labels = new() { ["0"] = "Kein Anhang", ["1"] = "Bild", ["2"] = "Bild + Video" }, Hint = "Ältere Firmware speichert den Anhang als Zahl – nach dem Speichern bitte in der Reolink-App prüfen." }, E("Email.attachment", "Anhang"), E("Email.interval", "Mindestabstand"),
                    },
                    SchedulePaths = new[] { "Email.schedule.table" },
                },
            },
            Actions =
            {
                new SettingsAction("Test-E-Mail senden", async ctx =>
                {
                    var s = ctx.FindSection("Email");
                    var mail = JsonUtil.Get(s?.Edited, "Email");
                    if (mail == null) { await ctx.Report("E-Mail nicht verfügbar."); return; }
                    var r = await ctx.Session.ExecuteAsync("TestEmail", new JsonObject { ["Email"] = mail.DeepClone() });
                    await ctx.Report(r.Ok ? "Test-E-Mail gesendet." : $"E-Mail-Test fehlgeschlagen: {r.ErrorDetail}");
                }),
            },
        });

        // ------------------------------------------------------------------ Push
        pages.Add(new SettingsPage
        {
            Title = "Push", Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = "Push-Benachrichtigung (Reolink-App)", GetCmd = "GetPushV20", SetCmd = "SetPushV20", Fallback = ("GetPush", "SetPush"), GetParam = P(),
                    Fields = { B("Push.enable", "Push aktiviert"), B("Push.scheduleEnable", "Zeitplan aktiv"), B("Push.schedule.enable", "Zeitplan aktiv") },
                    SchedulePaths = new[] { "Push.schedule.table" },
                },
            },
        });

        // ------------------------------------------------------------------ PTZ
        pages.Add(new SettingsPage
        {
            Title = "PTZ", Icon = "",
            CustomContent = ctx => new PtzPanel(ctx),
        });

        return pages;
    }

    /// <summary>Encoder options from the camera's range: one range entry per main resolution.</summary>
    private static IEnumerable<string>? EncOptions(SettingsSection s, string stream, string key)
    {
        var rangeEnc = JsonUtil.Get(s.Range, "Enc");
        var entries = rangeEnc switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => new List<JsonObject> { o },
            _ => new List<JsonObject>(),
        };
        if (entries.Count == 0) return null;

        static IEnumerable<string> Values(JsonNode? n) => n switch
        {
            JsonArray a => a.Select(JsonUtil.AsString).OfType<string>(),
            null => Enumerable.Empty<string>(),
            _ => new[] { JsonUtil.AsString(n)! },
        };

        if (stream == "mainStream" && key == "size")
            return entries.SelectMany(e => Values(JsonUtil.Get(e, "mainStream.size"))).Distinct();

        // Everything else depends on the selected main resolution.
        var mainSize = JsonUtil.AsString(JsonUtil.Get(s.Edited, "Enc.mainStream.size"));
        var entry = entries.FirstOrDefault(e => Values(JsonUtil.Get(e, "mainStream.size")).Contains(mainSize)) ?? entries[0];
        var values = Values(JsonUtil.Get(entry, $"{stream}.{key}")).ToList();
        if (values.Count == 0 && key == "size") // some firmware lists sub sizes only in other entries
            values = entries.SelectMany(e => Values(JsonUtil.Get(e, $"{stream}.size"))).Distinct().ToList();
        return values.Count > 0 ? values : null;
    }
}
