using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;

namespace OpenReo.Core.Configuration;

/// <summary>
/// Loads and saves the application configuration as JSON in %APPDATA%\OpenReo\config.json.
/// Passwords are encrypted with DPAPI (bound to the current Windows user).
/// Writes are atomic (temp file + replace) and the previous file is kept as .bak.
/// </summary>
public sealed class ConfigurationManager
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenReo.Credentials.v1");
    private readonly object _sync = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; }
    public AppConfiguration Current { get; private set; } = new();

    public ConfigurationManager(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.ConfigFile;
    }

    public AppConfiguration Load()
    {
        AppConfiguration? cfg = TryRead(FilePath);
        if (cfg == null && File.Exists(FilePath))
        {
            // Corrupted file: keep a copy for diagnosis and try the backup.
            try { File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            cfg = TryRead(FilePath + ".bak");
            if (cfg != null) Log.Warn("Configuration was corrupted – restored from backup.");
        }
        cfg ??= new AppConfiguration();
        Normalize(cfg);
        foreach (var cam in cfg.Cameras)
            cam.Password = Unprotect(cam.ProtectedPassword);
        cfg.Settings.DefaultPassword = Unprotect(cfg.Settings.ProtectedDefaultPassword);
        Current = cfg;
        Log.Info($"Configuration loaded: {cfg.Cameras.Count} camera(s), {cfg.CustomLayouts.Count} custom layout(s).");
        return cfg;
    }

    private static AppConfiguration? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read configuration '{path}'", ex);
            return null;
        }
    }

    private static void Normalize(AppConfiguration cfg)
    {
        cfg.Settings ??= new AppSettings();
        cfg.Cameras ??= new();
        cfg.CustomLayouts ??= new();
        cfg.SavedViews ??= new();
        cfg.LastView ??= new ViewState();
        cfg.LastView.Slots ??= new();
        cfg.Cameras.RemoveAll(c => c == null);
        foreach (var l in cfg.CustomLayouts) { l.Cells ??= new(); l.Normalize(); }
        foreach (var v in cfg.SavedViews) { v.Layout ??= new(); v.Layout.Cells ??= new(); v.Layout.Normalize(); v.Slots ??= new(); }
        var s = cfg.Settings;
        s.StreamReleaseDelaySeconds = Math.Clamp(s.StreamReleaseDelaySeconds, 0, 600);
        s.ConnectTimeoutSeconds = Math.Clamp(s.ConnectTimeoutSeconds, 3, 60);
        s.ReconnectMaxDelaySeconds = Math.Clamp(s.ReconnectMaxDelaySeconds, 2, 300);
        s.SidebarWidth = Math.Clamp(s.SidebarWidth, 180, 600);
    }

    /// <summary>Writes the current configuration to disk. Thread safe.</summary>
    public void Save()
    {
        lock (_sync)
        {
            try
            {
                foreach (var cam in Current.Cameras)
                    cam.ProtectedPassword = Protect(cam.Password);
                var s = Current.Settings;
                if (!s.RememberCredentials) { s.DefaultUsername = null; s.DefaultPassword = null; }
                s.ProtectedDefaultPassword = Protect(s.DefaultPassword);

                var json = JsonSerializer.Serialize(Current, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save configuration", ex);
            }
        }
    }

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not decrypt stored password (different Windows user?): {ex.Message}");
            return "";
        }
    }
}
