using System.Buffers.Binary;
using System.Text;
using FFmpeg.AutoGen;
using Microsoft.Win32.SafeHandles;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Video.Recordings;

/// <summary>
/// Fast access to fragmented MP4 files as Reolink cameras write them while uploading via FTP
/// (ftyp + moov without sample tables, then one moof/mdat pair per GOP, no index, no time stamps
/// in the fragments).
///
/// FFmpeg reads every fragment header of such a file before it can play or seek: hundreds of
/// random reads, several seconds on a NAS with a cold cache. Here the time line of the file is
/// estimated from a few fragments read in parallel instead: a fragment always holds the same
/// number of frames, its length only changes when the camera changes the frame rate (day/night).
/// Fragments are sampled across the file, and only where the length differs between two samples
/// the range is bisected until the change is found. FFmpeg is then started directly at the
/// wanted fragment (see <see cref="OpenAt"/>).
/// </summary>
public sealed class FragmentedMp4
{
    private const int HeaderReadSize = 256 * 1024;
    private const int ScanWindow = 128 * 1024;
    private const int MaxScanBytes = 16 * 1024 * 1024;
    private const int InitialSamples = 12;

    public string Path { get; }

    /// <summary>Everything in front of the first fragment (ftyp + moov).</summary>
    internal byte[] Header { get; }

    private readonly uint _videoTrack;
    private readonly uint _videoTimescale;
    private readonly uint _videoDefaultDuration;

    private readonly object _sync = new();
    private readonly SortedList<uint, Fragment> _samples = new();
    private double[] _cum = Array.Empty<double>(); // start time of each sample (same order as _samples)
    private long _modelLength = -1;

    /// <summary>A fragment: offset of its moof, offset of the next fragment, length of its video part.</summary>
    public readonly record struct Fragment(uint Seq, long Offset, long NextOffset, double Duration);

    /// <summary>Position to start playing from.</summary>
    public readonly record struct StartPoint(Fragment Fragment, double Time);

    private FragmentedMp4(string path, byte[] header, uint videoTrack, uint timescale, uint defaultDuration)
    {
        Path = path;
        Header = header;
        _videoTrack = videoTrack;
        _videoTimescale = timescale;
        _videoDefaultDuration = defaultDuration;
    }

    internal static SafeFileHandle OpenHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);

    /// <summary>Reads the header; null if the file is not a fragmented MP4 of the supported kind.</summary>
    public static FragmentedMp4? TryOpen(string path)
    {
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var h = OpenHandle(path);
            var buf = new byte[(int)Math.Min(HeaderReadSize, RandomAccess.GetLength(h))];
            int n = ReadAt(h, 0, buf);
            int p = 0, moovStart = -1, moovSize = 0;
            while (p + 8 <= n)
            {
                long size = U32(buf, p);
                if (size < 8) return null;
                string type = Type(buf, p + 4);
                if (type == "moov") { moovStart = p; moovSize = (int)Math.Min(size, int.MaxValue); }
                else if (type == "moof") return moovStart >= 0 && moovStart + moovSize <= p ? Create(path, h, buf, moovStart, moovSize, p) : null;
                else if (type == "mdat") return null; // regular MP4 (index in moov) – FFmpeg handles it quickly
                p += (int)Math.Min(size, int.MaxValue);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"fMP4 header of {path}: {ex.Message}");
            return null;
        }
    }

    private static FragmentedMp4? Create(string path, SafeFileHandle h, byte[] buf, int moovStart, int moovSize, int firstMoof)
    {
        var moov = buf.AsSpan(moovStart, moovSize);
        int mvex = Find(moov, 8, "mvex");
        if (mvex < 0) return null;

        uint videoTrack = 0, timescale = 0, trexDuration = 0;
        foreach (var (start, size) in Children(moov, 8, "trak"))
        {
            var t = moov.Slice(start, size);
            int tkhd = Find(t, 8, "tkhd"), mdia = Find(t, 8, "mdia");
            if (tkhd < 0 || mdia < 0) continue;
            var m = t.Slice(mdia, (int)U32(t, mdia));
            int hdlr = Find(m, 8, "hdlr"), mdhd = Find(m, 8, "mdhd");
            if (hdlr < 0 || mdhd < 0 || Type(m, hdlr + 16) != "vide") continue;
            videoTrack = U32(t, tkhd + (t[tkhd + 8] == 1 ? 28 : 20));
            timescale = U32(m, mdhd + (m[mdhd + 8] == 1 ? 28 : 20));
            break;
        }
        if (videoTrack == 0 || timescale == 0) return null;
        var mvexBox = moov.Slice(mvex, (int)U32(moov, mvex));
        foreach (var (start, size) in Children(mvexBox, 8, "trex"))
            if (size >= 24 && U32(mvexBox, start + 12) == videoTrack) trexDuration = U32(mvexBox, start + 20);

        var file = new FragmentedMp4(path, buf.AsSpan(0, firstMoof).ToArray(), videoTrack, timescale, trexDuration);
        var first = file.FragmentAt(h, buf, firstMoof, firstMoof);
        if (first is not { Duration: > 0.05 and < 60 } f) return null;
        file._samples[f.Seq] = f;
        return file;
    }

    // =====================================================================
    // Time line model
    // =====================================================================

    /// <summary>Length of the file in seconds.</summary>
    public double Duration
    {
        get
        {
            lock (_sync)
            {
                EnsureModel();
                return _cum[^1] + _samples.Values[^1].Duration;
            }
        }
    }

    /// <summary>
    /// The fragment that contains <paramref name="seconds"/> and its start time. Usually costs one
    /// or two rounds of parallel reads for the first call, a single read afterwards.
    /// </summary>
    public StartPoint Locate(double seconds)
    {
        lock (_sync)
        {
            var head = _samples.Values[0];
            if (seconds < head.Duration) return new StartPoint(head, 0); // start of the file: no model needed
            EnsureModel();
            var keys = _samples.Keys;
            var values = _samples.Values;
            // last sample starting at or before the target
            int i = 0;
            while (i + 1 < keys.Count && _cum[i + 1] <= seconds) i++;
            var a = values[i];
            if (seconds < _cum[i] + a.Duration || i + 1 >= keys.Count && seconds >= _cum[i]) return new StartPoint(a, _cum[i]);

            // between sample i and i+1: fragment number from the (constant) fragment length
            double d = Run(i);
            uint seq = a.Seq + (uint)Math.Clamp((int)((seconds - _cum[i] - a.Duration) / d) + 1, 1, (int)(keys[i + 1] - a.Seq - 1));
            using var h = OpenHandle(Path);
            var f = FindSeq(h, a, values[i + 1], seq);
            double time = f.Seq == a.Seq ? _cum[i] : _cum[i] + a.Duration + (f.Seq - a.Seq - 1) * d;
            Recompute(); // FindSeq added samples
            return new StartPoint(f, time);
        }
    }

    /// <summary>Length per fragment between sample i and i+1 (assumed constant there).</summary>
    private double Run(int i)
    {
        var a = _samples.Values[i];
        if (i + 1 >= _samples.Count) return a.Duration;
        var b = _samples.Values[i + 1];
        // a fragment run is homogeneous between the samples; a itself may be the odd one (first fragment)
        return b.Seq - a.Seq > 1 ? b.Duration : a.Duration;
    }

    private void EnsureModel()
    {
        using var h = OpenHandle(Path);
        long len = RandomAccess.GetLength(h);
        if (_modelLength == len) return;
        if (_modelLength < 0 || len < _modelLength) BuildModel(h, len);
        else ExtendModel(h, len); // file still growing (recording in progress)
        _modelLength = len;
        Recompute();
    }

    private void BuildModel(SafeFileHandle h, long len)
    {
        var first = _samples.Values[0];
        // fragment 2 (the first one is often shorter) + samples spread over the file + the last fragment
        var offsets = new List<long>();
        long span = len - first.NextOffset;
        for (int k = 1; k <= InitialSamples; k++) offsets.Add(first.NextOffset + span * k / (InitialSamples + 1));
        var found = new Fragment?[offsets.Count + 2];
        Parallel.Invoke(
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            Enumerable.Range(0, offsets.Count).Select(k => (Action)(() => found[k] = ScanForward(h, offsets[k], len)))
                .Append(() => found[^2] = first.NextOffset + 24 <= len ? FragmentAtFile(h, first.NextOffset) : null)
                .Append(() => found[^1] = ScanBackward(h, len))
                .ToArray());
        foreach (var f in found) if (f is { } x) _samples[x.Seq] = x;
        Refine(h, len);
    }

    private void ExtendModel(SafeFileHandle h, long len)
    {
        var last = _samples.Values[^1];
        if (ScanBackward(h, len) is { } tail) _samples[tail.Seq] = tail;
        if (last.NextOffset + 24 <= len && FragmentAtFile(h, last.NextOffset) is { } next) _samples[next.Seq] = next;
        Refine(h, len);
    }

    /// <summary>Bisects every range whose ends have different fragment lengths (frame rate changed in between).</summary>
    private void Refine(SafeFileHandle h, long len)
    {
        for (int round = 0; round < 16; round++)
        {
            var todo = new List<(Fragment A, Fragment B)>();
            var values = _samples.Values;
            for (int i = 1; i + 1 < values.Count; i++) // [0]→[1] is the odd first fragment, adjacent anyway
            {
                var a = values[i];
                var b = values[i + 1];
                if (b.Seq - a.Seq > 1 && !SameLength(a.Duration, b.Duration)) todo.Add((a, b));
            }
            if (values.Count >= 2 && values[1].Seq - values[0].Seq > 1) todo.Add((values[0], values[1]));
            if (todo.Count == 0) return;
            var found = new Fragment?[todo.Count];
            Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, k =>
            {
                var (a, b) = todo[k];
                long mid = a.NextOffset + (b.Offset - a.NextOffset) / 2;
                var f = b.Seq - a.Seq > 2 ? ScanForward(h, mid, b.Offset) : null;
                // nothing between the middle and b (or b is next to a): take the fragment right after a
                found[k] = f is { } x && x.Seq > a.Seq && x.Seq < b.Seq ? x : FragmentAtFile(h, a.NextOffset);
            });
            int added = 0;
            foreach (var f in found)
                if (f is { } x && !_samples.ContainsKey(x.Seq)) { _samples[x.Seq] = x; added++; }
            if (added == 0) return;
        }
    }

    private static bool SameLength(double a, double b) => Math.Abs(a - b) <= 0.01 * Math.Max(a, b);

    private void Recompute()
    {
        var values = _samples.Values;
        _cum = new double[values.Count];
        for (int i = 1; i < values.Count; i++)
        {
            var a = values[i - 1];
            uint between = values[i].Seq - a.Seq - 1; // fragments between the two samples
            _cum[i] = _cum[i - 1] + a.Duration + between * Run(i - 1);
        }
    }

    /// <summary>Finds the fragment with sequence number <paramref name="seq"/> between two known fragments.</summary>
    private Fragment FindSeq(SafeFileHandle h, Fragment lower, Fragment upper, uint seq)
    {
        for (int iteration = 0; iteration < 12 && lower.Seq < seq; iteration++)
        {
            if (seq - lower.Seq <= 4) break; // close: follow the chain
            double perFrag = (upper.Offset - lower.Offset) / (double)(upper.Seq - lower.Seq);
            long guess = lower.Offset + (long)((seq - lower.Seq) * perFrag) - (long)(perFrag / 2);
            if (guess <= lower.NextOffset || guess >= upper.Offset) guess = lower.NextOffset + (upper.Offset - lower.NextOffset) / 2;
            var found = ScanForward(h, guess, upper.Offset);
            if (found is not { } f || f.Seq >= upper.Seq) { upper = new Fragment(upper.Seq, guess, guess, 0); continue; }
            _samples.TryAdd(f.Seq, f);
            if (f.Seq == seq) return f;
            if (f.Seq < seq) lower = f; else upper = f;
        }
        // chain from lower
        var cur = lower;
        for (int i = 0; i < 64 && cur.Seq < seq; i++)
        {
            if (FragmentAtFile(h, cur.NextOffset) is not { } next) break;
            cur = next;
        }
        return cur;
    }

    // =====================================================================
    // Reading fragments
    // =====================================================================

    private Fragment? FragmentAtFile(SafeFileHandle h, long offset)
    {
        var buf = new byte[4096];
        int n = ReadAt(h, offset, buf);
        return n >= 24 && IsMoof(buf, 0, n) ? FragmentAt(h, buf, 0, offset, n) : null;
    }

    /// <summary>First fragment at or after <paramref name="start"/> (starting below <paramref name="end"/>).</summary>
    private Fragment? ScanForward(SafeFileHandle h, long start, long end)
    {
        var buf = new byte[ScanWindow];
        long pos = Math.Max(Header.Length, start);
        for (long scanned = 0; pos < end && scanned < MaxScanBytes; scanned += ScanWindow)
        {
            int n = ReadAt(h, pos, buf);
            if (n < 24) return null;
            int limit = (int)Math.Min(n - 24, end - pos - 1);
            for (int i = 0; i <= limit; i++)
                if (buf[i + 4] == (byte)'m' && IsMoof(buf, i, n)) return FragmentAt(h, buf, i, pos + i, n);
            pos += n - 23; // overlap: a header across the window border is found in the next window
        }
        return null;
    }

    /// <summary>Last fragment of the file.</summary>
    private Fragment? ScanBackward(SafeFileHandle h, long len)
    {
        var buf = new byte[ScanWindow];
        for (long end = len; end > Header.Length && len - end < MaxScanBytes; end -= ScanWindow - 24)
        {
            long start = Math.Max(Header.Length, end - ScanWindow);
            int n = ReadAt(h, start, buf.AsSpan(0, (int)(end - start)));
            for (int i = n - 24; i >= 0; i--)
                if (buf[i + 4] == (byte)'m' && IsMoof(buf, i, n)) return FragmentAt(h, buf, i, start + i, n);
            if (start == Header.Length) break;
        }
        return null;
    }

    /// <summary>Fragment whose moof starts at <paramref name="i"/> in the buffer (= <paramref name="offset"/> in the file).</summary>
    private Fragment? FragmentAt(SafeFileHandle h, byte[] buf, int i, long offset, int n = -1)
    {
        if (n < 0) n = buf.Length;
        long moofSize = U32(buf, i);
        if (moofSize < 24 || moofSize > 4 * 1024 * 1024) return null;
        byte[] moof;
        if (i + moofSize <= n) moof = buf.AsSpan(i, (int)moofSize).ToArray();
        else
        {
            moof = new byte[moofSize];
            if (ReadAt(h, offset, moof) < moofSize) return null;
        }
        // mdat header right after the moof
        Span<byte> mh = stackalloc byte[16];
        if (i + moofSize + 16 <= n) buf.AsSpan((int)(i + moofSize), 16).CopyTo(mh);
        else if (ReadAt(h, offset + moofSize, mh) < 16) mh.Clear();
        long next = offset + moofSize;
        if (mh[4] == 'm' && mh[5] == 'd' && mh[6] == 'a' && mh[7] == 't')
        {
            long size = U32(mh, 0);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(mh[8..]);
            else if (size == 0) size = RandomAccess.GetLength(h) - next;
            next += size;
        }
        return new Fragment(U32(moof, 20), offset, next, VideoTicks(moof) / (double)_videoTimescale);
    }

    /// <summary>Sum of the video sample durations of a moof.</summary>
    private long VideoTicks(byte[] moof)
    {
        var span = moof.AsSpan();
        foreach (var (start, size) in Children(span, 8, "traf"))
        {
            var t = span.Slice(start, size);
            int tfhd = Find(t, 8, "tfhd");
            if (tfhd < 0 || U32(t, tfhd + 12) != _videoTrack) continue;
            uint fl = U32(t, tfhd + 8) & 0xFFFFFF;
            int q = tfhd + 16;
            if ((fl & 0x1) != 0) q += 8;
            if ((fl & 0x2) != 0) q += 4;
            uint defDur = (fl & 0x8) != 0 ? U32(t, q) : _videoDefaultDuration;
            long ticks = 0;
            foreach (var (rs, rsize) in Children(t, 8, "trun"))
            {
                var r = t.Slice(rs, rsize);
                uint rf = U32(r, 8) & 0xFFFFFF;
                uint count = U32(r, 12);
                int p = 16 + ((rf & 0x1) != 0 ? 4 : 0) + ((rf & 0x4) != 0 ? 4 : 0);
                if ((rf & 0x100) == 0) { ticks += (long)count * defDur; continue; }
                int stride = 4 + ((rf & 0x200) != 0 ? 4 : 0) + ((rf & 0x400) != 0 ? 4 : 0) + ((rf & 0x800) != 0 ? 4 : 0);
                for (int k = 0; k < count && p + k * stride + 4 <= r.Length; k++) ticks += U32(r, p + k * stride);
            }
            return ticks;
        }
        return 0;
    }

    private static bool IsMoof(byte[] b, int i, int n) =>
        i + 24 <= n && b[i + 4] == 'm' && b[i + 5] == 'o' && b[i + 6] == 'o' && b[i + 7] == 'f'
        && U32(b, i + 8) == 16 && b[i + 12] == 'm' && b[i + 13] == 'f' && b[i + 14] == 'h' && b[i + 15] == 'd'
        && U32(b, i) is >= 40 and <= 4 * 1024 * 1024;

    // =====================================================================
    // FFmpeg access starting at a fragment
    // =====================================================================

    /// <summary>
    /// Opens the file in FFmpeg so that demuxing starts at <paramref name="fragment"/>: FFmpeg sees
    /// ftyp + moov, a "free" box covering everything up to the fragment and the rest of the file.
    /// File offsets stay unchanged (the fragments address their data with absolute offsets).
    /// Time stamps of the returned context start at 0 for that fragment.
    /// </summary>
    public FragmentReader OpenAt(Fragment fragment) => new(this, fragment.Offset);

    // =====================================================================
    // Helpers
    // =====================================================================

    internal static int ReadAt(SafeFileHandle h, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = RandomAccess.Read(h, buffer[total..], offset + total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static string Type(ReadOnlySpan<byte> b, int o) => Encoding.ASCII.GetString(b.Slice(o, 4));

    private static int Find(ReadOnlySpan<byte> parent, int start, string type)
    {
        foreach (var c in Children(parent, start, type)) return c.Start;
        return -1;
    }

    private static List<(int Start, int Size)> Children(ReadOnlySpan<byte> parent, int start, string type)
    {
        var list = new List<(int, int)>();
        int p = start;
        while (p + 8 <= parent.Length)
        {
            int size = (int)U32(parent, p);
            if (size < 8 || p + size > parent.Length) break;
            if (Type(parent, p + 4) == type) list.Add((p, size));
            p += size;
        }
        return list;
    }
}

/// <summary>
/// FFmpeg demuxer context over a fragmented MP4 that starts at a given fragment
/// (custom I/O, see <see cref="FragmentedMp4.OpenAt"/>).
/// </summary>
public sealed unsafe class FragmentReader : IDisposable
{
    private const int IoBufferSize = 256 * 1024;

    private readonly SafeFileHandle _file;
    private readonly byte[] _header;
    private readonly long _skipFrom, _skipTo;
    private readonly byte[] _freeBox;
    private long _pos;
    // kept in fields: FFmpeg calls them through function pointers
    private readonly avio_alloc_context_read_packet _read;
    private readonly avio_alloc_context_seek _seek;
    private AVIOContext* _io;
    private AVFormatContext* _fmt;

    public AVFormatContext* Context => _fmt;

    internal FragmentReader(FragmentedMp4 file, long fragmentOffset)
    {
        _header = file.Header;
        _skipFrom = _header.Length;
        _skipTo = Math.Max(_skipFrom, fragmentOffset);
        long freeSize = _skipTo - _skipFrom;
        _freeBox = freeSize == 0 ? Array.Empty<byte>() : freeSize <= uint.MaxValue ? BoxHeader((uint)freeSize) : LargeBoxHeader(freeSize);
        _file = FragmentedMp4.OpenHandle(file.Path);
        _read = Read;
        _seek = Seek;
        try
        {
            var buffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
            _io = ffmpeg.avio_alloc_context(buffer, IoBufferSize, 0, null, _read, default, _seek);
            if (_io == null) { ffmpeg.av_free(buffer); throw new FFmpegException("avio_alloc_context"); }
            _fmt = ffmpeg.avformat_alloc_context();
            _fmt->pb = _io;
            // ignidx: don't read all fragment headers in advance (that is what makes opening slow)
            _fmt->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_IGNIDX;
            var fmt = _fmt;
            int ret = ffmpeg.avformat_open_input(&fmt, null, null, null);
            _fmt = ret < 0 ? null : fmt; // freed by avformat_open_input on failure
            if (ret < 0) throw new FFmpegException(FFmpegHelper.ErrorString(ret), ret);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static byte[] BoxHeader(uint size)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(b, size);
        "free"u8.CopyTo(b.AsSpan(4));
        return b;
    }

    private static byte[] LargeBoxHeader(long size)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(b, 1);
        "free"u8.CopyTo(b.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), (ulong)size);
        return b;
    }

    private int Read(void* opaque, byte* buf, int size)
    {
        try
        {
            var dst = new Span<byte>(buf, size);
            int total = 0;
            while (total < size)
            {
                var rest = dst[total..];
                int n;
                if (_pos < _skipFrom)
                {
                    n = (int)Math.Min(rest.Length, _skipFrom - _pos);
                    _header.AsSpan((int)_pos, n).CopyTo(rest);
                }
                else if (_pos < _skipTo)
                {
                    n = (int)Math.Min(rest.Length, _skipTo - _pos);
                    long inBox = _pos - _skipFrom;
                    rest[..n].Clear();
                    if (inBox < _freeBox.Length)
                        _freeBox.AsSpan((int)inBox, (int)Math.Min(n, _freeBox.Length - inBox)).CopyTo(rest);
                }
                else
                {
                    if (total > 0) break; // hand over what we have, read the file in the next call
                    n = RandomAccess.Read(_file, rest, _pos);
                    if (n <= 0) return ffmpeg.AVERROR_EOF;
                }
                _pos += n;
                total += n;
            }
            return total;
        }
        catch (Exception ex)
        {
            Log.Debug($"fMP4 read: {ex.Message}");
            return ffmpeg.AVERROR_EOF;
        }
    }

    private long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            switch (whence & ~ffmpeg.AVSEEK_FORCE)
            {
                case ffmpeg.AVSEEK_SIZE: return RandomAccess.GetLength(_file);
                case 0: _pos = offset; break; // SEEK_SET
                case 1: _pos += offset; break; // SEEK_CUR
                case 2: _pos = RandomAccess.GetLength(_file) + offset; break; // SEEK_END
                default: return -1;
            }
            return _pos;
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        if (_io != null)
        {
            ffmpeg.av_freep(&_io->buffer);
            var io = _io;
            ffmpeg.avio_context_free(&io);
            _io = null;
        }
        _file.Dispose();
    }
}
