using ClickZen.Device.Decoding;

namespace ClickZen.Device.Tests.Decoding;

public sealed class PacketMergerTests
{
    [Fact]
    public void Config_is_prepended_once_to_next_media_packet()
    {
        var m = new PacketMerger();
        Assert.Null(m.Merge(new byte[] { 1, 2 }, isConfig: true));

        var first = m.Merge(new byte[] { 9 }, isConfig: false);
        Assert.Equal(new byte[] { 1, 2, 9 }, first!.Value.ToArray());

        var second = m.Merge(new byte[] { 8 }, isConfig: false);
        Assert.Equal(new byte[] { 8 }, second!.Value.ToArray());
    }

    [Fact]
    public void Newer_config_replaces_older()
    {
        var m = new PacketMerger();
        m.Merge(new byte[] { 1 }, true);
        m.Merge(new byte[] { 2 }, true);
        Assert.Equal(new byte[] { 2, 7 }, m.Merge(new byte[] { 7 }, false)!.Value.ToArray());
    }

    [Fact]
    public void Reset_drops_pending_config()
    {
        var m = new PacketMerger();
        m.Merge(new byte[] { 1 }, true);
        m.Reset();
        Assert.Equal(new byte[] { 7 }, m.Merge(new byte[] { 7 }, false)!.Value.ToArray());
    }
}

/// <summary>
/// Decodes a real H.264 stream (generated with x264) through the FFmpeg DLLs bundled from scrcpy,
/// framed the way scrcpy-server frames it (config packet + one access unit per media packet).
/// </summary>
public sealed class FfmpegVideoDecoderTests
{
    private static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "Samples");

    private static string? FfmpegDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        var candidate = dir is null ? null : Path.Combine(dir.FullName, ".cache", "scrcpy-4.1", "scrcpy-win64-v4.1");
        return candidate is not null && File.Exists(Path.Combine(candidate, "avcodec-62.dll")) ? candidate : null;
    }

    /// <summary>Splits Annex-B into (isConfig, bytes) packets like scrcpy-server does.</summary>
    internal static List<(bool Config, bool Key, byte[] Data)> ScrcpyPackets(byte[] annexB)
    {
        var starts = new List<int>();
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                starts.Add(i > 0 && annexB[i - 1] == 0 ? i - 1 : i);
                i += 2;
            }
        }

        var nals = new List<(int Type, bool FirstSlice, byte[] Bytes)>();
        for (var k = 0; k < starts.Count; k++)
        {
            var s = starts[k];
            var e = k + 1 < starts.Count ? starts[k + 1] : annexB.Length;
            var hdr = annexB[s + 2] == 1 ? s + 3 : s + 4;
            // first_mb_in_slice is ue(v); its value is 0 exactly when the first bit is 1.
            var firstSlice = hdr + 1 < e && (annexB[hdr + 1] & 0x80) != 0;
            nals.Add((annexB[hdr] & 0x1F, firstSlice, annexB[s..e]));
        }

        var packets = new List<(bool, bool, byte[])>();
        var config = new List<byte>();
        var frame = new List<byte>();
        var key = false;
        foreach (var (type, firstSlice, bytes) in nals)
        {
            if (type is 7 or 8)
            {
                if (frame.Count > 0)
                {
                    packets.Add((false, key, frame.ToArray()));
                    frame.Clear();
                    key = false;
                }

                config.AddRange(bytes);
                continue;
            }

            if (config.Count > 0)
            {
                packets.Add((true, false, config.ToArray()));
                config.Clear();
            }

            // A VCL slice with first_mb_in_slice == 0 starts a new picture (x264 may emit several
            // slices per picture). SEI (6) and other non-VCL units belong to the next picture.
            if (type is 1 or 5 && firstSlice && ContainsVcl(frame))
            {
                packets.Add((false, key, frame.ToArray()));
                frame.Clear();
                key = false;
            }

            frame.AddRange(bytes);
            key |= type == 5;
        }

        if (frame.Count > 0)
        {
            packets.Add((false, key, frame.ToArray()));
        }

        return packets;
    }

    private static bool ContainsVcl(List<byte> frame)
    {
        for (var i = 0; i + 3 < frame.Count; i++)
        {
            if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1)
            {
                var t = frame[i + 3] & 0x1F;
                if (t is 1 or 5)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Decodes_scrcpy_framed_h264_stream()
    {
        var ffmpegDir = FfmpegDir();
        Assert.SkipWhen(ffmpegDir is null, "Bundled FFmpeg not available (run a build first).");
        FfmpegVideoDecoder.Initialize(ffmpegDir!);

        var packets = ScrcpyPackets(File.ReadAllBytes(Path.Combine(SamplesDir, "testsrc2_176x144_12f.h264")));
        Assert.Equal(2, packets.Count(p => p.Config));

        using var decoder = new FfmpegVideoDecoder();
        var pictures = new List<DecodedPicture>();
        var pts = 0L;
        foreach (var (config, key, data) in packets)
        {
            var pic = decoder.Decode(data, config, key, config ? 0 : pts += 33_333);
            if (pic is not null)
            {
                pictures.Add(pic);
            }
        }

        Assert.Equal(12, pictures.Count);
        Assert.All(pictures, p =>
        {
            Assert.Equal(176, p.Width);
            Assert.Equal(144, p.Height);
        });

        // Compare the first picture against ffmpeg's own BGRA output (saved as PNG).
        var reference = Vision_DecodePng(Path.Combine(SamplesDir, "testsrc2_frame0.png"));
        var first = pictures[0].Bgra;
        long diff = 0;
        for (var i = 0; i < first.Length; i += 4)
        {
            diff += Math.Abs(first[i] - reference[i]) + Math.Abs(first[i + 1] - reference[i + 1]) + Math.Abs(first[i + 2] - reference[i + 2]);
        }

        var meanPerChannel = diff / (double)(176 * 144 * 3);
        Assert.True(meanPerChannel < 3.0, $"Mean channel difference {meanPerChannel:F2} too large.");
    }

    [Fact]
    public void Corrupt_packet_does_not_throw()
    {
        var ffmpegDir = FfmpegDir();
        Assert.SkipWhen(ffmpegDir is null, "Bundled FFmpeg not available (run a build first).");
        FfmpegVideoDecoder.Initialize(ffmpegDir!);

        using var decoder = new FfmpegVideoDecoder();
        var garbage = Enumerable.Range(0, 500).Select(i => (byte)(i * 37)).ToArray();
        var ex = Record.Exception(() => decoder.Decode(garbage, false, false, 1));
        Assert.Null(ex);
    }

    /// <summary>Minimal PNG → BGRA via System.Drawing-free path: WIC is unavailable in tests, so use the Windows imaging API through WinRT.</summary>
    private static byte[] Vision_DecodePng(string path)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        var pixels = decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        return pixels.DetachPixelData();
    }
}
