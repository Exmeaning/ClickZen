using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ClickZen.Device.Decoding;

/// <summary>A decoded picture in BGRA, owned by the caller.</summary>
public sealed record DecodedPicture(int Width, int Height, byte[] Bgra, long Pts);

/// <summary>
/// Software H.264/H.265 decoder over FFmpeg (avcodec only – the scrcpy build ships no parser
/// and no swscale, neither is needed: scrcpy already frames access units, and YUV→BGRA is done
/// by <see cref="YuvConverter"/>). One instance per stream; not thread-safe.
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IDisposable
{
    private static readonly Lock InitLock = new();
    private static bool _initialized;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private readonly PacketMerger _merger = new();
    private bool _disposed;

    /// <param name="codec">AV_CODEC_ID_H264 or AV_CODEC_ID_HEVC.</param>
    public FfmpegVideoDecoder(AVCodecID codec = AVCodecID.AV_CODEC_ID_H264)
    {
        var decoder = ffmpeg.avcodec_find_decoder(codec);
        if (decoder == null)
        {
            throw new NotSupportedException($"FFmpeg has no decoder for {codec}.");
        }

        _ctx = ffmpeg.avcodec_alloc_context3(decoder);
        if (_ctx == null)
        {
            throw new OutOfMemoryException("avcodec_alloc_context3 failed.");
        }

        // Lowest latency: no frame reordering delay, use all cores for slices.
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;

        Check(ffmpeg.avcodec_open2(_ctx, decoder, null), "avcodec_open2");
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Points FFmpeg.AutoGen at the folder holding avcodec-62.dll / avutil-60.dll. Call once before
    /// creating decoders; safe to call repeatedly.
    /// </summary>
    public static void Initialize(string ffmpegDirectory)
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            ffmpeg.RootPath = ffmpegDirectory;
            // Touch the library so a missing/mismatched DLL fails here with a clear message.
            var version = ffmpeg.avcodec_version() >> 16;
            if (version != 62)
            {
                throw new NotSupportedException($"Unexpected avcodec major version {version} (expected 62).");
            }

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _initialized = true;
        }
    }

    /// <summary>Decodes one scrcpy media packet. Returns a picture, or null when the decoder needs more data.</summary>
    public DecodedPicture? Decode(ReadOnlyMemory<byte> data, bool isConfig, bool isKeyFrame, long pts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var merged = _merger.Merge(data, isConfig);
        if (merged is null)
        {
            return null;
        }

        var bytes = merged.Value;
        Check(ffmpeg.av_new_packet(_packet, bytes.Length), "av_new_packet");
        try
        {
            bytes.Span.CopyTo(new Span<byte>(_packet->data, bytes.Length));
            _packet->pts = pts;
            _packet->dts = pts;
            if (isKeyFrame)
            {
                _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
            }

            var send = ffmpeg.avcodec_send_packet(_ctx, _packet);
            if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // A corrupt packet should not kill the stream; wait for the next key frame.
                return null;
            }
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }

        DecodedPicture? latest = null;
        while (true)
        {
            var r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            Check(r, "avcodec_receive_frame");
            latest = Convert(_frame);
            ffmpeg.av_frame_unref(_frame);
        }

        return latest;
    }

    /// <summary>Drops pending config and decoder state (call on a new session, e.g. after rotation).</summary>
    public void Flush()
    {
        _merger.Reset();
        if (_ctx != null)
        {
            ffmpeg.avcodec_flush_buffers(_ctx);
        }
    }

    private static DecodedPicture Convert(AVFrame* f)
    {
        int w = f->width, h = f->height;
        var bgra = new byte[w * h * 4];
        var cs = f->colorspace == AVColorSpace.AVCOL_SPC_BT709 ? YuvColorSpace.Bt709Limited
            : f->color_range == AVColorRange.AVCOL_RANGE_JPEG ? YuvColorSpace.Bt601Full
            : YuvColorSpace.Bt601Limited;

        var fmt = (AVPixelFormat)f->format;
        var chromaH = (h + 1) / 2;
        switch (fmt)
        {
            case AVPixelFormat.AV_PIX_FMT_YUV420P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ420P:
                YuvConverter.I420ToBgra(
                    new ReadOnlySpan<byte>(f->data[0], f->linesize[0] * h), f->linesize[0],
                    new ReadOnlySpan<byte>(f->data[1], f->linesize[1] * chromaH), f->linesize[1],
                    new ReadOnlySpan<byte>(f->data[2], f->linesize[2] * chromaH), f->linesize[2],
                    bgra, w, h, fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P ? YuvColorSpace.Bt601Full : cs);
                break;
            case AVPixelFormat.AV_PIX_FMT_NV12:
                YuvConverter.Nv12ToBgra(
                    new ReadOnlySpan<byte>(f->data[0], f->linesize[0] * h), f->linesize[0],
                    new ReadOnlySpan<byte>(f->data[1], f->linesize[1] * chromaH), f->linesize[1],
                    bgra, w, h, cs);
                break;
            default:
                throw new NotSupportedException($"Unsupported decoder pixel format {fmt}.");
        }

        return new DecodedPicture(w, h, bgra, f->pts);
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            var buf = stackalloc byte[256];
            ffmpeg.av_strerror(result, buf, 256);
            throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buf)} ({result})");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        var ctx = _ctx;
        ffmpeg.avcodec_free_context(&ctx);
        _ctx = null;
    }
}
