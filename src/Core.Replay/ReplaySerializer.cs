using System.Buffers.Binary;
using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Serializes and deserializes <see cref="Replay"/> values to/from the binary
/// RPLK v1 stream format.
///
/// ── Format summary (RPLK v1, all integers little-endian) ─────────────────────
///
/// Header — 32 bytes:
///   [0..3]  Magic      : ASCII "RPLK"  (0x52 0x50 0x4C 0x4B)
///   [4]     Version    : byte  = 1
///   [5]     Flags      : byte  = 0  (reserved)
///   [6..7]  HeaderSize : LE ushort = 32
///   [8..11] Seed       : LE uint
///   [12..15]StartFrame : LE uint
///   [16..19]FrameCount : LE uint
///   [20..23]PayloadCrc : LE uint  (CRC32/IEEE over payload bytes only)
///   [24..31]Reserved   : 8 bytes = 0
///
/// Payload — FrameCount × 4 bytes:
///   Per frame: P1.Buttons (LE ushort) | P2.Buttons (LE ushort)
///
/// ── Format stability ─────────────────────────────────────────────────────────
///
/// The magic and version byte provide a version gate: any format change that is
/// not backward-compatible must bump <see cref="FormatVersion"/>.  The CRC32
/// over the payload detects single-bit corruption and partial writes.
/// Both together make stored replays "safe to open": the reader either
/// succeeds with correct data or throws a typed exception — it never silently
/// produces garbage.
///
/// ── No unsafe code ───────────────────────────────────────────────────────────
///
/// All integer encoding uses <see cref="BinaryPrimitives"/> (explicit LE), which
/// requires no unsafe blocks or <c>MemoryMarshal</c> struct reinterpretation.
/// </summary>
public static class ReplaySerializer
{
    // ── Format constants ──────────────────────────────────────────────────────

    private static readonly byte[] Magic = [(byte)'R', (byte)'P', (byte)'L', (byte)'K'];
    private const byte   FormatVersion = 1;
    private const byte   FormatFlags   = 0;
    private const ushort FormatHeaderSize = 32;

    // Byte offsets inside the fixed-length header.
    private const int OffMagic       = 0;   // 4 bytes
    private const int OffVersion     = 4;   // 1 byte
    private const int OffFlags       = 5;   // 1 byte
    private const int OffHeaderSize  = 6;   // 2 bytes (LE ushort)
    private const int OffSeed        = 8;   // 4 bytes (LE uint)
    private const int OffStartFrame  = 12;  // 4 bytes (LE uint)
    private const int OffFrameCount  = 16;  // 4 bytes (LE uint)
    private const int OffPayloadCrc  = 20;  // 4 bytes (LE uint)
    // bytes [24..31] are reserved zeros.

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="replay"/> into <paramref name="stream"/>
    /// using the RPLK v1 binary format.
    /// </summary>
    /// <param name="stream">Writable stream; position advances by HeaderSize + FrameCount×4.</param>
    /// <param name="replay">The replay to write.  <see cref="Replay.StartFrame"/> must be 0.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    ///   <paramref name="replay"/>.<see cref="Replay.StartFrame"/> is non-zero
    ///   (mid-session replays are not yet supported).
    /// </exception>
    public static void Write(Stream stream, Replay replay)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(replay);

        if (replay.StartFrame != 0u)
            throw new NotSupportedException(
                $"Mid-session replays (StartFrame={replay.StartFrame}) are not supported. " +
                "Write requires StartFrame=0.");

        // ── 1. Encode payload (need it before CRC) ────────────────────────────
        int frameCount = replay.FrameCount;
        byte[] payload = new byte[frameCount * 4];

        IReadOnlyList<ReplayFrame> frames = replay.Frames;
        for (int i = 0; i < frameCount; i++)
        {
            ReplayFrame frame = frames[i];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 4,     2), frame.P1.Buttons);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 4 + 2, 2), frame.P2.Buttons);
        }

        uint crc = InternalCrc32.Compute(payload);

        // ── 2. Build header ───────────────────────────────────────────────────
        // Stack-allocated; 32 bytes is always safe on the stack.
        Span<byte> header = stackalloc byte[FormatHeaderSize];
        header.Clear();

        header[OffMagic + 0] = Magic[0];
        header[OffMagic + 1] = Magic[1];
        header[OffMagic + 2] = Magic[2];
        header[OffMagic + 3] = Magic[3];
        header[OffVersion]   = FormatVersion;
        header[OffFlags]     = FormatFlags;

        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(OffHeaderSize, 2), FormatHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(OffSeed,       4), replay.Seed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(OffStartFrame, 4), replay.StartFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(OffFrameCount, 4), (uint)frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(OffPayloadCrc, 4), crc);
        // Reserved bytes [24..31] remain zero from header.Clear().

        // ── 3. Write header then payload ──────────────────────────────────────
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>
    /// Deserializes a <see cref="Replay"/> from <paramref name="stream"/>,
    /// validating the magic, version, header size, payload length, and CRC32.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of an RPLK record.</param>
    /// <returns>The deserialized <see cref="Replay"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">
    ///   Magic bytes are wrong, <see cref="FormatHeaderSize"/> is unexpected,
    ///   payload is truncated, or CRC32 does not match.
    /// </exception>
    /// <exception cref="NotSupportedException">Version byte is not <see cref="FormatVersion"/>.</exception>
    /// <exception cref="EndOfStreamException">Stream ends before the expected number of bytes.</exception>
    public static Replay Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // ── 1. Read and validate header ───────────────────────────────────────
        Span<byte> header = stackalloc byte[FormatHeaderSize];
        ReadExact(stream, header);

        // Magic
        if (header[OffMagic + 0] != Magic[0] ||
            header[OffMagic + 1] != Magic[1] ||
            header[OffMagic + 2] != Magic[2] ||
            header[OffMagic + 3] != Magic[3])
        {
            throw new InvalidDataException(
                $"Not a valid RPLK file: magic bytes are " +
                $"0x{header[0]:X2} 0x{header[1]:X2} 0x{header[2]:X2} 0x{header[3]:X2}, " +
                $"expected 0x52 0x50 0x4C 0x4B ('RPLK').");
        }

        // Version
        byte version = header[OffVersion];
        if (version != FormatVersion)
            throw new NotSupportedException(
                $"RPLK version {version} is not supported. Only version {FormatVersion} is supported.");

        // HeaderSize (forward-compatibility guard)
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(OffHeaderSize, 2));
        if (headerSize != FormatHeaderSize)
            throw new InvalidDataException(
                $"Unexpected RPLK header size {headerSize}; expected {FormatHeaderSize}.");

        // Read remaining header fields
        uint seed       = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(OffSeed,       4));
        uint startFrame = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(OffStartFrame, 4));
        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(OffFrameCount, 4));
        uint storedCrc  = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(OffPayloadCrc, 4));

        // ── 2. Read payload ───────────────────────────────────────────────────
        byte[] payload = new byte[checked((int)frameCount * 4)];
        ReadExact(stream, payload);

        // ── 3. Verify CRC ─────────────────────────────────────────────────────
        uint computedCrc = InternalCrc32.Compute(payload);
        if (computedCrc != storedCrc)
            throw new InvalidDataException(
                $"RPLK payload CRC32 mismatch: stored=0x{storedCrc:X8}, computed=0x{computedCrc:X8}. " +
                "The file may be corrupted.");

        // ── 4. Decode frames ──────────────────────────────────────────────────
        var replayFrames = new ReplayFrame[frameCount];
        for (int i = 0; i < (int)frameCount; i++)
        {
            ushort p1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 4,     2));
            ushort p2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 4 + 2, 2));
            replayFrames[i] = new ReplayFrame(new FrameInput(p1), new FrameInput(p2));
        }

        return new Replay(seed, startFrame, replayFrames);
    }

    /// <summary>
    /// Convenience wrapper: creates or overwrites a file at <paramref name="path"/>
    /// and writes the RPLK v1 record.
    /// </summary>
    public static void WriteToFile(string path, Replay replay)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, replay);
    }

    /// <summary>
    /// Convenience wrapper: opens <paramref name="path"/> for reading and
    /// deserializes the RPLK v1 record.
    /// </summary>
    public static Replay ReadFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads exactly <c>buffer.Length</c> bytes from <paramref name="stream"/>,
    /// retrying on short reads (e.g. buffered streams).
    /// Throws <see cref="EndOfStreamException"/> if the stream ends early.
    /// </summary>
    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(total));
            if (n == 0)
                throw new EndOfStreamException(
                    $"Expected {buffer.Length} bytes but stream ended after {total} bytes.");
            total += n;
        }
    }

    // ── Internal CRC32 (IEEE 802.3, polynomial 0xEDB88320, bit-reflected) ─────
    //
    // System.IO.Hashing.Crc32 is not automatically referenced in net10.0 without
    // an explicit NuGet dependency.  This minimal table-based implementation is
    // self-contained, allocation-free per call, and produces the standard
    // ISO 3309 / ITU-T V.42 CRC-32 used by gzip, Ethernet, and zip.
    //
    // Reference: https://www.w3.org/TR/PNG/#D-CRCAppendix

    private static class InternalCrc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256u; i++)
            {
                uint entry = i;
                for (int bit = 0; bit < 8; bit++)
                    entry = (entry & 1u) != 0u ? (entry >> 1) ^ 0xEDB88320u : entry >> 1;
                table[i] = entry;
            }
            return table;
        }

        /// <summary>Computes the CRC32 of <paramref name="data"/>.</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFF_FFFFu;
            for (int i = 0; i < data.Length; i++)
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFFu];
            return crc ^ 0xFFFF_FFFFu;
        }
    }
}
