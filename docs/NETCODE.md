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
