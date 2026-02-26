# Core.Net Packet Codec (v0.3-1) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `Core.Net` library + `Core.Net.Tests` with a complete RBN1 v1 UDP packet
codec (encode / decode / validate), 0 warnings, 75 tests green, `docs/NETCODE.md` present.

**Architecture:** Static `PacketCodec` reads/writes with `Span<byte>` +
`BinaryPrimitives`. Two value types: `InputPacketHeader` (metadata only, used by the
zero-alloc path) and `InputPacket` (wraps internal `FrameInput[]`, exposes only
`ReadOnlySpan<FrameInput> InputsSpan`). `TryDecodeInto` gives callers a zero-alloc
escape hatch without breaking the standard API.

**Tech Stack:** C# / .NET 8 + 10, xUnit 2.9.3, `System.Buffers.Binary.BinaryPrimitives`,
`"RBN1"u8` UTF-8 literal for magic.

---

## Task 1: Project scaffolding

**Files:**
- Create: `src/Core.Net/Core.Net.csproj`
- Create: `tests/Core.Net.Tests/Core.Net.Tests.csproj`
- Modify: `RollbackPlayground.sln` (via `dotnet sln add`)

### Step 1: Create `src/Core.Net/Core.Net.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Core.Sim/Core.Sim.csproj" />
  </ItemGroup>
</Project>
```

### Step 2: Create `tests/Core.Net.Tests/Core.Net.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk"       Version="17.14.1" />
    <PackageReference Include="xunit"                        Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio"    Version="3.1.4">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector"           Version="6.0.4">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Core.Net/Core.Net.csproj" />
    <ProjectReference Include="../../src/Core.Sim/Core.Sim.csproj" />
  </ItemGroup>
</Project>
```

### Step 3: Add to solution

Run from the repo root (`C:\Users\chris\Documents\neues projekt\`):

```
dotnet sln RollbackPlayground.sln add src/Core.Net/Core.Net.csproj
dotnet sln RollbackPlayground.sln add tests/Core.Net.Tests/Core.Net.Tests.csproj
```

### Step 4: Smoke-build

```
dotnet build RollbackPlayground.sln
```

Expected: possible CS warning about no source files in Core.Net — that is OK. 0 errors.

### Step 5: Commit

```
git add src/Core.Net/Core.Net.csproj tests/Core.Net.Tests/Core.Net.Tests.csproj RollbackPlayground.sln
git commit -m "chore: scaffold Core.Net + Core.Net.Tests projects"
```

---

## Task 2: Type definitions

**Files:**
- Create: `src/Core.Net/InputPacketHeader.cs`
- Create: `src/Core.Net/InputPacket.cs`

### Step 1: Create `src/Core.Net/InputPacketHeader.cs`

```csharp
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
```

### Step 2: Create `src/Core.Net/InputPacket.cs`

```csharp
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
```

### Step 3: Verify build

```
dotnet build RollbackPlayground.sln
```

Expected: 0 warnings (CS8618 not possible — struct has no nullable fields), 0 errors.

### Step 4: Commit

```
git add src/Core.Net/InputPacketHeader.cs src/Core.Net/InputPacket.cs
git commit -m "feat(net): add InputPacketHeader + InputPacket value types"
```

---

## Task 3: PacketCodec stub + all tests (TDD red phase)

**Files:**
- Create: `src/Core.Net/PacketCodec.cs` (stub — method signatures, all throw `NotImplementedException`)
- Create: `tests/Core.Net.Tests/PacketCodecTests.cs`

### Step 1: Create `src/Core.Net/PacketCodec.cs` (stub)

```csharp
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
```

### Step 2: Create `tests/Core.Net.Tests/PacketCodecTests.cs`

```csharp
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
```

### Step 3: Build — must compile, tests must fail at runtime

```
dotnet build RollbackPlayground.sln
dotnet test RollbackPlayground.sln --filter "FullyQualifiedName~PacketCodecTests" --no-build
```

Expected: build succeeds; all 7 tests throw `NotImplementedException` (red).

### Step 4: Commit stub + tests

```
git add src/Core.Net/PacketCodec.cs tests/Core.Net.Tests/PacketCodecTests.cs
git commit -m "test(net): add PacketCodecTests — 7 tests, red phase"
```

---

## Task 4: PacketCodec implementation (TDD green phase)

**Files:**
- Modify: `src/Core.Net/PacketCodec.cs`

### Step 1: Replace stub with full implementation

Replace the entire contents of `src/Core.Net/PacketCodec.cs`:

```csharp
using Core.Sim;
using System.Buffers.Binary;

namespace Core.Net;

/// <summary>
/// Encodes and decodes RBN1 v1 UDP input packets.
///
/// Wire layout (little-endian) — see <c>docs/NETCODE.md</c> for the full table:
///   [0..3]   Magic        "RBN1"
///   [4]      Version      = 1
///   [5]      Flags        bit0 = HasChecksum; bits 1–7 must be 0
///   [6..9]   StartFrame   uint32 LE
///   [10]     Count        uint8  (1..32)
///   [11..14] AckFrame     uint32 LE
///   ── present when Flags &amp; 1 ──────────────────────────────────────────
///   [15..18] ChecksumFrame  uint32 LE
///   [19..22] Checksum       uint32 LE  (SimHash FNV-1a 32-bit, caller-computed)
///   ── payload: Count × 2-byte ushort Buttons (LE) ─────────────────────────
/// </summary>
public static class PacketCodec
{
    // ── Public constants ──────────────────────────────────────────────────────

    /// <summary>Maximum on-wire size: 23-byte header + 32 × 2-byte frames.</summary>
    public const int MaxPacketSize = 87;

    /// <summary>Maximum number of input frames per packet.</summary>
    public const int MaxInputsPerPacket = 32;

    // ── Private constants ─────────────────────────────────────────────────────

    private static ReadOnlySpan<byte> Magic        => "RBN1"u8;
    private const  byte               Version      = 1;
    private const  int                MinHeader    = 15;  // without checksum block
    private const  int                CsumBlock    = 8;   // ChecksumFrame + Checksum
    private const  int                FrameBytes   = 2;   // sizeof(ushort)
    private const  byte               FlagCsum     = 0x01;
    private const  byte               FlagReserved = 0xFE; // bits 1–7 must be zero

    // ── Encode ────────────────────────────────────────────────────────────────

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
            BinaryPrimitives.WriteUInt32LittleEndian(dest[pos..],      pkt.ChecksumFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[(pos + 4)..], pkt.Checksum);
            pos += CsumBlock;
        }

        // Payload: N × ushort Buttons
        var inputs = pkt.InputsSpan;
        for (int i = 0; i < pkt.Count; i++, pos += FrameBytes)
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(pos, FrameBytes), inputs[i].Buttons);

        return packetSize;
    }

    // ── TryDecode ─────────────────────────────────────────────────────────────

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

    // ── TryDecodeInto ─────────────────────────────────────────────────────────

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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates all header fields in spec order and returns parsed values.
    /// Validation order: size → magic → version → flags → count → exact length.
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
        bool hasCsum    = (flags & FlagCsum) != 0;
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
    /// <paramref name="src"/> at <paramref name="pos"/> into
    /// <paramref name="dst"/> as <see cref="FrameInput"/> values.
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
```

### Step 2: Run all tests

```
dotnet test RollbackPlayground.sln
```

Expected: **75 passed** (68 existing + 7 new), 0 failed, 0 warnings.

### Step 3: Zero-warning release build

```
dotnet build RollbackPlayground.sln -c Release
```

Expected: 0 Warning(s), 0 Error(s).

### Step 4: Commit

```
git add src/Core.Net/PacketCodec.cs
git commit -m "feat(net): implement PacketCodec Encode / TryDecode / TryDecodeInto (RBN1 v1)"
```

---

## Task 5: docs/NETCODE.md

**Files:**
- Create: `docs/NETCODE.md`

### Step 1: Create `docs/NETCODE.md`

```markdown
# Netcode — RBN1 Packet Protocol

## Packet Format (v1, little-endian)

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 4 | Magic | `"RBN1"` = `52 42 4E 31` |
| 4 | 1 | Version | Must be `1`; decoder rejects any other value |
| 5 | 1 | Flags | bit0 = HasChecksum; bits 1–7 must be 0 |
| 6 | 4 | StartFrame | `uint32 LE` — frame index of the first input in this packet |
| 10 | 1 | Count | `uint8` — number of frames, range [1, 32] |
| 11 | 4 | AckFrame | `uint32 LE` — latest frame confirmed received from peer |
| *15* | *4* | *ChecksumFrame* | `uint32 LE` — present only when `Flags & 1` |
| *19* | *4* | *Checksum* | `uint32 LE` — present only when `Flags & 1` |
| 15 or 23 | 2 × N | Buttons[0..N-1] | `ushort LE` per frame — `FrameInput.Buttons` bitmask |

**Header size:** 15 bytes (no checksum block) / 23 bytes (with checksum block).
**Max packet size:** 23 + 32 × 2 = **87 bytes** — safe to `stackalloc` on every call.

---

## Why Send the Last N Frames (Redundant Delivery)

A UDP packet may be lost in transit. If each packet carries only the current frame,
a loss stalls the remote peer until a re-request round-trip completes — adding at
least one full RTT of extra delay.

By setting `StartFrame = currentFrame − (N − 1)` and including the last N frames in
every packet, the sender lets the receiver fill gaps silently:

```
Sender frame 10 → packet: StartFrame=8, Count=3, frames=[8,9,10]
Sender frame 11 → packet: StartFrame=9, Count=3, frames=[9,10,11]
```

If the frame-10 packet is lost, the frame-11 packet delivers frames 9, 10, and 11
together. No re-request needed.

`N = delayFrames + 1` is the natural choice: the receiver's prediction horizon
(how far back it may need to roll back) equals the input-delay buffer depth, so
redundant frames always cover the full gap.

---

## Checksum Field — Desync Detection (not Packet Integrity)

The optional `Checksum` field carries the result of `SimHash.Checksum(in SimState)`:
a field-wise FNV-1a 32-bit hash of the full simulation state at `ChecksumFrame`.

**The codec treats the value as an opaque `uint`.**
The caller computes it via `SimHash.Checksum(in SimState)` and passes the result in.

### Why FNV-1a, not CRC32?

| Property | FNV-1a 32-bit | CRC32 |
|---|---|---|
| Purpose | General fingerprint | Payload integrity |
| Dependencies | None (trivial loop) | Lookup table |
| Input | Logical fields, fixed order | Canonical byte serialization |
| Already in project | ✅ `SimHash.Checksum` | ✅ `ReplaySerializer` (file integrity) |

CRC32 requires serializing the complete `SimState` to bytes in a canonical order before
hashing — more code, more error surface. FNV-1a over logical fields is already
established in `Core.Sim.SimHash` and is the project's determinism contract.

### How desync detection works

1. Peer A attaches `SimHash.Checksum(state[F])` and frame index `F` to an outgoing
   packet (sets `HasChecksum = true`, `ChecksumFrame = F`, `Checksum = hash`).
2. Peer B, on receiving the packet, compares `SimHash.Checksum(ownState[F])`.
3. If they differ → desync detected at frame F → log / disconnect / re-sync.

---

## Flags Byte — Extension Plan

| Bit | Name | Status |
|-----|------|--------|
| 0 | `HasChecksum` | ✅ v1 |
| 1 | `HasTimeSync` | 📋 planned v0.4 |
| 2 | `HasLossStats` | 📋 planned v0.4 |
| 3–7 | reserved | must be 0 in v1 |

Decoders **must** reject packets with unknown flag bits set
(current rule: `(flags & 0xFE) != 0 → false`).

When new flag bits are defined, either bump the protocol version or negotiate
capability before the first send so that old decoders never receive unknown flags.
```

### Step 2: Commit

```
git add docs/NETCODE.md
git commit -m "docs: add NETCODE.md — RBN1 packet layout, redundant-frames rationale, SimHash explanation"
```

---

## Task 6: Final verification + tag

### Step 1: Full test suite

```
dotnet test RollbackPlayground.sln
```

Expected: **75 passed** (68 + 7), 0 failed.

### Step 2: Release build — 0 warnings

```
dotnet build RollbackPlayground.sln -c Release
```

Expected: 0 Warning(s), 0 Error(s).

### Step 3: Godot build unaffected

```
dotnet build game/game.csproj
```

Expected: 0 errors (`game.csproj` does not reference `Core.Net`).

### Step 4: Git status

```
git status
```

Expected: nothing to commit, working tree clean.

### Step 5: Tag

```
git tag -a v0.3-1 -m "v0.3-1: Core.Net RBN1 v1 packet codec + 7 tests"
git show v0.3-1 --stat
git log --oneline -8
```
