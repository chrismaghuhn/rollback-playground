# ASSUMPTIONS.md — Rollback Netcode Playground

> **Single source of truth** for all platform constraints, determinism rules,
> fixed-point conventions, simulation semantics, and magic numbers.
>
> Every section here has a corresponding test or constant in the codebase.
> When in doubt, the test wins; when the test and this file disagree,
> update this file.

---

## Table of Contents

1. [Platform / Build](#1-platform--build)
2. [Determinism Contract](#2-determinism-contract)
3. [Fixed-Point Convention](#3-fixed-point-convention)
4. [Frame Semantics / Update Order](#4-frame-semantics--update-order)
5. [Collision & Hit Rules](#5-collision--hit-rules)
6. [Constants Table](#6-constants-table)
7. [Rollback Assumptions](#7-rollback-assumptions)
8. [Replay Assumptions](#8-replay-assumptions)
9. [What Intentionally Breaks Determinism Tests](#9-what-intentionally-breaks-determinism-tests)

---

## 1  Platform / Build

### SDK & Target Framework

| Setting | Value | Reason |
|---|---|---|
| `global.json` SDK | `10.0.103` (`rollForward: latestPatch`) | Project started on .NET 10; `latestPatch` ensures minor security patches without silent minor-version jumps. |
| Target Framework Moniker | `net10.0` | All four projects (`Core.Sim`, `Core.Rollback`, `Core.Replay`, test projects) use the same TFM. |
| Plan originally said | `net8.0` | Bumped to `net10.0` because the local dev machine already had 10.0.103 installed and no .NET 8 SDK was present. There is no semantic difference for this project: no API in use was added between .NET 8 and .NET 10. |

### CI Environment

- OS: **`ubuntu-latest`** (GitHub Actions)
- Action: `actions/setup-dotnet` with channel `10.0.x`
- Command: `dotnet test RollbackPlayground.sln`
- Expected: **all tests pass, 0 warnings**

### Build Configuration

- `<Nullable>enable</Nullable>` in every project — null-safety is enforced at compile time.
- `<ImplicitUsings>enable</ImplicitUsings>` — `System`, `System.Collections.Generic`, `System.IO`, etc. are auto-imported.
- No `TreatWarningsAsErrors` entry in `.csproj` files, but the acceptance criterion for every task requires **0 warnings** in `dotnet test` output.

---

## 2  Determinism Contract

These rules are **non-negotiable**. Violating any one of them will cause desync between two peers running the same replay or the same rollback session.

### 2.1  No Floating-Point in Core.Sim

`Core.Sim` must never use `float`, `double`, or `decimal`.

- IEEE 754 float/double results can differ between:
  - x86 FPU (80-bit extended precision) vs. SSE2 (strict 64-bit)
  - JIT compilation with different optimization flags
  - Mono vs. CoreCLR
- `decimal` is deterministic but slow and architecturally wrong for this domain.
- **Test:** `SimTypesTests.SimConstants_NoFloatOrDoubleOrDecimalFieldsInAssembly` uses reflection to verify no field in `Core.Sim.dll` carries a float/double/decimal type.

### 2.2  No Wall-Clock or Time APIs

`Core.Sim` must never call:
- `DateTime`, `DateTimeOffset`, `TimeOnly`
- `Stopwatch`, `Environment.TickCount`, `Environment.TickCount64`
- `Thread.Sleep`, `Task.Delay`
- Any platform timer or monotonic clock

The simulation is driven entirely by the caller supplying one `FrameInput` per tick. The notion of "time" is purely `SimState.Frame` — a `uint` incremented by `SimStep`.

### 2.3  No Engine APIs in Core.Sim

`Core.Sim` has **no dependency** on Godot, Unity, MonoGame, or any game-engine assembly. It is a plain .NET class library. The `game/` Godot project depends on `Core.Sim`; the reverse is forbidden.

### 2.4  PRNG: Prng32 XorShift32, Seed ≠ 0

- Algorithm: XorShift32 with the recurrence
  `x ^= x << 13;  x ^= x >> 17;  x ^= x << 5;`
- Period: **2³² − 1** (all non-zero 32-bit values).
- Seed **0** is the unique absorbing state (all three shifts produce 0 forever); `Prng32(0)` throws `ArgumentOutOfRangeException`.
- The `Prng32` struct is a **value type** — copying a `SimState` automatically snapshots the PRNG state. No extra bookkeeping is needed for rollback.

---

## 3  Fixed-Point Convention

```
FixedScale = 1 000

1 fixed-point unit = 1 / 1 000 world unit  (0.001 wu)
```

All positions (`X`, `Y`), velocities (`Vx`, `Vy`), and geometry constants are stored as `int` values multiplied by `FixedScale`.

| Human value | Fixed-point value | Field |
|---|---|---|
| 4.0 wu | 4 000 | `P1StartX` |
| 0.3 wu/tick | 300 | `MoveSpeedPerTick` |
| −0.04 wu/tick² | −40 | `GravityPerTick` |
| 0.5 wu/tick | 500 | `JumpVelocityPerTick` |

### Coordinate System

```
Y
↑
│          (arena ceiling, MaxY = 12 000)
│
│   [P1]                [P2]
│
└────────────────────────────→ X
0                        MaxX = 20 000
(GroundY = 0)
```

- **Origin**: bottom-left corner of the arena.
- **Ground**: `Y = 0` (`GroundY`). A player is "on the ground" when `Y == GroundY`.
- **X axis**: increases to the right. Left wall = `MinX = 0`; right wall = `MaxX = 20 000`.
- **Y axis**: increases upward. Ceiling = `MaxY = 12 000` (not enforced in MVP).
- **Player position** (`X`, `Y`) refers to the **bottom-left corner** of the player's axis-aligned bounding box.
- **Wall clamping**: player `X` is clamped to `[MinX, MaxX − PlayerWidth]` every tick (so the right edge of the sprite never exits the arena).

---

## 4  Frame Semantics / Update Order

### 4.1  Input–Frame Association

- An input tagged for **frame `f`** drives the transition **`state_f` → `state_{f+1}`**.
- `SimState.Frame` starts at **0** and is incremented as the **first** action of `SimStep.Step`.
- Therefore: after `Step` returns, `state.Frame == f + 1`.
- Snapshot convention in `RollbackEngine`: the pre-step snapshot of `state_f` (with `Frame = f`) is saved to `StateBuffer` **before** calling `SimStep.Step`. Rolling back to frame `f` restores this snapshot and re-simulates from there.

### 4.2  SimStep Update Order (per tick)

Each call to `SimStep.Step(in prev, p1Input, p2Input)` executes the following phases **in order**:

| Phase | Code step | Description |
|---|---|---|
| **A** | `Frame++` | Increment the frame counter. |
| **B** | `TickBegin` | Decrement `AttackCooldownFrames` for P1, then P2 (clamp at 0). Decrement `HitstunFrames` for P1, then P2; transition `Hitstun → Idle` when counter reaches 0. |
| **C** | `HandleAttackStart` | `TryStartAttack` for P1, then P2. Sets `State = Attack`, initialises `AttackActiveFrames`, sets `AttackCooldownFrames`, clears `AttackHasHit`. Blocked when `State == Hitstun` or `AttackCooldownFrames > 0`. |
| **D** | `Movement / Jump` | Apply directional input to `X`; update `Facing`. Initiate jump (set `Vy = JumpVelocityPerTick`) only when grounded. Hitstun blocks all movement. Wall-clamp `X` at end. Applied to P1, then P2. |
| **E** | `Gravity + Integrate` | `Vy += GravityPerTick`; then `Y += Vy`. Clamp `Y` to ground: sets `Y = 0`, `Vy = 0`, transitions `Jump → Idle` on landing. Applied to P1, then P2. |
| **F** | `AttackCountdown` | Decrement `AttackActiveFrames`; transition `Attack → Idle` when window closes. Applied to P1, then P2. |
| **G** | `Hit resolution` | **Simultaneous**: evaluate `CanHit(P1 → P2)` and `CanHit(P2 → P1)` using the current (post-F) state **before** applying either result. Then apply whichever hits landed. This prevents ordering bias. |

### 4.3  Cooldown Semantics

`AttackCooldownFrames` is decremented at **phase B** (TickBegin), and `TryStartAttack` runs at **phase C** (after the decrement). This means:

- An attack started on frame `f` sets `AttackCooldownFrames = 30`.
- At the start of frame `f+1` (phase B), it becomes 29.
- The attacker can start a new attack at the **earliest** on the tick when the counter reaches 0 after decrement.
- Therefore **`AttackCooldownFrames = 30`** means: the attacker must wait **exactly 30 ticks** between attack initiations (exclusive of the starting tick, inclusive of the ending tick).
- **Invariant**: `AttackCooldownFrames >= AttackActiveFrames` must hold to ensure the active hitbox window always closes before the cooldown expires.

---

## 5  Collision & Hit Rules

### 5.1  AABB Hit Detection — Open Intervals

All overlap tests use **strictly less-than** (`<`), not `<=`. This means **touching edges do NOT count as a hit**.

```csharp
bool xOverlap = hitLeft  < hurtRight && hurtLeft  < hitRight;
bool yOverlap = hitBottom < hurtTop  && hurtBottom < hitTop;
```

An attacker whose hitbox right-edge exactly equals the defender's hurtbox left-edge will **miss**.

### 5.2  Hitbox Placement (Attacker)

The attack hitbox is placed adjacent to the attacker's body in the facing direction:

| Facing | hitLeft | hitRight |
|---|---|---|
| Right (`Facing = +1`) | `X + PlayerWidth` | `X + PlayerWidth + AttackHitboxWidth` |
| Left  (`Facing = −1`) | `X − AttackHitboxWidth` | `X` |

Vertical extent (both facings):

```
hitBottom = Y
hitTop    = Y + AttackHitboxHeight
```

### 5.3  Hurtbox (Defender)

```
hurtLeft   = X
hurtRight  = X + PlayerWidth
hurtBottom = Y
hurtTop    = Y + PlayerHeight
```

The hurtbox is always the full player bounding box; it does not depend on facing or action state.

### 5.4  AttackHasHit — One Hit Per Swing

- `AttackHasHit` is a `byte` field on `PlayerState`, initialised to `0` when an attack starts.
- `CanHit` returns `false` if `AttackHasHit != 0` — the attacker can **land at most one hit** per attack window.
- `ApplyHit` sets `attacker.AttackHasHit = 1`.
- A new attack (phase C) resets `AttackHasHit = 0`.
- There is no multi-hit within a single `AttackActiveFrames` window.

### 5.5  Hitstun

- On a confirmed hit, the defender receives `HitstunFrames = 20` and transitions to `ActionState.Hitstun`.
- While in `Hitstun` the defender **cannot move, jump, or attack**.
- Hitstun is decremented at phase B; the `Hitstun → Idle` transition occurs when the counter reaches 0.

---

## 6  Constants Table

All values are fixed-point integers (see §3). Changing any of these **will break the golden checksum** (see §9).

### Timing

| Constant | Value | Human value |
|---|---|---|
| `TicksPerSecond` | `60` | 60 Hz simulation |
| `FixedScale` | `1 000` | 1 wu = 1 000 fp units |

### Arena

| Constant | Value | Human value |
|---|---|---|
| `MinX` | `0` | 0 wu (left wall) |
| `MaxX` | `20 000` | 20 wu (right wall) |
| `GroundY` | `0` | 0 wu (floor) |
| `MaxY` | `12 000` | 12 wu (ceiling, reference only — not enforced in MVP) |

### Player Geometry

| Constant | Value | Human value |
|---|---|---|
| `PlayerWidth` | `600` | 0.6 wu |
| `PlayerHeight` | `900` | 0.9 wu |

### Spawn Positions

| Constant | Value | Human value |
|---|---|---|
| `P1StartX` | `4 000` | 4 wu from left wall |
| `P2StartX` | `16 000` | 16 wu from left wall (4 wu from right wall) |
| `StartY` | `0` | `GroundY` — both players start on the floor |
| P1 initial `Facing` | `+1` | Faces right |
| P2 initial `Facing` | `−1` | Faces left |

### Movement / Physics

| Constant | Value | Human value |
|---|---|---|
| `MoveSpeedPerTick` | `300` | 0.3 wu/tick = 18 wu/s at 60 Hz |
| `GravityPerTick` | `−40` | −0.04 wu/tick² (downward) |
| `JumpVelocityPerTick` | `500` | 0.5 wu/tick initial upward velocity |

### Combat

| Constant | Value | Notes |
|---|---|---|
| `AttackHitboxWidth` | `700` | 0.7 wu (no horizontal offset — placed at body edge, see §5.2) |
| `AttackHitboxHeight` | `700` | 0.7 wu (aligned to player's Y) |
| `AttackActiveFrames` | `5` | Hitbox active for 5 ticks per swing |
| `AttackCooldownFrames` | `30` | 0.5 s at 60 Hz; ≥ `AttackActiveFrames` (invariant) |
| `AttackDamage` | `25` | HP deducted per hit |
| `HitstunFrames` | `20` | Ticks of lockout on the defender |
| `DefaultHp` | `100` | Starting HP for both players |

### Golden Checksum (SimHash FNV-1a)

| Setting | Value |
|---|---|
| Algorithm | FNV-1a 32-bit, field-wise over `Frame, P1.*, P2.*, Rng.State` |
| Seed | `1u` |
| Frames | 1 000 |
| Input script | P1: 0–49 Right, 50 Jump, 51–149 Right, 150–199 Attack every 20 f, 200+ Left; P2: 0–99 Left, 100–119 Jump, 120+ neutral |
| **Pinned checksum** | **`0x41B73DB7`** (1 102 527 927) — pinned 2026-02-25 |

---

## 7  Rollback Assumptions

### 7.1  Input Prediction — Repeat Last Known

When a remote input for frame `f` has not yet arrived, `InputBuffer.GetOrPredict(f)` returns:

1. **Exact hit** — the stored input for exactly frame `f`.
2. **Future frame** (`f > latestKnown`) — repeat the most-recently stored input unchanged.
3. **Gap** (`f ≤ latestKnown`, not in buffer) — backward search `[f−1, f−2, …]` for the nearest stored input within `capacity` frames; return that. Bounded O(capacity) worst case, no heap allocation.
4. **Nothing found** — return `default` (`FrameInput(0)`, all buttons released / neutral).

### 7.2  Sentinel Frames — `uint.MaxValue`

Both `InputBuffer` and `StateBuffer` use parallel `uint[]` tag arrays initialised to `uint.MaxValue`.

- `uint.MaxValue` ≈ 4.29 × 10⁹. At 60 Hz this equals ~828 days of continuous play — effectively unreachable.
- A slot tagged `uint.MaxValue` is treated as "never written", so `TryGet` returns `false` for any real frame number without needing a separate boolean-per-slot.

### 7.3  Bounded Back-Search

The backward search in `GetOrPredict` is bounded to `capacity` frames. Any input older than that has been **silently evicted** by the ring-buffer wraparound and cannot be recovered. This is a hard constraint: the caller must set `historyCapacity` large enough for the expected maximum rollback depth.

In `RollbackEngine`: if `StateBuffer.TryLoad(rollbackFrame)` returns `false`, an `InvalidOperationException` is thrown with a diagnostic message.

### 7.4  Predicted Inputs Are Stored

When `RollbackEngine.Tick` predicts a remote input, it writes the prediction back into `_remoteInputs` via `Set`. This enables mismatch detection: when the real input for that frame arrives later, `SetRemoteInput` calls `TryGet` and finds the prediction — if `Buttons` differ, a rollback is triggered.

Without this write-back, `TryGet` would return `false` for the predicted frame and mismatch detection would be impossible.

---

## 8  Replay Assumptions

### 8.1  Inputs-Only Storage

A `Replay` object stores:
- `Seed` (`uint`) — PRNG seed passed to `SimState.CreateInitial`
- `StartFrame` (`uint`) — first absolute frame covered (MVP: always 0)
- `Frames` — flat array of `ReplayFrame` (one per tick), each holding `P1.Buttons` and `P2.Buttons`

A `Replay` does **not** store any `SimState` snapshots. The simulation is fully deterministic from seed + inputs, so the final state can always be reproduced by `ReplayPlayer.Play`. Storing snapshots would:
1. Bloat storage proportionally to frame count × `sizeof(SimState)`.
2. Create a redundant invariant (stored state must agree with re-simulated state).
3. Require snapshot serialisation (a separate, optional concern for desync diagnostics).

### 8.2  RPLK v1 Binary Format

All integers are **little-endian**. Encoded with `BinaryPrimitives` (no `unsafe`, no `MemoryMarshal`).

```
Offset  Size  Field
──────────────────────────────────────────────────────
Header (32 bytes, fixed)
  0      4    Magic: ASCII "RPLK"  (0x52 0x50 0x4C 0x4B)
  4      1    Version: byte = 1
  5      1    Flags: byte = 0  (reserved)
  6      2    HeaderSize: LE ushort = 32
  8      4    Seed: LE uint
 12      4    StartFrame: LE uint
 16      4    FrameCount: LE uint
 20      4    PayloadCrc32: LE uint  (CRC32 over payload only)
 24      8    Reserved: 8 × 0x00

Payload (FrameCount × 4 bytes)
  per frame:
  +0     2    P1.Buttons: LE ushort
  +2     2    P2.Buttons: LE ushort
```

Total file size: `32 + FrameCount × 4` bytes.

### 8.3  CRC32 Algorithm

- Polynomial: **IEEE 802.3** reflected form — `0xEDB88320`
- Initial value: `0xFFFFFFFF`; final XOR: `0xFFFFFFFF`
- Scope: payload bytes only (not the header)
- Implementation: internal `InternalCrc32` table in `ReplaySerializer.cs`
  (`System.IO.Hashing.Crc32` requires an explicit NuGet package even on `net10.0` and was avoided to keep `Core.Replay` dependency-free)

### 8.4  MVP Constraint — StartFrame = 0

`ReplayPlayer.Play` and `ReplaySerializer.Write` both throw `NotSupportedException` if `StartFrame != 0`. Mid-session replays (e.g. starting from a checkpoint) are planned for a future milestone.

---

## 9  What Intentionally Breaks Determinism Tests

The following changes will cause `GoldenDeterminismTests` to fail immediately:

| Change | Why it breaks |
|---|---|
| Any value in `SimConstants` | Alters positions, velocities, or combat outcomes → different final state |
| Reordering phases in `SimStep.Step` | Changes which player acted "first" in a given tick |
| Changing the AABB overlap test from `<` to `<=` | Some frames near-miss would become hits → different HP |
| Changing XorShift32 recurrence shifts | Different PRNG sequence → different random outcomes |
| Adding a `float`/`double` operation in `Core.Sim` | Potential cross-platform divergence |
| Adding or removing a field from `SimHash.Checksum` (or reordering) | Hash value changes even if simulation state is unchanged |
| Changing `FNV-1a` constants in `SimHash` | Hash algorithm changes |

**This is intentional.** The golden test is a regression lock. It exists precisely to catch accidental behavioural changes. To purposefully change behaviour, update the golden constant (`Pinned`) in `GoldenDeterminismTests` and document the reason in this file and in the commit message.
