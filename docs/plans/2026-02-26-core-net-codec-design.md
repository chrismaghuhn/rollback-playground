# Design: Core.Net Packet Codec (v0.3-1)

**Date:** 2026-02-26
**Status:** Approved — ready for implementation

---

## Context

v0.3-1 adds a UDP-ready packet codec as the first step toward LAN netcode.
No socket layer yet — pure encode/decode with strict validation and 5 pinned tests.

Dependencies:
- `Core.Sim` for `FrameInput` (single `ushort Buttons` field)
- `SimHash.Checksum(in SimState)` result passed as caller-computed `uint` — Core.Net
  itself never imports `SimState` or calls SimHash

---

## Wire Layout (little-endian, v1)

```
Offset  Size  Field
──────  ────  ────────────────────────────────────────────────────────
 0       4    Magic = 0x52 0x42 0x4E 0x31  ("RBN1")
 4       1    Version = 1
 5       1    Flags   bit0 = HasChecksum; bits 1–7 MUST be 0
 6       4    StartFrame    uint32 LE   (frame index of first input)
10       1    Count         uint8  LE   range [1, 32]
11       4    AckFrame      uint32 LE   (latest frame confirmed from peer)
── present only when (Flags & 1) == 1 ──────────────────────────────
15       4    ChecksumFrame uint32 LE   (which frame's state was hashed)
19       4    Checksum      uint32 LE   (SimHash.Checksum result, FNV-1a 32-bit)
── payload: always Count × 2 bytes ─────────────────────────────────
15/23    2×N  N frames, each = FrameInput.Buttons  ushort LE
```

**Header sizes:** 15 B (no checksum) / 23 B (with checksum)
**Max packet:** 23 + 32 × 2 = **87 bytes** → `stackalloc byte[87]` always safe

---

## Project Structure

```
src/Core.Net/
  Core.Net.csproj            TargetFrameworks: net8.0;net10.0
                             <ProjectReference> Core.Sim
  InputPacketHeader.cs       readonly struct — non-inputs metadata
  InputPacket.cs             readonly struct — wraps FrameInput[] + Count
  PacketCodec.cs             static class — Encode / TryDecode / TryDecodeInto

tests/Core.Net.Tests/
  Core.Net.Tests.csproj      TargetFramework: net10.0
                             <ProjectReference> Core.Net, Core.Sim
  PacketCodecTests.cs        5 required + 2 optional test methods

docs/NETCODE.md              Wire layout table + rationale
```

Both projects added to `RollbackPlayground.sln`.

---

## Types

### InputPacketHeader

```csharp
readonly struct InputPacketHeader
{
    public uint StartFrame    { get; init; }
    public uint AckFrame      { get; init; }
    public bool HasChecksum   { get; init; }
    public uint ChecksumFrame { get; init; }  // meaningful only when HasChecksum
    public uint Checksum      { get; init; }  // caller computes via SimHash.Checksum
}
```

### InputPacket

```csharp
readonly struct InputPacket
{
    private readonly FrameInput[] _inputs;  // internal; never exposed directly

    public InputPacketHeader Header { get; }
    public byte              Count  { get; }   // [1..32]; independent of _inputs.Length

    public ReadOnlySpan<FrameInput> InputsSpan => _inputs.AsSpan(0, Count);

    // Convenience forwarding — callers write pkt.StartFrame, not pkt.Header.StartFrame
    public uint StartFrame    => Header.StartFrame;
    public uint AckFrame      => Header.AckFrame;
    public bool HasChecksum   => Header.HasChecksum;
    public uint ChecksumFrame => Header.ChecksumFrame;
    public uint Checksum      => Header.Checksum;

    // Count is explicit so TryDecode can construct without forcing inputs.Length == Count
    public InputPacket(InputPacketHeader header, FrameInput[] inputs, byte count)
    {
        Header  = header;
        _inputs = inputs;
        Count   = count;
    }
}
```

### PacketCodec

```csharp
static class PacketCodec
{
    public const int MaxPacketSize      = 87;
    public const int MaxInputsPerPacket = 32;

    // Encode pkt into dest. Returns bytes written.
    // Throws ArgumentException when: Count ∉ [1,32], reserved Flags bits set,
    // or dest.Length < required size.
    public static int Encode(in InputPacket pkt, Span<byte> dest);

    // Decode src into a new InputPacket (allocates one FrameInput[count]).
    // Returns false (packet = default) on any validation failure.
    public static bool TryDecode(ReadOnlySpan<byte> src, out InputPacket packet);

    // Zero-alloc variant: fills caller-supplied dst, returns header metadata.
    // Returns false on same conditions as TryDecode.
    public static bool TryDecodeInto(
        ReadOnlySpan<byte>    src,
        Span<FrameInput>      dst,
        out InputPacketHeader header,
        out int               count);
}
```

---

## Validation (both TryDecode variants — in order)

1. `src.Length < 15` → false (buffer too short for minimum header)
2. `src[0..4] != "RBN1"` → false (bad magic)
3. `src[4] != 1` → false (unsupported version)
4. `(src[5] & 0xFE) != 0` → false (unknown/reserved flag bits set)
5. `src[10] < 1 || src[10] > 32` → false (count out of bounds)
6. `expectedLen = (flags & 1 ? 23 : 15) + count * 2;  src.Length != expectedLen` → false (wrong length)

---

## Test Plan

| # | Method | Coverage |
|---|--------|----------|
| 1 | `EncodeDecode_RoundTrip_PreservesFields` | Full round-trip, both HasChecksum=true and false |
| 2 | `Encode_ByteLayout_IsPinned_ForKnownPacket` | StartFrame=1, Count=1, AckFrame=2, Buttons=0x0003, Flags=0 → exact 17-byte sequence |
| 3 | `Decode_RejectsBadMagic` | Flip byte 0 → false |
| 4 | `Decode_RejectsBadVersion` | Set byte 4 = 0xFF → false |
| 5 | `Decode_RejectsWrongLength` | Truncate by 1 → false |
| 6 | `Decode_RejectsUnknownFlags` *(optional)* | Set bit1 → false |
| 7 | `Decode_RejectsCountZero` *(optional)* | Count=0 → false |

**Pinned bytes for test 2:**
```
52 42 4E 31   "RBN1"
01            Version = 1
00            Flags = 0
01 00 00 00   StartFrame = 1 (LE)
01            Count = 1
02 00 00 00   AckFrame = 2 (LE)
03 00         Buttons = 0x0003 (Left | Right)
```
Total: 17 bytes

---

## docs/NETCODE.md outline

- Packet layout table (mirroring wire layout above)
- Rationale: redundant last-N-frames (sender includes frames [StartFrame .. StartFrame+Count-1];
  receiver can fill gaps without a dedicated re-request round-trip)
- SimHash / Desync detection: why FNV-1a, why not CRC32
- Flags extension plan: bit1 = HasTimeSync, bit2 = HasLossStats

---

## Key Implementation Notes

- `"RBN1"u8` UTF-8 literal for magic comparison (no allocation)
- `BinaryPrimitives.ReadUInt32LittleEndian / WriteUInt32LittleEndian` throughout
- No `unsafe`, no LINQ in hot paths
- `Encode` validates count bounds and reserved-flags bits before writing
- `InputPacket` ctor is intentionally permissive (validation lives in Encode/TryDecode)
