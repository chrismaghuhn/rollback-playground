using System.Buffers.Binary;
using Core.Replay;
using Core.Sim;

namespace Core.Replay.Tests;

public class ReplaySerializerTests
{
    // ── Deterministic input scripts (identical to ReplayPipelineTests) ────────

    private static readonly FrameInput Neutral = new(0);
    private static readonly FrameInput Left    = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput Right   = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput Jump    = FrameInput.FromButtons(false, false, true,  false);
    private static readonly FrameInput Attack  = FrameInput.FromButtons(false, false, false, true);

    private static FrameInput ScriptP1(uint f)
    {
        if (f < 50)   return Right;
        if (f == 50)  return Jump;
        if (f < 150)  return Right;
        if (f < 200)  return f % 20 == 0 ? Attack : Neutral;
        return Left;
    }

    private static FrameInput ScriptP2(uint f)
    {
        if (f < 100) return Left;
        if (f < 120) return Jump;
        return Neutral;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Replay BuildScriptedReplay(uint seed, int frames)
    {
        var recorder = new ReplayRecorder(seed);
        for (uint f = 0; f < (uint)frames; f++)
            recorder.Append(ScriptP1(f), ScriptP2(f));
        return recorder.Build();
    }

    private static byte[] SerializeToBytes(Replay replay)
    {
        using var ms = new MemoryStream();
        ReplaySerializer.Write(ms, replay);
        return ms.ToArray();
    }

    // ── 1) Full round-trip preserves every field and every frame ──────────────

    [Fact]
    public void RoundTrip_SerializeDeserialize_PreservesAllFields()
    {
        const uint seed   = 1u;
        const int  frames = 300;

        Replay original = BuildScriptedReplay(seed, frames);

        using var ms = new MemoryStream();
        ReplaySerializer.Write(ms, original);
        ms.Position = 0;
        Replay restored = ReplaySerializer.Read(ms);

        Assert.Equal(original.Seed,         restored.Seed);
        Assert.Equal(original.StartFrame,   restored.StartFrame);
        Assert.Equal(original.FrameCount,   restored.FrameCount);
        Assert.Equal(original.Frames.Count, restored.Frames.Count);

        for (int i = 0; i < original.FrameCount; i++)
        {
            Assert.Equal(original.Frames[i].P1.Buttons, restored.Frames[i].P1.Buttons);
            Assert.Equal(original.Frames[i].P2.Buttons, restored.Frames[i].P2.Buttons);
        }
    }

    // ── 2) Raw header bytes match the RPLK v1 spec exactly ───────────────────

    [Fact]
    public void Serialize_HeaderFields_AreCorrect_AndLengthMatches()
    {
        // Two precisely-chosen frames to allow deterministic byte-level checks.
        const uint seed = 999u;
        var recorder = new ReplayRecorder(seed);
        recorder.Append(Left,    Attack);  // frame 0: P1=Left(0x0001) P2=Attack(0x0008)
        recorder.Append(Neutral, Right);   // frame 1: P1=0x0000        P2=Right(0x0002)
        Replay replay = recorder.Build();

        byte[] bytes = SerializeToBytes(replay);

        // Total = 32-byte header + frameCount * 4 bytes payload
        Assert.Equal(32 + 2 * 4, bytes.Length);

        // ── Magic "RPLK" ──
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'L', bytes[2]);
        Assert.Equal((byte)'K', bytes[3]);

        // ── Version = 1 ──
        Assert.Equal(1, bytes[4]);

        // ── Flags = 0 ──
        Assert.Equal(0, bytes[5]);

        // ── HeaderSize = 32 (LE ushort) ──
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));

        // ── Seed (LE uint) ──
        Assert.Equal(seed, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));

        // ── StartFrame = 0 (LE uint) ──
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));

        // ── FrameCount = 2 (LE uint) ──
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));

        // ── Reserved bytes [24..31] must all be zero ──
        for (int i = 24; i < 32; i++)
            Assert.Equal(0, bytes[i]);

        // ── Payload: frame 0 — P1=Left(0x0001), P2=Attack(0x0008) ──
        Assert.Equal(FrameInput.Left,   BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32, 2)));
        Assert.Equal(FrameInput.Attack, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)));

        // ── Payload: frame 1 — P1=0x0000, P2=Right(0x0002) ──
        Assert.Equal((ushort)0,        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(36, 2)));
        Assert.Equal(FrameInput.Right, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(38, 2)));
    }

    // ── 3) Bad magic → InvalidDataException ──────────────────────────────────

    [Fact]
    public void Read_RejectsBadMagic()
    {
        byte[] bytes = SerializeToBytes(BuildScriptedReplay(1u, 10));
        bytes[0] = (byte)'X';  // corrupt first magic byte

        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ReplaySerializer.Read(ms));
    }

    // ── 4) Unsupported version → NotSupportedException ────────────────────────

    [Fact]
    public void Read_RejectsBadVersion()
    {
        byte[] bytes = SerializeToBytes(BuildScriptedReplay(1u, 10));
        bytes[4] = 2;  // version 2 is not supported

        using var ms = new MemoryStream(bytes);
        Assert.Throws<NotSupportedException>(() => ReplaySerializer.Read(ms));
    }

    // ── 5) CRC mismatch → InvalidDataException ───────────────────────────────

    [Fact]
    public void Read_RejectsCrcMismatch()
    {
        byte[] bytes = SerializeToBytes(BuildScriptedReplay(1u, 10));
        bytes[32] ^= 0xFF;  // flip all bits in the first payload byte

        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ReplaySerializer.Read(ms));
    }
}
