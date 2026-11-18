using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using OpenReo.Core.Localization;

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
            Title = Lang.T("Allgemein"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Geräteinformationen"), GetCmd = "GetDevInfo",
                    Fields =
                    {
                        R("DevInfo.model", "Modell"), R("DevInfo.type", Lang.T("Gerätetyp")), R("DevInfo.firmVer", "Firmware"),
                        R("DevInfo.hardVer", "Hardware"), R("DevInfo.serial", "Seriennummer"), R("DevInfo.channelNum", Lang.T("Kanäle")),
                        R("DevInfo.buildDay", "Build"), R("DevInfo.detail", "Details"),
                    },
                },
                new SettingsSection
                {
                    Title = Lang.T("Gerätename"), GetCmd = "GetDevName", SetCmd = "SetDevName", GetParam = P(),
                    Fields = { T("DevName.name", "Name", Lang.T("Bei einem NVR ist das der Name des Rekorders – der Kanalname steht unter „Anzeige“.")) },
                },
                new SettingsSection
                {
                    Title = Lang.T("Status-LED"), GetCmd = "GetPowerLed", SetCmd = "SetPowerLed", GetParam = P(),
                    Fields = { new FieldSpec("PowerLed.state", "Status-LED", FieldKind.Enum) { Options = new[] { "On", "Off" } } },
                },
                new SettingsSection
                {
                    Title = Lang.T("Speicherkarte / Festplatte"), GetCmd = "GetHddInfo",
                    Fields =
                    {
                        R("HddInfo.0.capacity", Lang.T("Kapazität (MB)")), R("HddInfo.0.size", Lang.T("Belegt/Größe laut Gerät (MB)")),
                        R("HddInfo.0.format", "Formatiert (1 = ja)"), R("HddInfo.0.mount", "Eingebunden (1 = ja)"),
                    },
                    Note = Lang.T("Formatieren ist bewusst nur über die Weboberfläche bzw. Reolink-App möglich."),
                },
            },
            Actions =
            {
                new SettingsAction(Lang.T("Weboberfläche öffnen"), ctx =>
                {
                    Process.Start(new ProcessStartInfo(ctx.Session.BaseUrl) { UseShellExecute = true });
                    return Task.CompletedTask;
                }),
                new SettingsAction(Lang.T("Sirene testen"), async ctx =>
                {
                    var r = await ctx.Session.ExecuteAsync("AudioAlarmPlay", new JsonObject
                    {
                        ["channel"] = ctx.Channel, ["alarm_mode"] = "times", ["manual_switch"] = 0, ["times"] = 1,
                    });
                    await ctx.Report(r.Ok ? Lang.T("Sirene ausgelöst.") : Lang.F("Sirene nicht verfügbar: {0}", r.ErrorDetail));
                }),
                new SettingsAction(Lang.T("Kamera neu starten"), async ctx =>
                {
                    if (MessageBox.Show(ctx.Owner, Lang.T("Kamera jetzt neu starten? Die Verbindung ist für ca. 1 Minute unterbrochen."), Lang.T("Neustart"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    var r = await ctx.Session.ExecuteAsync("Reboot", new JsonObject());
                    await ctx.Report(r.Ok ? Lang.T("Neustart ausgelöst.") : Lang.F("Neustart fehlgeschlagen: {0}", r.ErrorDetail));
                }, Destructive: true),
            },
        });

        // ------------------------------------------------------------------ Image
        pages.Add(new SettingsPage
        {
            Title = Lang.T("Bild"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Bildeinstellungen"), GetCmd = "GetImage", SetCmd = "SetImage", GetParam = P(),
                    Fields =
                    {
                        I("Image.bright", "Helligkeit", 0, 255), I("Image.contrast", "Kontrast", 0, 255),
                        I("Image.saturation", Lang.T("Sättigung"), 0, 255), I("Image.hue", "Farbton", 0, 255), I("Image.sharpen", Lang.T("Schärfe"), 0, 255),
                    },
                },
                new SettingsSection
                {
                    Title = Lang.T("Belichtung & Tag/Nacht"), GetCmd = "GetIsp", SetCmd = "SetIsp", GetParam = P(),
                    Fields =
                    {
                        E("Isp.dayNight", Lang.T("Tag/Nacht-Modus")), I("Isp.dayNightThreshold", "Tag/Nacht-Schwelle"),
                        E("Isp.antiFlicker", "Flimmerschutz"), E("Isp.exposure", "Belichtung"),
                        E("Isp.backLight", "Gegenlicht"), I("Isp.blc", Lang.T("Gegenlicht-Stärke (BLC)")), I("Isp.drc", "Dynamikbereich (DRC)"),
                        E("Isp.whiteBalance", Lang.T("Weißabgleich")), I("Isp.redGain", Lang.T("Rotverstärkung")), I("Isp.blueGain", Lang.T("Blauverstärkung")),
                        B("Isp.nr3d", Lang.T("3D-Rauschunterdrückung")), B("Isp.mirroring", Lang.T("Bild spiegeln")), B("Isp.rotation", Lang.T("Bild drehen (180°)")),
                    },
                },
                new SettingsSection
                {
                    Title = Lang.T("Infrarot"), GetCmd = "GetIrLights", SetCmd = "SetIrLights", GetParam = P(),
                    Fields = { new FieldSpec("IrLights.state", Lang.T("Infrarot-LEDs"), FieldKind.Enum) { Options = new[] { "Auto", "On", "Off" } } },
                },
                new SettingsSection
                {
                    Title = Lang.T("Scheinwerfer"), GetCmd = "GetWhiteLed", SetCmd = "SetWhiteLed", GetParam = P(),
                    Fields =
                    {
                        B("WhiteLed.state", Lang.T("Scheinwerfer jetzt an")),
                        new FieldSpec("WhiteLed.mode", "Modus", FieldKind.IntEnum)
                        {
                            Options = new[] { "0", "1", "2", "3" },
                            Labels = new() { ["0"] = "Aus", ["1"] = Lang.T("Automatisch (Nacht/Bewegung)"), ["2"] = Lang.T("Modus 2"), ["3"] = "Zeitplan" },
                            Hint = Lang.T("Die Modusnummern unterscheiden sich je nach Modell – im Zweifel in der Weboberfläche prüfen."),
                        },
                        I("WhiteLed.bright", "Helligkeit", 0, 100),
                        I("WhiteLed.LightingSchedule.StartHour", Lang.T("Zeitplan Start (Stunde)"), 0, 23), I("WhiteLed.LightingSchedule.StartMin", Lang.T("Zeitplan Start (Minute)"), 0, 59),
                        I("WhiteLed.LightingSchedule.EndHour", Lang.T("Zeitplan Ende (Stunde)"), 0, 23), I("WhiteLed.LightingSchedule.EndMin", Lang.T("Zeitplan Ende (Minute)"), 0, 59),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ OSD
        pages.Add(new SettingsPage
        {
            Title = Lang.T("Anzeige (OSD)"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Einblendungen im Bild"), GetCmd = "GetOsd", SetCmd = "SetOsd", GetParam = P(),
                    Fields =
                    {
                        T("Osd.osdChannel.name", Lang.T("Kamera-/Kanalname")), B("Osd.osdChannel.enable", "Namen einblenden"), E("Osd.osdChannel.pos", Lang.T("Position Name")),
                        B("Osd.osdTime.enable", Lang.T("Datum/Uhrzeit einblenden")), E("Osd.osdTime.pos", Lang.T("Position Uhrzeit")),
                        B("Osd.watermark", "Reolink-Wasserzeichen"), B("Osd.bgcolor", "Hintergrund hinter Text"),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ Encoding
        FieldSpec Size(string stream, string q) => new($"Enc.{stream}.size", Lang.F("{0} – Auflösung", q), FieldKind.Enum)
        {
            OptionsProvider = s => EncOptions(s, stream, "size"), RebuildOnChange = true,
        };
        FieldSpec Rate(string stream, string key, string label) => new($"Enc.{stream}.{key}", label, FieldKind.IntEnum)
        {
            OptionsProvider = s => EncOptions(s, stream, key),
        };
        FieldSpec Prof(string stream, string q) => new($"Enc.{stream}.profile", Lang.F("{0} – Profil", q), FieldKind.Enum) { OptionsProvider = s => EncOptions(s, stream, "profile") };

        pages.Add(new SettingsPage
        {
            Title = Lang.T("Streams"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Kodierung"), GetCmd = "GetEnc", SetCmd = "SetEnc", GetParam = P(),
                    Note = Lang.T("Clear = Hauptstream, Mid = Ext-Stream (falls vorhanden), Low = Substream. Nach dem Speichern verbinden sich die Streams kurz neu."),
                    Fields =
                    {
                        B("Enc.audio", "Audio aufzeichnen"),
                        R("Enc.mainStream.vType", "Clear – Codec"), Size("mainStream", "Clear"), Rate("mainStream", "frameRate", Lang.T("Clear – Bildrate (fps)")),
                        Rate("mainStream", "bitRate", Lang.T("Clear – Bitrate (kbit/s)")), I("Enc.mainStream.gop", Lang.T("Clear – I-Frame-Intervall")), Prof("mainStream", "Clear"),
                        R("Enc.extStream.vType", "Mid – Codec"), Size("extStream", "Mid"), Rate("extStream", "frameRate", Lang.T("Mid – Bildrate (fps)")),
                        Rate("extStream", "bitRate", Lang.T("Mid – Bitrate (kbit/s)")), I("Enc.extStream.gop", Lang.T("Mid – I-Frame-Intervall")), Prof("extStream", "Mid"),
                        R("Enc.subStream.vType", "Low – Codec"), Size("subStream", "Low"), Rate("subStream", "frameRate", Lang.T("Low – Bildrate (fps)")),
                        Rate("subStream", "bitRate", Lang.T("Low – Bitrate (kbit/s)")), I("Enc.subStream.gop", Lang.T("Low – I-Frame-Intervall")), Prof("subStream", "Low"),
                    },
                },
            },
        });

        // ------------------------------------------------------------------ Time
        pages.Add(new SettingsPage
        {
            Title = Lang.T("Datum & Zeit"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Uhrzeit"), GetCmd = "GetTime", SetCmd = "SetTime",
                    Fields =
                    {
                        R("Time.year", Lang.T("Jahr")), R("Time.mon", Lang.T("Monat")), R("Time.day", Lang.T("Tag")), R("Time.hour", Lang.T("Stunde")), R("Time.min", Lang.T("Minute")),
                        E("Time.timeFmt", "Datumsformat"),
                        new FieldSpec("Time.hourFmt", "Zeitformat", FieldKind.IntEnum) { Options = new[] { "0", "1" }, Labels = new() { ["0"] = "24 Stunden", ["1"] = "12 Stunden" } },
                        B("Dst.enable", "Sommerzeit"),
                    },
                },
                new SettingsSection
                {
                    Title = Lang.T("Zeitserver (NTP)"), GetCmd = "GetNtp", SetCmd = "SetNtp",
                    Fields = { B("Ntp.enable", "NTP verwenden"), T("Ntp.server", "Server"), I("Ntp.port", "Port", 1, 65535), I("Ntp.interval", Lang.T("Intervall (Minuten)")) },
                },
            },
            Actions =
            {
                new SettingsAction(Lang.T("PC-Uhrzeit übernehmen"), ctx =>
                {
                    var s = ctx.FindSection("GetTime");
                    if (s?.Edited == null) return ctx.Report(Lang.T("Uhrzeit nicht verfügbar."));
                    var now = DateTime.Now;
                    foreach (var (k, v) in new[] { ("year", now.Year), ("mon", now.Month), ("day", now.Day), ("hour", now.Hour), ("min", now.Minute), ("sec", now.Second) })
                        JsonUtil.Set(s.Edited, $"Time.{k}", v);
                    ctx.Rebuild();
                    return ctx.Report(Lang.T("PC-Uhrzeit eingetragen – mit „Übernehmen“ an die Kamera senden."));
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
            Fields = { I("AiAlarm.sensitivity", "Empfindlichkeit", 0, 100), I("AiAlarm.stay_time", Lang.T("Verweildauer (s)"), 0, 60) },
        };

        pages.Add(new SettingsPage
        {
            Title = Lang.T("Erkennung"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Bewegungserkennung"), GetCmd = "GetMdAlarm", SetCmd = "SetMdAlarm", GetParam = P(),
                    Fields =
                    {
                        I("MdAlarm.newSens.sensDef", "Empfindlichkeit"),
                        I("MdAlarm.sens.0.sensitivity", Lang.T("Empfindlichkeit (Zeitfenster 1)"), hint: Lang.T("Ältere Firmware: kleinerer Wert = empfindlicher.")),
                    },
                    Note = Lang.T("Erkennungszonen werden in der Weboberfläche bzw. Reolink-App eingestellt."),
                },
                Ai("people", Lang.T("KI-Erkennung: Personen")),
                Ai("vehicle", Lang.T("KI-Erkennung: Fahrzeuge")),
                Ai("dog_cat", Lang.T("KI-Erkennung: Tiere")),
            },
        });

        // ------------------------------------------------------------------ Recording
        pages.Add(new SettingsPage
        {
            Title = Lang.T("Aufnahme"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Aufnahme (SD-Karte / NVR)"), GetCmd = "GetRecV20", SetCmd = "SetRecV20", Fallback = ("GetRec", "SetRec"), GetParam = P(),
                    Fields =
                    {
                        B("Rec.enable", Lang.T("Aufnahme aktiviert")), B("Rec.scheduleEnable", Lang.T("Zeitplan aktiv")), B("Rec.schedule.enable", Lang.T("Zeitplan aktiv")),
                        B("Rec.overwrite", Lang.T("Älteste Aufnahmen überschreiben")), B("Rec.preRec", "Voraufnahme"),
                        E("Rec.postRec", "Nachlaufzeit"), E("Rec.packTime", Lang.T("Dateilänge")), I("Rec.saveDay", "Aufbewahrung (Tage)"),
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
                    Title = Lang.T("FTP-Upload"), GetCmd = "GetFtpV20", SetCmd = "SetFtpV20", Fallback = ("GetFtp", "SetFtp"), GetParam = P(),
                    Fields =
                    {
                        B("Ftp.enable", "FTP aktiviert"), B("Ftp.scheduleEnable", Lang.T("Upload-Zeitplan aktiv")), B("Ftp.schedule.enable", Lang.T("Upload-Zeitplan aktiv")),
                        T("Ftp.server", "Server"), I("Ftp.port", "Port", 1, 65535), B("Ftp.anonymous", "Anonym anmelden"),
                        T("Ftp.userName", "Benutzername"), new FieldSpec("Ftp.password", "Passwort", FieldKind.Password),
                        T("Ftp.remoteDir", "Verzeichnis"), B("Ftp.autoDir", "Unterordner automatisch anlegen"), B("Ftp.onlyFtps", Lang.T("Nur FTPS (verschlüsselt)")),
                        new FieldSpec("Ftp.mode", Lang.T("Übertragungsmodus"), FieldKind.IntEnum) { Options = new[] { "0", "1", "2" }, Labels = new() { ["0"] = "Automatisch", ["1"] = "Aktiv (PORT)", ["2"] = "Passiv (PASV)" } },
                        new FieldSpec("Ftp.streamType", "Video-Stream", FieldKind.IntEnum) { Options = new[] { "0", "1" }, Labels = new() { ["0"] = "Hauptstream (Clear)", ["1"] = "Substream (Low)", ["2"] = "Ext-Stream (Mid)" } },
                        E("Ftp.fileType", "Dateityp"), I("Ftp.maxSize", Lang.T("Max. Videogröße (MB)")), I("Ftp.interval", "Hochladeintervall (s)"),
                        I("Ftp.picInterval", Lang.T("Bildintervall (s)")), B("Ftp.bpicSingle", "Einzelbilder hochladen"), B("Ftp.bvideoSingle", "Einzelvideos hochladen"),
                        T("Ftp.picName", Lang.T("Bild-Dateiname")), T("Ftp.videoName", "Video-Dateiname"),
                    },
                    SchedulePaths = new[] { "Ftp.schedule.table" },
                },
            },
            Actions =
            {
                new SettingsAction(Lang.T("FTP-Verbindung testen"), async ctx =>
                {
                    var s = ctx.FindSection("Ftp");
                    var ftp = JsonUtil.Get(s?.Edited, "Ftp");
                    if (ftp == null) { await ctx.Report(Lang.T("FTP nicht verfügbar.")); return; }
                    var r = await ctx.Session.ExecuteAsync("TestFtp", new JsonObject { ["Ftp"] = ftp.DeepClone() });
                    await ctx.Report(r.Ok ? Lang.T("FTP-Test erfolgreich.") : Lang.F("FTP-Test fehlgeschlagen: {0}", r.ErrorDetail));
                }),
            },
        });

        // ------------------------------------------------------------------ E-Mail
        pages.Add(new SettingsPage
        {
            Title = Lang.T("E-Mail"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("E-Mail-Benachrichtigung"), GetCmd = "GetEmailV20", SetCmd = "SetEmailV20", Fallback = ("GetEmail", "SetEmail"), GetParam = P(),
                    Fields =
                    {
                        B("Email.enable", "E-Mail aktiviert"), B("Email.scheduleEnable", Lang.T("Zeitplan aktiv")), B("Email.schedule.enable", Lang.T("Zeitplan aktiv")),
                        T("Email.smtpServer", "SMTP-Server"), I("Email.smtpPort", "SMTP-Port", 1, 65535), B("Email.ssl", "SSL/TLS"),
                        T("Email.userName", "Benutzername"), new FieldSpec("Email.password", "Passwort", FieldKind.Password),
                        T("Email.nickName", "Absendername"), T("Email.addr1", Lang.T("Empfänger 1")), T("Email.addr2", Lang.T("Empfänger 2")), T("Email.addr3", Lang.T("Empfänger 3")),
                        new FieldSpec("Email.attachmentType", "Anhang", FieldKind.IntEnum) { Options = new[] { "0", "1", "2" }, Labels = new() { ["0"] = Lang.T("Kein Anhang"), ["1"] = "Bild", ["2"] = Lang.T("Bild + Video") }, Hint = Lang.T("Ältere Firmware speichert den Anhang als Zahl – nach dem Speichern bitte in der Reolink-App prüfen.") }, E("Email.attachment", "Anhang"), E("Email.interval", "Mindestabstand"),
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
                    if (mail == null) { await ctx.Report(Lang.T("E-Mail nicht verfügbar.")); return; }
                    var r = await ctx.Session.ExecuteAsync("TestEmail", new JsonObject { ["Email"] = mail.DeepClone() });
                    await ctx.Report(r.Ok ? Lang.T("Test-E-Mail gesendet.") : Lang.F("E-Mail-Test fehlgeschlagen: {0}", r.ErrorDetail));
                }),
            },
        });

        // ------------------------------------------------------------------ Push
        pages.Add(new SettingsPage
        {
            Title = Lang.T("Push"), Icon = "",
            Sections =
            {
                new SettingsSection
                {
                    Title = Lang.T("Push-Benachrichtigung (Reolink-App)"), GetCmd = "GetPushV20", SetCmd = "SetPushV20", Fallback = ("GetPush", "SetPush"), GetParam = P(),
                    Fields = { B("Push.enable", "Push aktiviert"), B("Push.scheduleEnable", Lang.T("Zeitplan aktiv")), B("Push.schedule.enable", Lang.T("Zeitplan aktiv")) },
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
