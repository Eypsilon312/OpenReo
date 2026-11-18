using System.Text;

namespace OpenReo.Core.Diagnostics;

/// <summary>Well known application directories.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenReo");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}

/// <summary>
/// Minimal thread safe file logger (daily files, kept for 14 days).
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static DateTime _writerDate;
    private static bool _cleaned;

    public static void Debug(string message) => Write("DBG", message);
    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                var now = DateTime.Now;
                if (_writer == null || _writerDate != now.Date)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    var file = Path.Combine(AppPaths.LogDirectory, $"openreo-{now:yyyyMMdd}.log");
                    _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                    _writerDate = now.Date;
                    if (!_cleaned) { _cleaned = true; CleanupOldFiles(); }
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:D3}] {message}");
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }

    private static void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDirectory, "openreo-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14))
                    File.Delete(f);
        }
        catch { /* ignore */ }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
