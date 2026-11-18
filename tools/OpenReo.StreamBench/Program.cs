// OpenReo.StreamBench – QA tool: opens N real RTSP streams through the same stream layer
// the viewer uses and reports fps, decoder (GPU/CPU), CPU and RAM usage.
//
// Usage:
//   OpenReo.StreamBench --url rtsp://user:pass@ip:554/h264Preview_01_main [--url ...] [--count 25]
//                       [--seconds 30] [--target 768x432] [--hw auto|d3d11va|dxva2|cuda|software] [--udp]
// Multiple --url values are distributed round robin over the streams.

using System.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Video;

var urls = new List<string>();
int count = 1, seconds = 20, tw = 768, th = 432;
var hw = HardwareAccelerationMode.Auto;
var transport = RtspTransport.Tcp;
bool audio = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url": urls.Add(args[++i]); break;
        case "--count": count = int.Parse(args[++i]); break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--target":
            var p = args[++i].Split('x');
            tw = int.Parse(p[0]); th = int.Parse(p[1]);
            break;
        case "--hw": hw = Enum.Parse<HardwareAccelerationMode>(args[++i], true); break;
        case "--udp": transport = RtspTransport.Udp; break;
        case "--audio": audio = true; break;
    }
}
if (urls.Count == 0)
{
    Console.WriteLine("Usage: OpenReo.StreamBench --url rtsp://... [--count N] [--seconds S] [--target WxH] [--hw auto|software|...]");
    return 1;
}

FFmpegLoader.Initialize();
Console.WriteLine($"FFmpeg {FFmpegLoader.Version}, GPU decoders: {string.Join(", ", HardwareAcceleration.Shared.ProbeAvailable())}");

using var manager = new StreamManager(new StreamOptions(hw, transport, true, 10, 10), TimeSpan.Zero);
var leases = new List<StreamLease>();
for (int i = 0; i < count; i++)
{
    // Every stream gets its own "camera" so that each one is a separate RTSP connection.
    var url = urls[i % urls.Count];
    CameraConfig cam;
    if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':');
        cam = new CameraConfig
        {
            Name = $"Bench{i + 1}",
            Host = uri.Host,
            RtspPort = uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            ClearPath = uri.PathAndQuery.TrimStart('/'),
        };
    }
    else
    {
        // Local file or other FFmpeg URL (decoder throughput test without a camera).
        cam = new CameraConfig { Name = $"Bench{i + 1}", Username = "", ClearPath = url };
    }
    var lease = manager.Acquire(cam, StreamQuality.Clear);
    lease.SetTargetSize(tw, th);
    leases.Add(lease);
}

// Audio: the first stream gets a counting sink (expected: 48000 Hz * 2 ch * 2 bytes = 192000 bytes/s).
var audioSink = new CountingAudioSink();
if (audio && leases.Count > 0) leases[0].Stream.AudioSink = audioSink;

// Simulated UI thread: consumes frames at 60 Hz like the WPF render loop.
long consumed = 0;
var cts = new CancellationTokenSource();
var consumer = new Thread(() =>
{
    while (!cts.IsCancellationRequested)
    {
        foreach (var l in leases)
            if (l.Stream.Frames.TryAcquireLatest(out var f) && f.Data != IntPtr.Zero)
                Interlocked.Increment(ref consumed);
        Thread.Sleep(16);
    }
}) { IsBackground = true };
consumer.Start();

var proc = Process.GetCurrentProcess();
var lastCpu = proc.TotalProcessorTime;
var sw = Stopwatch.StartNew();
var lastT = sw.Elapsed;
double cpuSum = 0; int cpuSamples = 0;
for (int s = 1; s <= seconds; s++)
{
    Thread.Sleep(1000);
    proc.Refresh();
    var cpu = proc.TotalProcessorTime;
    var t = sw.Elapsed;
    double cpuPct = (cpu - lastCpu).TotalMilliseconds / (t - lastT).TotalMilliseconds / Environment.ProcessorCount * 100;
    lastCpu = cpu; lastT = t;
    var playing = leases.Count(l => l.Stream.Status == StreamStatus.Playing);
    var gpu = leases.Count(l => l.Stream.Statistics.IsHardware);
    var fps = leases.Sum(l => l.Stream.Statistics.Fps);
    if (s > 3) { cpuSum += cpuPct; cpuSamples++; }
    if (audio) Console.WriteLine($"       audio {leases[0].Stream.AudioCodec ?? "-"}: {Interlocked.Exchange(ref audioSink.Bytes, 0)} bytes/s PCM");
    Console.WriteLine($"[{s,3}s] playing {playing}/{count}  gpu {gpu}  total fps {fps,7:0.0}  CPU {cpuPct,5:0.0}%  RAM {proc.WorkingSet64 / 1048576,5} MB  presented {Interlocked.Exchange(ref consumed, 0)}");
}

foreach (var l in leases.Take(3).Concat(leases.Skip(3).Where(l => l.Stream.Status != StreamStatus.Playing)))
{
    var st = l.Stream.Statistics;
    Console.WriteLine($"  {l.Stream.Name}: {l.Stream.Status} {st.Codec} {st.Width}x{st.Height} -> {st.OutputWidth}x{st.OutputHeight} {st.Fps:0.0} fps {st.BitrateKbps:0} kbit/s {st.Decoder} errors {st.DecodeErrors} {l.Stream.LastError}");
}
Console.WriteLine($"Average CPU (after warm-up): {(cpuSamples > 0 ? cpuSum / cpuSamples : 0):0.0}% of {Environment.ProcessorCount} logical cores");
cts.Cancel();
foreach (var l in leases) l.Release(true);
return 0;

sealed class CountingAudioSink : IAudioSink
{
    public long Bytes;
    public void Write(ReadOnlySpan<byte> pcm) => Interlocked.Add(ref Bytes, pcm.Length);
}
