namespace ClickZen.Device.Decoding;

/// <summary>
/// H.264/H.265 config packets (SPS/PPS) must be prepended to the next media packet before decoding
/// (same as scrcpy's packet_merger.c). Not thread-safe; used by the single decode thread.
/// </summary>
public sealed class PacketMerger
{
    private byte[]? _config;

    /// <summary>
    /// Feeds one packet. Config packets are stored and produce nothing; media packets are returned,
    /// with any pending config prepended.
    /// </summary>
    public ReadOnlyMemory<byte>? Merge(ReadOnlyMemory<byte> packet, bool isConfig)
    {
        if (isConfig)
        {
            _config = packet.ToArray();
            return null;
        }

        if (_config is null)
        {
            return packet;
        }

        var merged = new byte[_config.Length + packet.Length];
        _config.CopyTo(merged, 0);
        packet.Span.CopyTo(merged.AsSpan(_config.Length));
        _config = null;
        return merged;
    }

    public void Reset() => _config = null;
}
