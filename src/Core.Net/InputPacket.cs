using Core.Sim;

namespace Core.Net;

/// <summary>
/// Complete RBN1 v1 input packet: header metadata + decoded inputs.
///
/// The internal storage is a <c>FrameInput[]</c>; the external surface exposes
/// only a <see cref="ReadOnlySpan{T}"/> so callers are decoupled from the array.
/// Future migration to <c>ArrayPool</c> or <c>InlineArray</c> will not require
/// any change at call sites.
/// </summary>
public readonly struct InputPacket
{
    private readonly FrameInput[] _inputs;

    /// <summary>Header metadata (start frame, ack, optional checksum block).</summary>
    public InputPacketHeader Header { get; }

    /// <summary>Number of frames in this packet.  Range [1, 32].</summary>
    public byte Count { get; }

    /// <summary>Decoded input frames; length equals <see cref="Count"/>.</summary>
    public ReadOnlySpan<FrameInput> InputsSpan => _inputs.AsSpan(0, Count);

    // ── Convenience forwarding — avoids pkt.Header.X at every call site ──────

    /// <inheritdoc cref="InputPacketHeader.StartFrame"/>
    public uint StartFrame    => Header.StartFrame;

    /// <inheritdoc cref="InputPacketHeader.AckFrame"/>
    public uint AckFrame      => Header.AckFrame;

    /// <inheritdoc cref="InputPacketHeader.HasChecksum"/>
    public bool HasChecksum   => Header.HasChecksum;

    /// <inheritdoc cref="InputPacketHeader.ChecksumFrame"/>
    public uint ChecksumFrame => Header.ChecksumFrame;

    /// <inheritdoc cref="InputPacketHeader.Checksum"/>
    public uint Checksum      => Header.Checksum;

    /// <summary>
    /// Constructs an <see cref="InputPacket"/>.
    /// </summary>
    /// <param name="header">Parsed or constructed header metadata.</param>
    /// <param name="inputs">Backing array.  Must not be <c>null</c>.</param>
    /// <param name="count">
    /// Number of valid entries in <paramref name="inputs"/> to expose via
    /// <see cref="InputsSpan"/>.  Kept separate from <c>inputs.Length</c> so
    /// <see cref="PacketCodec.TryDecode"/> can construct without forcing
    /// an exact-length allocation.
    /// </param>
    public InputPacket(InputPacketHeader header, FrameInput[] inputs, byte count)
    {
        Header  = header;
        _inputs = inputs;
        Count   = count;
    }
}
