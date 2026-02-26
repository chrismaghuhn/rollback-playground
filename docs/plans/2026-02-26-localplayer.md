# RollbackEngine LocalPlayer Support — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `LocalPlayer` enum and update `RollbackEngine` so the second machine in a LAN session can run as P2 without producing a mirrored (incorrect) simulation.

**Architecture:** A `LocalPlayer` optional constructor parameter (default = P1) is stored in a private field. A private `MapInputs` helper swaps `localInput`/`remoteInput` → `p1Input`/`p2Input` at both `SimStep.Step` call sites (Tick line 130, RollbackTo line 216). Buffer semantics (`_localInputs` = own, `_remoteInputs` = opponent) are unchanged.

**Tech Stack:** C# / .NET 10, xUnit, `RollbackPlayground.sln`

---

### Task 1: Create LocalPlayer enum

**Files:**
- Create: `src/Core.Rollback/LocalPlayer.cs`

**Step 1: Create the file**

```csharp
// src/Core.Rollback/LocalPlayer.cs
namespace Core.Rollback;

/// <summary>
/// Identifies which player this <see cref="RollbackEngine"/> instance controls locally.
/// <c>default(LocalPlayer) == 0</c> is intentionally not a valid value; the constructor
/// validates and throws, preventing silent wrong-branch execution.
/// </summary>
public enum LocalPlayer : byte
{
    /// <summary>The local machine controls Player 1 (default for existing callers).</summary>
    P1 = 1,

    /// <summary>The local machine controls Player 2 (remote peer is P1).</summary>
    P2 = 2,
}
```

**Step 2: Verify existing tests still pass**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=minimal"
```
Expected: `75 passed, 0 failed, 0 skipped`

**Step 3: Commit**

```bash
git add src/Core.Rollback/LocalPlayer.cs
git commit -m "feat(rollback): add LocalPlayer enum (P1=1, P2=2, no default)"
```

---

### Task 2: Write failing tests + stub constructor changes

**Files:**
- Create: `tests/Core.Rollback.Tests/RollbackEngineLocalPlayerTests.cs`
- Modify: `src/Core.Rollback/RollbackEngine.cs` (stub only — behaviour unchanged)

**Step 1: Create the test file**

```csharp
// tests/Core.Rollback.Tests/RollbackEngineLocalPlayerTests.cs
using Core.Sim;

namespace Core.Rollback.Tests;

public class RollbackEngineLocalPlayerTests
{
    // ── Input constants (copied verbatim from RollbackEngineTests) ───────────────

    private static readonly FrameInput Neutral = new(0);
    private static readonly FrameInput Left    = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput Right   = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput Jump    = FrameInput.FromButtons(false, false, true,  false);
    private static readonly FrameInput Attack  = FrameInput.FromButtons(false, false, false, true);

    // ── Input scripts (copied verbatim from RollbackEngineTests) ─────────────────

    /// <summary>P1: 0–49 Right, 50 Jump, 51–149 Right, 150–199 Attack every 20 frames, 200+ Left.</summary>
    private static FrameInput ScriptP1(uint f)
    {
        if (f < 50)   return Right;
        if (f == 50)  return Jump;
        if (f < 150)  return Right;
        if (f < 200)  return f % 20 == 0 ? Attack : Neutral;
        return Left;
    }

    /// <summary>P2: 0–99 Left, 100–119 Jump, 120+ Neutral.</summary>
    private static FrameInput ScriptP2(uint f)
    {
        if (f < 100) return Left;
        if (f < 120) return Jump;
        return Neutral;
    }

    /// <summary>Reference simulation — no rollback, no prediction.</summary>
    private static SimState GroundTruthRun(uint seed, int frames)
    {
        var state = SimState.CreateInitial(seed);
        for (int i = 0; i < frames; i++)
            state = SimStep.Step(in state, ScriptP1((uint)i), ScriptP2((uint)i));
        return state;
    }

    /// <summary>Compares key fields of two SimStates for equality.</summary>
    private static void AssertStatesMatch(SimState expected, SimState actual)
    {
        Assert.Equal(expected.Frame,     actual.Frame);
        Assert.Equal(expected.P1.X,      actual.P1.X);
        Assert.Equal(expected.P1.Y,      actual.P1.Y);
        Assert.Equal(expected.P1.Hp,     actual.P1.Hp);
        Assert.Equal(expected.P1.State,  actual.P1.State);
        Assert.Equal(expected.P1.Facing, actual.P1.Facing);
        Assert.Equal(expected.P2.X,      actual.P2.X);
        Assert.Equal(expected.P2.Y,      actual.P2.Y);
        Assert.Equal(expected.P2.Hp,     actual.P2.Hp);
        Assert.Equal(expected.P2.State,  actual.P2.State);
        Assert.Equal(expected.P2.Facing, actual.P2.Facing);
        Assert.Equal(expected.Rng.State, actual.Rng.State);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No-lag P2 session must converge to the same ground truth as P1 and produce
    /// zero rollbacks when remote (P1) input arrives before each tick.
    /// </summary>
    [Fact]
    public void NoLag_LocalIsP2_EqualsGroundTruth()
    {
        const uint seed   = 1u;
        const int  frames = 300;

        SimState gt = GroundTruthRun(seed, frames);
        var engine  = new RollbackEngine(SimState.CreateInitial(seed), 512, LocalPlayer.P2);

        for (uint f = 0; f < (uint)frames; f++)
        {
            engine.SetRemoteInput(f, ScriptP1(f)); // remote = P1
            engine.Tick(ScriptP2(f));              // local  = P2
        }

        AssertStatesMatch(gt, engine.CurrentState);
        Assert.Equal(0, engine.RollbackCount);
    }

    /// <summary>
    /// Constructor must throw ArgumentOutOfRangeException for any LocalPlayer value
    /// that is not P1 or P2 (in particular, the C# default 0).
    /// </summary>
    [Fact]
    public void Ctor_InvalidLocalPlayer_Throws()
    {
        var bad = (LocalPlayer)0;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollbackEngine(SimState.CreateInitial(1u), 8, bad));
    }

    /// <summary>
    /// P2 engine with 6-frame lag on remote (P1) inputs must still converge to
    /// ground truth via rollbacks, mirroring Lag_DelayedRemoteInputs_ConvergesToGroundTruth.
    /// </summary>
    [Fact]
    public void Lag_LocalIsP2_ConvergesToGroundTruth()
    {
        const int  delay  = 6;
        const int  frames = 300;
        const uint seed   = 1u;

        SimState gt = GroundTruthRun(seed, frames);
        var engine  = new RollbackEngine(SimState.CreateInitial(seed), 512, LocalPlayer.P2);

        // Remote (P1) input arrives 6 ticks late.
        for (uint t = 0; t < (uint)frames; t++)
        {
            if (t >= (uint)delay)
            {
                uint deliveredFrame = t - (uint)delay;
                engine.SetRemoteInput(deliveredFrame, ScriptP1(deliveredFrame));
            }
            engine.Tick(ScriptP2(t)); // local = P2
        }

        // Drain the last <delay> remote inputs.
        for (uint f = (uint)(frames - delay); f < (uint)frames; f++)
            engine.SetRemoteInput(f, ScriptP1(f));

        AssertStatesMatch(gt, engine.CurrentState);
        Assert.True(engine.RollbackCount > 0,
            $"Expected at least one rollback but RollbackCount={engine.RollbackCount}");
    }
}
```

**Step 2: Attempt a build — expect compile error**

```bash
dotnet build RollbackPlayground.sln
```
Expected: error CS1501 — `RollbackEngine` has no 3-argument constructor accepting `LocalPlayer`.

**Step 3: Add stub changes to `src/Core.Rollback/RollbackEngine.cs`**

Make exactly three edits — do **not** add MapInputs yet, do **not** add validation yet:

a) After the `_stateBuffer` field (around line 48), add the new field:
```csharp
private readonly LocalPlayer _localPlayer;
```

b) After the `MaxRollbackDepth` property (around line 65), add the new property:
```csharp
/// <summary>Which player this engine instance controls locally.</summary>
public LocalPlayer LocalPlayer { get; }
```

c) Replace the constructor signature and add the two new assignment lines:
```csharp
// BEFORE:
public RollbackEngine(SimState initialState, int historyCapacity)

// AFTER:
public RollbackEngine(SimState initialState, int historyCapacity,
                      LocalPlayer localPlayer = LocalPlayer.P1)
```
Inside the constructor body, after the capacity check and before `CurrentState = initialState;`, add:
```csharp
_localPlayer = localPlayer;
LocalPlayer  = localPlayer;
```
(Leave the capacity check unchanged. Leave Tick and RollbackTo unchanged.)

**Step 4: Build — expect success**

```bash
dotnet build RollbackPlayground.sln
```
Expected: Build succeeded, 0 errors, 0 warnings

**Step 5: Run tests — expect 3 failures (red)**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=minimal"
```
Expected: **75 passed, 3 failed**
- `Ctor_InvalidLocalPlayer_Throws` — FAIL (no validation → no exception thrown)
- `NoLag_LocalIsP2_EqualsGroundTruth` — FAIL (no MapInputs → P2 engine runs as P1)
- `Lag_LocalIsP2_ConvergesToGroundTruth` — FAIL (same reason)

The 75 existing tests must still pass.

**Step 6: Commit failing tests + stub**

```bash
git add tests/Core.Rollback.Tests/RollbackEngineLocalPlayerTests.cs \
        src/Core.Rollback/RollbackEngine.cs
git commit -m "test(rollback): add LocalPlayer tests (red — MapInputs not yet implemented)"
```

---

### Task 3: Implement MapInputs — make tests green

**Files:**
- Modify: `src/Core.Rollback/RollbackEngine.cs` (4 surgical edits)

**Step 1: Add constructor validation**

Inside the constructor, immediately after the capacity check block (after the closing `}` of `if (historyCapacity < 2)`), add:

```csharp
if (localPlayer != LocalPlayer.P1 && localPlayer != LocalPlayer.P2)
    throw new ArgumentOutOfRangeException(
        nameof(localPlayer), localPlayer, "LocalPlayer must be P1 or P2.");
```

**Step 2: Add MapInputs private helper**

At the end of the class, inside the `// ── Private helpers ─` section, after the closing `}` of `RollbackTo`, add:

```csharp
/// <summary>
/// Maps local/remote inputs to the p1/p2 order expected by <see cref="SimStep.Step"/>.
/// When this instance is P1: p1 = localInput, p2 = remoteInput.
/// When this instance is P2: p1 = remoteInput, p2 = localInput.
/// </summary>
private void MapInputs(
    FrameInput localInput,  FrameInput remoteInput,
    out FrameInput p1,      out FrameInput p2)
{
    if (_localPlayer == LocalPlayer.P1) { p1 = localInput;  p2 = remoteInput; }
    else                                { p1 = remoteInput; p2 = localInput;  }
}
```

**Step 3: Fix Tick() call site (line 130)**

```csharp
// BEFORE (line 130):
CurrentState = SimStep.Step(in prev, localInput, remoteInput);

// AFTER:
MapInputs(localInput, remoteInput, out var p1, out var p2);
CurrentState = SimStep.Step(in prev, p1, p2);
```

**Step 4: Fix RollbackTo() call site (line 216)**

```csharp
// BEFORE (line 216):
CurrentState = SimStep.Step(in prevReplay, li, ri);

// AFTER:
MapInputs(li, ri, out var p1r, out var p2r);
CurrentState = SimStep.Step(in prevReplay, p1r, p2r);
```

**Step 5: Run tests — expect all green**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=minimal"
```
Expected: **78 passed, 0 failed, 0 skipped**

**Step 6: Commit**

```bash
git add src/Core.Rollback/RollbackEngine.cs
git commit -m "feat(rollback): implement LocalPlayer support via MapInputs helper"
```

---

### Task 4: Verify, push, tag

**Step 1: Full solution build with warnings-as-errors**

```bash
dotnet build RollbackPlayground.sln -warnaserror
```
Expected: Build succeeded, 0 errors, 0 warnings

**Step 2: Final test run**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=normal"
```
Expected: **78 passed, 0 failed, 0 skipped**
- 75 pre-existing tests still green
- 3 new LocalPlayer tests green

**Step 3: Push commits**

```bash
git push
```

**Step 4: Tag and push tag**

```bash
git tag v0.3-2-task1
git push origin v0.3-2-task1
```
