using Core.Sim;
using System.Buffers.Binary;

namespace Core.Net;

/// <summary>
/// Encodes and decodes RBN1 v1 UDP input packets.
/// Wire layout documented in <c>docs/NETCODE.md</c>.
/// </summary>
public static class PacketCodec
{
    /// <summary>Maximum on-wire size: 23-byte header + 32 × 2-byte payload.</summary>
    public const int MaxPacketSize = 87;

    /// <summary>Maximum number of input frames per packet.</summary>
    public const int MaxInputsPerPacket = 32;

    /// <summary>Encodes <paramref name="pkt"/> into <paramref name="dest"/>. Returns bytes written.</summary>
    public static int Encode(in InputPacket pkt, Span<byte> dest) =>
        throw new NotImplementedException();

    /// <summary>
    /// Decodes <paramref name="src"/> into a new <see cref="InputPacket"/>.
    /// Allocates one <c>FrameInput[count]</c> on success.
    /// Returns <c>false</c> on any validation failure.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> src, out InputPacket packet)
    {
        packet = default;
        throw new NotImplementedException();
    }

    /// <summary>
    /// Zero-alloc decode variant: fills caller-supplied <paramref name="dst"/>.
    /// Returns <c>false</c> on the same conditions as <see cref="TryDecode"/>.
    /// </summary>
    public static bool TryDecodeInto(
        ReadOnlySpan<byte>    src,
        Span<FrameInput>      dst,
        out InputPacketHeader header,
        out int               count)
    {
        header = default;
        count  = 0;
        throw new NotImplementedException();
    }
}
