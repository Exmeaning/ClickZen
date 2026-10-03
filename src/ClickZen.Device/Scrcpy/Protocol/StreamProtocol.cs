using System.Buffers.Binary;
using System.Text;

namespace ClickZen.Device.Scrcpy.Protocol;

/// <summary>Video codec ids sent as the first 4 bytes of the video socket.</summary>
public enum VideoCodecId : uint
{
    H264 = 0x68323634,
    H265 = 0x68323635,
    Av1 = 0x00617631,
}

/// <summary>A 12-byte header on the video stream: either a session packet or a media packet header.</summary>
public readonly record struct StreamHeader
{
    /// <summary>Session packet (sent on start and on every rotation/resize).</summary>
    public bool IsSession { get; init; }

    // --- session packet
    public int Width { get; init; }
    public int Height { get; init; }
    public bool ClientResized { get; init; }

    // --- media packet
    public bool IsConfig { get; init; }
    public bool IsKeyFrame { get; init; }
    /// <summary>Presentation timestamp in microseconds (media packets).</summary>
    public long Pts { get; init; }
    public int PacketSize { get; init; }
}

/// <summary>
/// Parses the scrcpy 4.x video stream framing (doc/develop.md, "Video and audio").
/// <code>
/// session packet:  1000_0000 0000_0000 0000_0000 0000_000R | width u32 | height u32
/// media header:    0CK + 61-bit PTS (u64)                    | size u32  | payload
/// </code>
/// </summary>
public static class StreamProtocol
{
    public const int HeaderSize = 12;
    public const int DeviceNameFieldLength = 64;

    private const ulong MediaConfigFlag = 1UL << 62;
    private const ulong MediaKeyFrameFlag = 1UL << 61;
    private const ulong PtsMask = MediaKeyFrameFlag - 1;

    public static StreamHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            throw new ArgumentException("Header must be 12 bytes.", nameof(header));
        }

        if ((header[0] & 0x80) != 0)
        {
            return new StreamHeader
            {
                IsSession = true,
                ClientResized = (header[3] & 0x01) != 0,
                Width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[4..])),
                Height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[8..])),
            };
        }

        var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (size == 0 || size > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid media packet size {size}.");
        }

        return new StreamHeader
        {
            IsConfig = (ptsAndFlags & MediaConfigFlag) != 0,
            IsKeyFrame = (ptsAndFlags & MediaKeyFrameFlag) != 0,
            Pts = (long)(ptsAndFlags & PtsMask),
            PacketSize = (int)size,
        };
    }

    /// <summary>Device name from the 64-byte NUL-padded field sent on the first socket.</summary>
    public static string ParseDeviceName(ReadOnlySpan<byte> field)
    {
        var f = field.Length > DeviceNameFieldLength ? field[..DeviceNameFieldLength] : field;
        var nul = f.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? f[..nul] : f[..^1]);
    }

    public static VideoCodecId ParseCodecId(ReadOnlySpan<byte> four) => (VideoCodecId)BinaryPrimitives.ReadUInt32BigEndian(four);

    // ---- writers (used by tests and the fake server)

    public static void WriteSessionHeader(Span<byte> buf, int width, int height, bool clientResized = false)
    {
        buf[..HeaderSize].Clear();
        buf[0] = 0x80;
        buf[3] = clientResized ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(buf[8..], (uint)height);
    }

    public static void WriteMediaHeader(Span<byte> buf, long pts, bool config, bool keyFrame, int size)
    {
        var v = (ulong)pts & PtsMask;
        if (config)
        {
            v |= MediaConfigFlag;
        }

        if (keyFrame)
        {
            v |= MediaKeyFrameFlag;
        }

        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        BinaryPrimitives.WriteUInt32BigEndian(buf[8..], (uint)size);
    }
}
