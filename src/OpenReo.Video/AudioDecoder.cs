using FFmpeg.AutoGen;
using OpenReo.Core.Localization;

namespace OpenReo.Video;

/// <summary>Output format of the decoded audio.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
}

/// <summary>Audio output that can drop its buffered (not yet heard) audio, e.g. after a seek.</summary>
public interface IAudioOutput
{
    /// <summary>Delay between handing audio over and hearing it (sound card buffers).</summary>
    TimeSpan OutputLatency { get; }

    /// <summary>Discards audio that was handed over but not played yet.</summary>
    void Flush();
}

/// <summary>Receives decoded PCM audio (48 kHz, stereo, signed 16 bit, interleaved).</summary>
public interface IAudioSink
{
    /// <summary>Called on the stream's worker thread.</summary>
    void Write(ReadOnlySpan<byte> pcm);
}

/// <summary>
/// Decodes the camera's audio track (Reolink: AAC; others: G.711, ADPCM …) and converts it to
/// 48 kHz stereo S16 for the sound card.
/// </summary>
internal sealed unsafe class AudioDecoder : IDisposable
{
    public const int OutputRate = AudioFormat.SampleRate;
    public const int OutputChannels = AudioFormat.Channels;

    private AVCodecContext* _ctx;
    private SwrContext* _swr;
    private AVFrame* _frame;
    private int _swrInRate, _swrInFormat, _swrInChannels;
    private byte[] _buffer = new byte[16384];

    public string CodecName { get; }

    public AudioDecoder(AVCodecParameters* par)
    {
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null) throw new FFmpegException(Lang.F("Kein Audio-Decoder für {0}.", ffmpeg.avcodec_get_name(par->codec_id)));
        CodecName = ffmpeg.avcodec_get_name(par->codec_id)?.ToUpperInvariant() ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        FFmpegHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(_ctx, par), "Audio-Parameter");
        FFmpegHelper.ThrowIfError(ffmpeg.avcodec_open2(_ctx, codec, null), "Audio-Decoder");
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Decodes one packet and writes the resulting PCM into <paramref name="sink"/>.</summary>
    public void Decode(AVPacket* packet, IAudioSink sink)
    {
        if (ffmpeg.avcodec_send_packet(_ctx, packet) < 0) return; // corrupt audio packets are simply skipped
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            try { Convert(_frame, sink); }
            finally { ffmpeg.av_frame_unref(_frame); }
        }
    }

    private void Convert(AVFrame* f, IAudioSink sink)
    {
        if (f->nb_samples <= 0 || f->sample_rate <= 0) return;
        if (_swr == null || f->sample_rate != _swrInRate || f->format != _swrInFormat || f->ch_layout.nb_channels != _swrInChannels)
        {
            if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, OutputChannels);
            SwrContext* swr = null;
            int ret = ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutputRate,
                &f->ch_layout, (AVSampleFormat)f->format, f->sample_rate, 0, null);
            if (ret < 0 || swr == null || ffmpeg.swr_init(swr) < 0)
            {
                if (swr != null) ffmpeg.swr_free(&swr);
                return;
            }
            _swr = swr;
            _swrInRate = f->sample_rate;
            _swrInFormat = f->format;
            _swrInChannels = f->ch_layout.nb_channels;
        }

        int maxOut = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(_swr, f->sample_rate) + f->nb_samples, OutputRate, f->sample_rate, AVRounding.AV_ROUND_UP);
        int bytesNeeded = maxOut * OutputChannels * 2;
        if (_buffer.Length < bytesNeeded) _buffer = new byte[bytesNeeded * 2];

        int samples;
        fixed (byte* p = _buffer)
        {
            byte* outPtr = p;
            samples = ffmpeg.swr_convert(_swr, &outPtr, maxOut, f->extended_data, f->nb_samples);
        }
        if (samples > 0) sink.Write(_buffer.AsSpan(0, samples * OutputChannels * 2));
    }

    public void Dispose()
    {
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_frame != null) { var fr = _frame; ffmpeg.av_frame_free(&fr); _frame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}
