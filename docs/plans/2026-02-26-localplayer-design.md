# Design: RollbackEngine LocalPlayer Support (v0.3-2 Task 1)

**Date:** 2026-02-26
**Status:** Approved — ready for implementation

---

## Problem

`RollbackEngine.Tick(FrameInput localInput)` implicitly assumes local = P1. Both
`SimStep.Step(...)` call sites pass `(localInput, remoteInput)` directly, which maps
local → p1, remote → p2. For LAN play where the second machine is the P2 client,
this produces a mirrored (incorrect) simulation.

---

## Approach: Constructor Parameter + MapInputs Helper

**Only meaningful approach.** Alternatives (`TickAsP2()` method, static factories)
add API surface with no benefit.

Key invariant: `_localInputs` / `_remoteInputs` buffers keep their
"own input vs. opponent's input" semantics. Only the mapping to `p1Input` / `p2Input`
at the two `SimStep.Step()` call sites changes.

---

## Files Changed

```
src/Core.Rollback/LocalPlayer.cs           ← NEW
src/Core.Rollback/RollbackEngine.cs        ← MODIFIED
tests/Core.Rollback.Tests/RollbackEngineLocalPlayerTests.cs  ← NEW
```

---

## (1) LocalPlayer Enum

```csharp
// src/Core.Rollback/LocalPlayer.cs
namespace Core.Rollback;

public enum LocalPlayer : byte { P1 = 1, P2 = 2 }
```

`default(LocalPlayer) == 0` is intentionally not a valid value. The constructor
validates and throws, so silent wrong-branch execution is impossible.

---

## (2) RollbackEngine Changes

### Constructor signature (no breaking change — default = P1)

```csharp
public RollbackEngine(SimState initialState, int historyCapacity,
                      LocalPlayer localPlayer = LocalPlayer.P1)
```

### Validation (after existing capacity check)

```csharp
if (localPlayer != LocalPlayer.P1 && localPlayer != LocalPlayer.P2)
    throw new ArgumentOutOfRangeException(
        nameof(localPlayer), localPlayer, "LocalPlayer must be P1 or P2.");
```

### New property + field

```csharp
public LocalPlayer LocalPlayer { get; }    // public read-only

private readonly LocalPlayer _localPlayer; // stored in ctor
```

### MapInputs private helper (eliminates duplication at both call sites)

```csharp
private void MapInputs(
    FrameInput localInput, FrameInput remoteInput,
    out FrameInput p1, out FrameInput p2)
{
    if (_localPlayer == LocalPlayer.P1) { p1 = localInput;  p2 = remoteInput; }
    else                                { p1 = remoteInput; p2 = localInput;  }
}
```

### Tick() — replace the Step() call

```csharp
// Before:
CurrentState = SimStep.Step(in prev, localInput, remoteInput);

// After:
MapInputs(localInput, remoteInput, out var p1, out var p2);
CurrentState = SimStep.Step(in prev, p1, p2);
```

### RollbackTo() resim loop — replace the Step() call

```csharp
// Before:
CurrentState = SimStep.Step(in prevReplay, li, ri);

// After:
MapInputs(li, ri, out var p1r, out var p2r);
CurrentState = SimStep.Step(in prevReplay, p1r, p2r);
```

---

## (3) Tests

New file: `tests/Core.Rollback.Tests/RollbackEngineLocalPlayerTests.cs`

Reuses existing helpers from `RollbackEngineTests`:
- `ScriptP1(uint f)` — deterministic P1 input
- `ScriptP2(uint f)` — deterministic P2 input
- `AssertStatesMatch(SimState expected, SimState actual)` — field-wise assert

### Required tests

**`NoLag_LocalIsP2_EqualsGroundTruth()`**
- seed=1, frames=300
- Ground truth: `SimStep.Step(prev, ScriptP1(f), ScriptP2(f))` for 300 frames
- Engine: `LocalPlayer.P2`, capacity=512
  - per frame: `SetRemoteInput(f, ScriptP1(f))` then `Tick(ScriptP2(f))`
- Assert: `AssertStatesMatch` + `RollbackCount == 0`

**`Ctor_InvalidLocalPlayer_Throws()`**
- `var bad = (LocalPlayer)0;`
- `Assert.Throws<ArgumentOutOfRangeException>(() => new RollbackEngine(..., 8, bad));`

### Optional test

**`Lag_LocalIsP2_ConvergesToGroundTruth()`**
- delay=6, 300 frames — mirrors existing `Lag_DelayedRemoteInputs_ConvergesToGroundTruth`
  but with `LocalPlayer.P2` and swapped script roles

---

## Acceptance Criteria

- `dotnet test RollbackPlayground.sln` green, 0 warnings
- All existing `RollbackEngineTests` remain unchanged and pass
- `RollbackEngine(state, cap)` (existing 2-arg form) still works (default = P1)
