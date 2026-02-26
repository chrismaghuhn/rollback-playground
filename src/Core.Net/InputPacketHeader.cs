namespace Core.Net;

/// <summary>
/// Non-inputs metadata parsed from an RBN1 v1 packet header.
/// Used as the <c>out</c> parameter for the zero-alloc
/// <see cref="PacketCodec.TryDecodeInto"/> variant.
/// </summary>
public readonly struct InputPacketHeader
{
    /// <summary>Frame index of the first input in the packet.</summary>
    public uint StartFrame { get; init; }

    /// <summary>Latest frame index confirmed received from the remote peer.</summary>
    public uint AckFrame { get; init; }

    /// <summary><c>true</c> when the optional 8-byte checksum block is present.</summary>
    public bool HasChecksum { get; init; }

    /// <summary>
    /// Frame whose <c>SimHash</c> value is attached.
    /// Meaningful only when <see cref="HasChecksum"/> is <c>true</c>.
    /// </summary>
    public uint ChecksumFrame { get; init; }

    /// <summary>
    /// FNV-1a 32-bit SimHash of the game state at <see cref="ChecksumFrame"/>.
    /// Computed by the caller via <c>SimHash.Checksum(in SimState)</c>;
    /// the codec treats it as an opaque <c>uint</c>.
    /// Meaningful only when <see cref="HasChecksum"/> is <c>true</c>.
    /// </summary>
    public uint Checksum { get; init; }
}
