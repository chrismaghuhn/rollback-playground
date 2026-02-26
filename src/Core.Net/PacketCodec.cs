using Core.Sim;
using System.Buffers.Binary;

namespace Core.Net;

/// <summary>
/// Encodes and decodes RBN1 v1 UDP input packets.
///
/// Wire layout (little-endian) -- see <c>docs/NETCODE.md</c> for the full table:
///   [0..3]   Magic        "RBN1"
///   [4]      Version      = 1
///   [5]      Flags        bit0 = HasChecksum; bits 1-7 must be 0
///   [6..9]   StartFrame   uint32 LE
///   [10]     Count        uint8  (1..32)
///   [11..14] AckFrame     uint32 LE
///   -- present when Flags &amp; 1 ------------------------------------------
///   [15..18] ChecksumFrame  uint32 LE
///   [19..22] Checksum       uint32 LE  (SimHash FNV-1a 32-bit, caller-computed)
///   -- payload: Count x 2-byte ushort Buttons (LE) -------------------------
/// </summary>
public static class PacketCodec
{
    // -- Public constants -------------------------------------------------

    /// <summary>Maximum on-wire size: 23-byte header + 32 x 2-byte frames.</summary>
    public const int MaxPacketSize = 87;

    /// <summary>Maximum number of input frames per packet.</summary>
    public const int MaxInputsPerPacket = 32;

    // -- Private constants ------------------------------------------------

    private static ReadOnlySpan<byte> Magic        => "RBN1"u8;
    private const  byte               Version      = 1;
    private const  int                MinHeader    = 15;  // without checksum block
    private const  int                CsumBlock    = 8;   // ChecksumFrame + Checksum
    private const  int                FrameBytes   = 2;   // sizeof(ushort)
    private const  byte               FlagCsum     = 0x01;
    private const  byte               FlagReserved = 0xFE; // bits 1-7 must be zero

    // -- Encode -----------------------------------------------------------

    /// <summary>
    /// Encodes <paramref name="pkt"/> into <paramref name="dest"/>.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <c>Count</c> is outside [1, 32] or <paramref name="dest"/>
    /// is smaller than the required packet size.
    /// </exception>
    public static int Encode(in InputPacket pkt, Span<byte> dest)
    {
        if (pkt.Count < 1 || pkt.Count > MaxInputsPerPacket)
            throw new ArgumentException(
                $"Count must be 1..{MaxInputsPerPacket}, got {pkt.Count}.", nameof(pkt));

        byte flags      = pkt.HasChecksum ? FlagCsum : (byte)0;
        int  packetSize = (pkt.HasChecksum ? MinHeader + CsumBlock : MinHeader)
                          + pkt.Count * FrameBytes;

        if (dest.Length < packetSize)
            throw new ArgumentException(
                $"Buffer too small: need {packetSize} bytes, have {dest.Length}.",
                nameof(dest));

        // Fixed header
        Magic.CopyTo(dest);
        dest[4]  = Version;
        dest[5]  = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[6..],  pkt.StartFrame);
        dest[10] = pkt.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[11..], pkt.AckFrame);

        int pos = MinHeader;

        // Optional checksum block
        if (pkt.HasChecksum)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest[pos..],       pkt.ChecksumFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[(pos + 4)..], pkt.Checksum);
            pos += CsumBlock;
        }

        // Payload: N x ushort Buttons
        var inputs = pkt.InputsSpan;
        for (int i = 0; i < pkt.Count; i++, pos += FrameBytes)
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(pos, FrameBytes), inputs[i].Buttons);

        return packetSize;
    }

    // -- TryDecode --------------------------------------------------------

    /// <summary>
    /// Decodes <paramref name="src"/> into a new <see cref="InputPacket"/>.
    /// Allocates one <c>FrameInput[count]</c> on success.
    /// </summary>
    /// <returns>
    /// <c>false</c> (and <paramref name="packet"/> = <c>default</c>) on any
    /// validation failure.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<byte> src, out InputPacket packet)
    {
        packet = default;

        if (!TryParseHeader(src, out var header, out int count, out int payloadOffset))
            return false;

        var inputs = new FrameInput[count];
        FillInputs(src, payloadOffset, inputs.AsSpan());

        packet = new InputPacket(header, inputs, (byte)count);
        return true;
    }

    // -- TryDecodeInto ----------------------------------------------------

    /// <summary>
    /// Zero-alloc decode variant: fills caller-supplied <paramref name="dst"/>
    /// with decoded inputs and returns header metadata.
    /// </summary>
    /// <param name="src">Raw packet bytes.</param>
    /// <param name="dst">
    /// Caller-supplied buffer; must be at least <see cref="MaxInputsPerPacket"/>
    /// elements to guarantee it can always hold the decoded inputs.
    /// </param>
    /// <param name="header">Parsed header metadata on success.</param>
    /// <param name="count">Number of inputs written to <paramref name="dst"/>.</param>
    /// <returns><c>false</c> on any validation failure.</returns>
    public static bool TryDecodeInto(
        ReadOnlySpan<byte>    src,
        Span<FrameInput>      dst,
        out InputPacketHeader header,
        out int               count)
    {
        header = default;
        count  = 0;

        if (!TryParseHeader(src, out header, out count, out int payloadOffset))
            return false;

        if (dst.Length < count)
            return false;

        FillInputs(src, payloadOffset, dst[..count]);
        return true;
    }

    // -- Private helpers --------------------------------------------------

    /// <summary>
    /// Validates all header fields in spec order and returns parsed values.
    /// Validation order: size -> magic -> version -> flags -> count -> exact length.
    /// </summary>
    private static bool TryParseHeader(
        ReadOnlySpan<byte>    src,
        out InputPacketHeader header,
        out int               count,
        out int               payloadOffset)
    {
        header        = default;
        count         = 0;
        payloadOffset = 0;

        // 1. Minimum buffer size
        if (src.Length < MinHeader)             return false;
        // 2. Magic
        if (!src[..4].SequenceEqual(Magic))     return false;
        // 3. Version
        if (src[4] != Version)                  return false;
        // 4. Reserved flag bits must all be zero
        byte flags = src[5];
        if ((flags & FlagReserved) != 0)        return false;
        // 5. Count bounds
        int c = src[10];
        if (c < 1 || c > MaxInputsPerPacket)    return false;
        // 6. Exact packet length
        bool hasCsum     = (flags & FlagCsum) != 0;
        int  expectedLen = (hasCsum ? MinHeader + CsumBlock : MinHeader) + c * FrameBytes;
        if (src.Length != expectedLen)          return false;

        // Parse numeric header fields
        uint startFrame    = BinaryPrimitives.ReadUInt32LittleEndian(src[6..]);
        uint ackFrame      = BinaryPrimitives.ReadUInt32LittleEndian(src[11..]);
        uint checksumFrame = 0;
        uint checksum      = 0;
        int  dataStart     = MinHeader;

        if (hasCsum)
        {
            checksumFrame = BinaryPrimitives.ReadUInt32LittleEndian(src[15..]);
            checksum      = BinaryPrimitives.ReadUInt32LittleEndian(src[19..]);
            dataStart    += CsumBlock;
        }

        header = new InputPacketHeader
        {
            StartFrame    = startFrame,
            AckFrame      = ackFrame,
            HasChecksum   = hasCsum,
            ChecksumFrame = checksumFrame,
            Checksum      = checksum,
        };
        count         = c;
        payloadOffset = dataStart;
        return true;
    }

    /// <summary>
    /// Reads <paramref name="dst"/>.Length ushort values from
    /// <paramref name="src"/> starting at <paramref name="pos"/> and stores
    /// them as <see cref="FrameInput"/> values in <paramref name="dst"/>.
    /// </summary>
    private static void FillInputs(ReadOnlySpan<byte> src, int pos, Span<FrameInput> dst)
    {
        for (int i = 0; i < dst.Length; i++, pos += FrameBytes)
        {
            ushort buttons = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos, FrameBytes));
            dst[i] = new FrameInput(buttons);
        }
    }
}
