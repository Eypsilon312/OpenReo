using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Video.Recordings;

/// <summary>One recorded video file (e.g. uploaded by the camera via FTP to a NAS).</summary>
public sealed class RecordingFile
{
    public required string Path { get; init; }
    public required DateTime Start { get; init; }
    public long Size { get; init; }
    public DateTime LastWrite { get; init; }

    /// <summary>Real duration (read from the file) or null while unknown.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Estimated duration until the real one is known (gap to the next file, max. 1 h).</summary>
    public TimeSpan EstimatedDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The estimate comes from the file time: the camera writes the file while recording, so the
    /// last write is the end of the recording (accurate to about a second, no need to read the file).
    /// </summary>
    public bool EstimateFromFileTime { get; set; }

    /// <summary>Length is known well enough for the time line.</summary>
    public bool HasReliableDuration => Duration != null || EstimateFromFileTime;

    public TimeSpan EffectiveDuration => Duration ?? EstimatedDuration;
    public DateTime End => Start + EffectiveDuration;
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Picture the camera uploads together with the recordings (Reolink: one snapshot when a motion /
/// alarm recording starts). Shown as event marker with preview on the time line.
/// </summary>
public sealed record RecordingSnapshot(string Path, DateTime Time);

/// <summary>
/// Reads the start time of a recording from its file name or path. Supports the usual Reolink
/// naming schemes (…_20261118143512.mp4, RecM01_20261118_143512_….mp4, …/2026/11/18/143512.mp4,
/// 2026-11-18 14-35-12.mp4). Falls back to the file time stamp.
/// </summary>
public static class RecordingTimeParser
{
    private static readonly Regex Compact = new(@"(?<!\d)(20\d{2})(\d{2})(\d{2})[_\-T ]?(\d{2})(\d{2})(\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Separated = new(@"(?<!\d)(20\d{2})[-_.](\d{2})[-_.](\d{2})[ _T-](\d{2})[-_.:](\d{2})[-_.:](\d{2})(?!\d)", RegexOptions.Compiled);

    public static DateTime? Parse(string relativePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(relativePath);
        foreach (var candidate in new[]
                 {
                     name,
                     // date in the folders, time in the file name (…/2026/11/18/143512.mp4)
                     Regex.Replace(System.IO.Path.ChangeExtension(relativePath, null) ?? "", @"[\\/]", ""),
                 })
        {
            foreach (var re in new[] { Compact, Separated })
            {
                var m = re.Match(candidate);
                if (!m.Success) continue;
                int[] v = Enumerable.Range(1, 6).Select(i => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
                if (v[1] is < 1 or > 12 || v[2] is < 1 or > 31 || v[3] > 23 || v[4] > 59 || v[5] > 59) continue;
                try { return new DateTime(v[0], v[1], v[2], v[3], v[4], v[5], DateTimeKind.Local); }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        return null;
    }
}

/// <summary>
/// Index of all recordings of one camera folder. Scanning only lists the files (fast, also
/// over SMB); the exact durations are read lazily per day and cached on disk.
/// </summary>
public sealed class RecordingIndex
{
    private static readonly string[] Extensions = { ".mp4", ".mkv", ".mov", ".avi", ".ts", ".flv" };
    private static readonly string[] PictureExtensions = { ".jpg", ".jpeg" };

    public string Folder { get; }
    public IReadOnlyList<RecordingFile> Files { get; }

    /// <summary>Snapshots uploaded by the camera (event markers), sorted by time.</summary>
    public IReadOnlyList<RecordingSnapshot> Snapshots { get; }

    private RecordingIndex(string folder, List<RecordingFile> files, List<RecordingSnapshot> snapshots)
    {
        Folder = folder;
        Files = files;
        Snapshots = snapshots;
    }

    public static Task<RecordingIndex> ScanAsync(string folder, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = new List<RecordingFile>();
        var snapshots = new List<RecordingSnapshot>();
        var cache = DurationCache.Load();
        // DirectoryInfo delivers size and time stamps from the directory listing itself – no extra
        // request per file (new FileInfo(path) costs one SMB round trip each: 8 s instead of 1 s for 7000 files).
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            var ext = info.Extension;
            bool video = Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
            if (!video && !PictureExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            if (info.Length < 1024) continue; // incomplete uploads
            var start = RecordingTimeParser.Parse(System.IO.Path.GetRelativePath(folder, info.FullName));
            if (!video)
            {
                snapshots.Add(new RecordingSnapshot(info.FullName, start ?? info.LastWriteTime));
                continue;
            }
            files.Add(new RecordingFile
            {
                Path = info.FullName,
                Start = start ?? info.LastWriteTime,
                Size = info.Length,
                LastWrite = info.LastWriteTime,
                Duration = cache.TryGet(info.FullName, info.Length, info.LastWriteTimeUtc),
            });
        }
        files.Sort((a, b) => a.Start.CompareTo(b.Start));
        snapshots.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var gap = i + 1 < files.Count ? files[i + 1].Start - f.Start : (TimeSpan?)null;
            // the camera writes while recording → last write ≈ end of the recording
            var byFileTime = f.LastWrite - f.Start;
            if (byFileTime > TimeSpan.FromSeconds(1) && byFileTime < TimeSpan.FromHours(2) && (gap == null || byFileTime <= gap.Value + TimeSpan.FromSeconds(10)))
            {
                f.EstimatedDuration = gap is { } g && byFileTime > g ? g : byFileTime;
                f.EstimateFromFileTime = true;
            }
            else f.EstimatedDuration = gap is { } g && g > TimeSpan.Zero && g < TimeSpan.FromHours(1) ? g : TimeSpan.FromMinutes(1);
        }
        Log.Info($"Recording index {folder}: {files.Count} file(s), {snapshots.Count} snapshot(s)");
        return new RecordingIndex(folder, files, snapshots);
    }, ct);

    /// <summary>Days with recordings, newest first.</summary>
    public IReadOnlyList<DateTime> Days => Files.Select(f => f.Start.Date).Distinct().OrderByDescending(d => d).ToList();

    public IReadOnlyList<RecordingSnapshot> SnapshotsOn(DateTime day) =>
        Snapshots.Where(s => s.Time.Date == day.Date).ToList();

    public IReadOnlyList<RecordingFile> FilesOn(DateTime day) =>
        Files.Where(f => f.Start.Date == day.Date || (f.Start < day.Date && f.End > day.Date)).ToList();

    /// <summary>File that covers the given time (or the next one after it).</summary>
    public RecordingFile? FindAt(DateTime time, bool orNext = true)
    {
        RecordingFile? next = null;
        foreach (var f in Files)
        {
            if (f.Start <= time && f.End > time) return f;
            if (orNext && f.Start > time) { next ??= f; break; }
        }
        return next;
    }

    public RecordingFile? Next(RecordingFile file)
    {
        int i = Files.ToList().IndexOf(file);
        return i >= 0 && i + 1 < Files.Count ? Files[i + 1] : null;
    }

    public RecordingFile? Previous(RecordingFile file)
    {
        int i = Files.ToList().IndexOf(file);
        return i > 0 ? Files[i - 1] : null;
    }

    /// <summary>Reads the exact durations of the given files (background) and caches them.</summary>
    /// <remarks>
    /// Files whose length is already known from the file time are skipped; the others are read
    /// a few at a time in parallel (the NAS answers several requests at once much faster).
    /// </remarks>
    public static Task LoadDurationsAsync(IEnumerable<RecordingFile> files, IProgress<RecordingFile>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var todo = files.Where(f => !f.HasReliableDuration).ToList();
        if (todo.Count == 0) return;
        var cache = DurationCache.Load();
        int changed = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (f, token) =>
        {
            var d = MediaInfo.ProbeDuration(f.Path);
            if (d != null)
            {
                f.Duration = d;
                lock (cache) cache.Set(f.Path, f.Size, f.LastWrite.ToUniversalTime(), d.Value);
                Interlocked.Increment(ref changed);
                progress?.Report(f);
            }
            return ValueTask.CompletedTask;
        });
        if (changed > 0) cache.Save();
    }, ct);
}

/// <summary>Assignment of recording sub folders to cameras.</summary>
public static class RecordingFolders
{
    public static IReadOnlyList<string> SubFolders(string root) =>
        Directory.EnumerateDirectories(root).Select(System.IO.Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith('.') && !n.StartsWith('@')) // hidden / NAS system folders (@eaDir …)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Best matching folder for a camera name (exact, then contains), or null.</summary>
    public static string? Match(string cameraName, IEnumerable<string> folders)
    {
        var n = Normalize(cameraName);
        if (n.Length == 0) return null;
        var list = folders.ToList();
        return list.FirstOrDefault(f => Normalize(f) == n)
               ?? list.FirstOrDefault(f => Normalize(f).Contains(n) || n.Contains(Normalize(f)) && Normalize(f).Length >= 3);
    }
}

/// <summary>Reads media information with FFmpeg.</summary>
public static unsafe class MediaInfo
{
    public static TimeSpan? ProbeDuration(string path)
    {
        // fragmented MP4 (Reolink uploads): a few reads instead of FFmpeg reading every fragment header
        if (FragmentedMp4.TryOpen(path) is { } fmp4)
        {
            try { return TimeSpan.FromSeconds(fmp4.Duration); }
            catch (Exception ex) { Log.Debug($"fMP4 duration of {path}: {ex.Message}"); }
        }
        FFmpegLoader.Initialize();
        AVFormatContext* fmt = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) { fmt = null; return null; }
            if (fmt->duration <= 0 && ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            return fmt->duration > 0 ? TimeSpan.FromSeconds(fmt->duration / (double)ffmpeg.AV_TIME_BASE) : null;
        }
        finally
        {
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }
}

/// <summary>Persistent cache of file durations (%APPDATA%\OpenReo\recording-durations.json).</summary>
internal sealed class DurationCache
{
    private static readonly object Sync = new();
    private static string FilePath => System.IO.Path.Combine(AppPaths.DataDirectory, "recording-durations.json");
    private Dictionary<string, double> _entries = new();

    private static string Key(string path, long size, DateTime mtimeUtc) => $"{path}|{size}|{mtimeUtc.Ticks}";

    public static DurationCache Load()
    {
        lock (Sync)
        {
            var c = new DurationCache();
            try
            {
                if (File.Exists(FilePath))
                    c._entries = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { Log.Warn($"Duration cache unreadable: {ex.Message}"); }
            return c;
        }
    }

    public TimeSpan? TryGet(string path, long size, DateTime mtimeUtc) =>
        _entries.TryGetValue(Key(path, size, mtimeUtc), out var s) ? TimeSpan.FromSeconds(s) : null;

    public void Set(string path, long size, DateTime mtimeUtc, TimeSpan d) => _entries[Key(path, size, mtimeUtc)] = d.TotalSeconds;

    public void Save()
    {
        lock (Sync)
        {
            try
            {
                // merge with entries written by other indexes meanwhile
                var merged = Load()._entries;
                foreach (var (k, v) in _entries) merged[k] = v;
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(merged));
            }
            catch (Exception ex) { Log.Warn($"Duration cache not saved: {ex.Message}"); }
        }
    }
}
