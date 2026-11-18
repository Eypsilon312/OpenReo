using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Localization;

namespace OpenReo.Video;

/// <summary>
/// Loads the native FFmpeg libraries (expected in the "ffmpeg" sub folder of the application)
/// and routes FFmpeg log output into the application log.
/// </summary>
public static unsafe class FFmpegLoader
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static av_log_set_callback_callback? _logCallback; // keep delegate alive
    private static long _logWindowStart;
    private static int _logCount;

    public static string? Version { get; private set; }
    public static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;

            var dir = LibraryDirectory;
            if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "avcodec-*.dll").Any())
                throw new DllNotFoundException(Lang.F("FFmpeg-Bibliotheken nicht gefunden in '{0}'.", dir));

            ffmpeg.RootPath = dir;
            DynamicallyLoadedBindings.Initialize();

            Version = ffmpeg.av_version_info();
            ffmpeg.avformat_network_init();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _logCallback = OnLog;
            ffmpeg.av_log_set_callback(_logCallback);

            _initialized = true;
            Log.Info($"FFmpeg {Version} loaded from {dir}");
        }
    }

    private static void OnLog(void* avcl, int level, string format, byte* vl)
    {
        if (level > ffmpeg.av_log_get_level()) return;

        // Rate limit: corrupt streams can produce thousands of messages per second.
        var now = Environment.TickCount64;
        if (now - _logWindowStart > 1000) { _logWindowStart = now; _logCount = 0; }
        if (++_logCount > 20) return;

        const int size = 1024;
        var buffer = stackalloc byte[size];
        var prefix = 1;
        ffmpeg.av_log_format_line(avcl, level, format, vl, buffer, size, &prefix);
        var line = Marshal.PtrToStringAnsi((IntPtr)buffer)?.TrimEnd();
        if (!string.IsNullOrEmpty(line))
            Log.Debug("ffmpeg: " + line);
    }
}

/// <summary>Exception carrying an FFmpeg error code.</summary>
public sealed class FFmpegException : Exception
{
    public int ErrorCode { get; }
    public FFmpegException(string message, int errorCode = 0) : base(message) { ErrorCode = errorCode; }
}

internal static unsafe class FFmpegHelper
{
    public static string ErrorString(int error)
    {
        const int size = 1024;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? Lang.F("Fehler {0}", error);
    }

    /// <summary>Translates common FFmpeg / RTSP errors into user friendly German messages.</summary>
    public static string FriendlyError(int error, bool interruptedByTimeout)
    {
        if (interruptedByTimeout) return Lang.T("Zeitüberschreitung – Kamera nicht erreichbar");
        // Windows CRT errno values used by FFmpeg's network code: AVERROR(x) == -x
        switch (-error)
        {
            case 138: return Lang.T("Zeitüberschreitung – Kamera nicht erreichbar");          // ETIMEDOUT
            case 107: return Lang.T("Verbindung abgelehnt (RTSP-Port prüfen)");               // ECONNREFUSED
            case 108: return Lang.T("Verbindung von der Kamera zurückgesetzt");               // ECONNRESET
            case 110: return Lang.T("Kamera nicht erreichbar (Host unreachable)");           // EHOSTUNREACH
            case 118: return Lang.T("Netzwerk nicht erreichbar");                            // ENETUNREACH
            case 106: return Lang.T("Verbindung abgebrochen");                               // ECONNABORTED
        }
        var raw = ErrorString(error);
        if (raw.StartsWith("Error number", StringComparison.Ordinal)) return Lang.F("Verbindungsfehler ({0})", error);
        if (raw.Contains("401")) return Lang.T("Anmeldung fehlgeschlagen (Benutzername/Passwort prüfen)");
        if (raw.Contains("404")) return Lang.T("Stream nicht gefunden (RTSP-Pfad prüfen)");
        if (raw.Contains("503")) return Lang.T("Kamera ausgelastet (zu viele Verbindungen?)");
        if (raw.Contains("refused", StringComparison.OrdinalIgnoreCase)) return Lang.T("Verbindung abgelehnt (RTSP-Port prüfen)");
        if (raw.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return Lang.T("Zeitüberschreitung – Kamera nicht erreichbar");
        if (raw.Contains("unreachable", StringComparison.OrdinalIgnoreCase)) return Lang.T("Netzwerk nicht erreichbar");
        if (error == ffmpeg.AVERROR_EOF) return Lang.T("Stream wurde von der Kamera beendet");
        return raw;
    }

    public static void ThrowIfError(int ret, string action)
    {
        if (ret < 0) throw new FFmpegException($"{action}: {FriendlyError(ret, false)}", ret);
    }
}
