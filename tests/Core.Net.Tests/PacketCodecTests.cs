using Core.Net;
using Core.Sim;

namespace Core.Net.Tests;

public sealed class PacketCodecTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    private static InputPacket MakePacket(
        uint         startFrame,
        uint         ackFrame,
        FrameInput[] inputs,
        bool         hasChecksum   = false,
        uint         checksumFrame = 0,
        uint         checksum      = 0)
    {
        var header = new InputPacketHeader
        {
            StartFrame    = startFrame,
            AckFrame      = ackFrame,
            HasChecksum   = hasChecksum,
            ChecksumFrame = checksumFrame,
            Checksum      = checksum,
        };
        return new InputPacket(header, inputs, (byte)inputs.Length);
    }

    // ── 1: Round-trip ─────────────────────────────────────────────────────────

    [Fact]
    public void EncodeDecode_RoundTrip_PreservesFields()
    {
        // Without checksum — 2 inputs
        var inputs = new[]
        {
            new FrameInput(FrameInput.Left | FrameInput.Jump),
            new FrameInput(FrameInput.Right),
        };
        var pkt = MakePacket(startFrame: 100, ackFrame: 95, inputs: inputs);

        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        Assert.True(PacketCodec.TryDecode(buf[..written], out var decoded));
        Assert.Equal(pkt.StartFrame,     decoded.StartFrame);
        Assert.Equal(pkt.AckFrame,       decoded.AckFrame);
        Assert.Equal(pkt.Count,          decoded.Count);
        Assert.False(decoded.HasChecksum);
        Assert.Equal(inputs[0].Buttons,  decoded.InputsSpan[0].Buttons);
        Assert.Equal(inputs[1].Buttons,  decoded.InputsSpan[1].Buttons);

        // With checksum — 1 input
        var pkt2 = MakePacket(200, 195,
            inputs:        new[] { new FrameInput(FrameInput.Attack) },
            hasChecksum:   true,
            checksumFrame: 199,
            checksum:      0xDEADBEEF);
        int written2 = PacketCodec.Encode(in pkt2, buf);

        Assert.Equal(23 + 1 * 2, written2);   // 25 bytes
        Assert.True(PacketCodec.TryDecode(buf[..written2], out var decoded2));
        Assert.True(decoded2.HasChecksum);
        Assert.Equal(pkt2.ChecksumFrame,   decoded2.ChecksumFrame);
        Assert.Equal(pkt2.Checksum,        decoded2.Checksum);
        Assert.Equal(pkt2.AckFrame,        decoded2.AckFrame);
        Assert.Equal(pkt2.StartFrame,      decoded2.StartFrame);
        Assert.Equal(pkt2.InputsSpan[0].Buttons, decoded2.InputsSpan[0].Buttons);
    }

    // ── 2: Pinned byte layout ─────────────────────────────────────────────────

    [Fact]
    public void Encode_ByteLayout_IsPinned_ForKnownPacket()
    {
        // StartFrame=1, Count=1, AckFrame=2, Buttons=0x0003 (Left|Right), Flags=0
        var pkt = MakePacket(startFrame: 1, ackFrame: 2,
                             inputs: new[] { new FrameInput(0x0003) });

        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        Assert.Equal(17, written);

        byte[] expected =
        [
            0x52, 0x42, 0x4E, 0x31,  // "RBN1"
            0x01,                    // Version = 1
            0x00,                    // Flags   = 0
            0x01, 0x00, 0x00, 0x00,  // StartFrame = 1 LE
            0x01,                    // Count = 1
            0x02, 0x00, 0x00, 0x00,  // AckFrame = 2 LE
            0x03, 0x00,              // Buttons = 0x0003 LE
        ];
        Assert.Equal(expected, buf[..written].ToArray());
    }

    // ── 3: Reject bad magic ───────────────────────────────────────────────────

    [Fact]
    public void Decode_RejectsBadMagic()
    {
        var pkt = MakePacket(1, 0, new[] { new FrameInput(0) });
        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        buf[0] = (byte)'X';  // corrupt first magic byte

        Assert.False(PacketCodec.TryDecode(buf[..written], out _));
    }

    // ── 4: Reject bad version ─────────────────────────────────────────────────

    [Fact]
    public void Decode_RejectsBadVersion()
    {
        var pkt = MakePacket(1, 0, new[] { new FrameInput(0) });
        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        buf[4] = 0xFF;  // corrupt version byte

        Assert.False(PacketCodec.TryDecode(buf[..written], out _));
    }

    // ── 5: Reject wrong length ────────────────────────────────────────────────

    [Fact]
    public void Decode_RejectsWrongLength()
    {
        var pkt = MakePacket(1, 0, new[] { new FrameInput(0) });
        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        // Truncate by one byte — length no longer matches header's declared size
        Assert.False(PacketCodec.TryDecode(buf[..(written - 1)], out _));
    }

    // ── 6 (optional): Reject unknown / reserved flags ─────────────────────────

    [Fact]
    public void Decode_RejectsUnknownFlags()
    {
        var pkt = MakePacket(1, 0, new[] { new FrameInput(0) });
        Span<byte> buf = stackalloc byte[PacketCodec.MaxPacketSize];
        int written = PacketCodec.Encode(in pkt, buf);

        buf[5] = 0x02;  // set bit1 (currently reserved)

        Assert.False(PacketCodec.TryDecode(buf[..written], out _));
    }

    // ── 7 (optional): Reject count = 0 ───────────────────────────────────────

    [Fact]
    public void Decode_RejectsCountZero()
    {
        // Craft a 15-byte buffer: valid magic + version + flags=0, but Count=0.
        // expectedLen for Count=0 would be 15+0=15, so this must be caught by
        // the count-bounds check (step 5 in the validation order).
        Span<byte> buf = stackalloc byte[15];
        buf[0] = 0x52; buf[1] = 0x42; buf[2] = 0x4E; buf[3] = 0x31; // "RBN1"
        buf[4] = 0x01;  // Version = 1
        buf[5] = 0x00;  // Flags   = 0
        // buf[6..9]  zeroed => StartFrame = 0
        buf[10] = 0x00; // Count = 0  ← invalid
        // buf[11..14] zeroed => AckFrame = 0

        Assert.False(PacketCodec.TryDecode(buf, out _));
    }
}
