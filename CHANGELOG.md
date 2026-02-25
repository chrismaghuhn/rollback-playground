# Changelog

All notable changes to this project are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

---

## [v0.1-mvp] — 2026-02-25

First complete MVP milestone. All Core.Sim, Core.Rollback, and Core.Replay
libraries are implemented and covered by automated tests (68 tests, 0 warnings).

### Added

#### Core.Sim
- `Prng32` — deterministic XorShift32 PRNG with explicit state; seed-0 guard;
  value-type so rollback snapshots the RNG for free.
- `FrameInput` — bitmask struct (`ushort`) for player buttons: Left, Right,
  Jump, Attack.
- `PlayerState` — flat struct holding position (`X`, `Y`), velocity (`Vx`,
  `Vy`), `Facing`, `ActionState`, `Hp`, cooldown counters, and `AttackHasHit`.
- `SimState` — top-level value-type snapshot: `Frame`, `P1`, `P2`, `Rng`.
  `CreateInitial(seed)` produces a symmetric starting state.
- `SimConstants` — all magic numbers in one place (fixed-point, FixedScale=1000,
  arena bounds, physics, combat). No floats, no doubles.
- `SimStep.Step` — pure deterministic tick function. Phase order: frame++,
  TickBegin (decrement counters), AttackStart, Movement/Jump, Gravity+Integrate,
  AttackCountdown, simultaneous hit resolution.
- `SimHash` — FNV-1a 32-bit field-wise checksum for determinism verification.
  Golden pinned value: `0x41B73DB7` (seed=1, 1 000 frames, pinned 2026-02-25).

#### Core.Rollback
- `InputBuffer` — fixed-capacity circular ring buffer of `FrameInput` values.
  `Set`/`TryGet`/`GetOrPredict` (repeat-last-known prediction); sentinel
  `uint.MaxValue`; O(capacity) bounded back-search; no per-call allocation.
- `StateBuffer` — circular ring buffer of `SimState` snapshots. Value-copy
  semantics; `LatestFrame` nullable tracker; `uint.MaxValue` sentinel.
- `RollbackEngine` — drives a two-player rollback session. `Tick` records local
  input, predicts remote input (stored for mismatch detection), saves a pre-step
  snapshot, then steps the sim. `SetRemoteInput` triggers `RollbackTo` on
  mismatch. Tracks `RollbackCount`, `RollbackFramesTotal`, `MaxRollbackDepth`.

#### Core.Replay
- `ReplayFrame` — readonly struct holding `P1` and `P2` `FrameInput` per tick.
- `Replay` — immutable session record (seed, startFrame, deep-copied frame
  array). Stores only inputs + metadata — no GameState snapshots.
- `ReplayRecorder` — mutable builder; `Append`/`Build`; amortised O(1) per call.
- `ReplayPlayer` — static pure-function playback. `Play(replay)` reconstructs
  the final `SimState` deterministically. `PlayAndChecksum` convenience overload.
- `ReplaySerializer` — RPLK v1 binary format (little-endian, 32-byte header,
  4 bytes/frame payload, CRC32/IEEE over payload). `Write`/`Read` (stream) +
  `WriteToFile`/`ReadFromFile`. Internal CRC32 (polynomial 0xEDB88320) — no
  external NuGet dependency.

#### Tests & Documentation
- 68 automated tests across three test projects (36 Sim, 20 Rollback, 8 Replay).
- `DeterminismAuditTests` — reflection audit (method signatures, property types,
  engine assembly references) + source scan for forbidden tokens (wall-clock
  APIs, engine names, float/double/decimal keywords).
- `docs/ASSUMPTIONS.md` — single source of truth for all design decisions,
  fixed-point conventions, frame semantics, collision rules, constants, rollback
  and replay contracts.
- `docs/RELEASE.md` — preflight checklist, tagging procedure, determinism
  verification guide, and instructions for intentional sim-behaviour changes.

### Architecture constraints (enforced by tests)
- Zero floats, doubles, or decimals anywhere in `Core.Sim`.
- No wall-clock or timer APIs in `Core.Sim`.
- No game-engine assembly references in `Core.Sim`.
- `Core.Replay` has no dependency on `Core.Rollback` (one-way layering).

---

[Unreleased]: https://github.com/example/rollback-playground/compare/v0.1-mvp...HEAD
[v0.1-mvp]:   https://github.com/example/rollback-playground/releases/tag/v0.1-mvp
