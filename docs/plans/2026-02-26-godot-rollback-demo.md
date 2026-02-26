# Godot Rollback Demo Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a playable Godot 4.6.1 C# demo in `game/` that visualises rollback-netcode by wiring `Core.Rollback.RollbackEngine` to keyboard input (P1) and a scripted lag-delayed remote (P2).

**Architecture:** Single `RollbackDemo.cs` Node2D script; all Godot nodes created procedurally in `_Ready()`; `_PhysicsProcess` is the authoritative tick loop; `_Draw()` renders everything. No Godot API outside `game/`.

**Tech Stack:** Godot 4.6.1 / `Godot.NET.Sdk/4.6.1` / `net8.0` / `Core.Sim` + `Core.Rollback` via ProjectReference.

**Note on TDD:** `RollbackDemo.cs` is a Godot rendering/presentation layer — there is no meaningful unit-testable surface. All business logic (`RollbackEngine`, `SimStep`) is already covered by 68 passing tests. Acceptance criterion is `dotnet build game/game.csproj` clean + manual run in Godot editor.

---

## Task 1: `game/game.csproj`

**Files:**
- Create: `game/game.csproj`

**Step 1: Create the csproj**

```xml
<Project Sdk="Godot.NET.Sdk/4.6.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/Core.Sim/Core.Sim.csproj" />
    <ProjectReference Include="../src/Core.Rollback/Core.Rollback.csproj" />
  </ItemGroup>
</Project>
```

`EnableDynamicLoading=true` is required by Godot's C# hot-reload mechanism.
Paths are relative to `game/game.csproj`.

**Step 2: Verify restore works**

```bash
dotnet restore game/game.csproj
```
Expected: `Restore complete` — Godot.NET.Sdk/4.6.1 and GodotSharp packages downloaded.

**Step 3: Commit**

```bash
git add game/game.csproj
git commit -m "feat(game): add game.csproj — Godot.NET.Sdk/4.6.1, net8.0, refs Core.Sim+Core.Rollback"
```

---

## Task 2: Update `project.godot` + create `RollbackDemo.tscn`

**Files:**
- Modify: `game/project.godot`
- Create: `game/RollbackDemo.tscn`

**Step 1: Update `project.godot`**

Replace the full file content with:

```ini
; Engine configuration file.
; It's best edited using the editor UI and not directly,
; since the parameters that go here are not all obvious.
;
; Format:
;   [section] ; section goes between []
;   param=value ; assign values to parameters

config_version=5

[application]

config/name="game"
config/features=PackedStringArray("4.6", "Forward Plus")
config/icon="res://icon.svg"
run/main_scene="res://RollbackDemo.tscn"

[display]

window/size/viewport_width=1280
window/size/viewport_height=600

[dotnet]

project/assembly_name="game"

[physics]

3d/physics_engine="Jolt Physics"
common/physics_ticks_per_second=60

[rendering]

rendering_device/driver.windows="d3d12"
```

Key additions:
- `run/main_scene` — launches `RollbackDemo.tscn` on play
- `[display]` — fixes viewport to 1280×600
- `[dotnet]` — names the C# assembly `game` (must match project dir name)
- `common/physics_ticks_per_second=60` — locks physics loop to 60 Hz (= sim tick rate)

**Step 2: Create `RollbackDemo.tscn`**

```
[gd_scene load_steps=2 format=3 uid="uid://rollbackdemo01"]

[ext_resource type="Script" path="res://RollbackDemo.cs" id="1_demo01"]

[node name="RollbackDemo" type="Node2D"]
script = ExtResource("1_demo01")
```

This is the minimum scene: a single `Node2D` root with the C# script attached.
Godot will update UIDs on first import — that's expected and fine.

**Step 3: Commit**

```bash
git add game/project.godot game/RollbackDemo.tscn
git commit -m "feat(game): configure main scene, viewport 1280x600, physics 60 Hz"
```

---

## Task 3: `game/RollbackDemo.cs` — complete implementation

**Files:**
- Create: `game/RollbackDemo.cs`

All six sections are in one file. Write the entire file at once.

**Step 1: Create `game/RollbackDemo.cs`**

```csharp
using Godot;
using Core.Sim;
using Core.Rollback;
using System.Collections.Generic;

/// <summary>
/// Rollback-netcode demo scene.
///
/// P1 = keyboard  (A = left, D = right, Space = jump, J = attack).
/// P2 = scripted  (deterministic 60-frame cycle), delivered with
///      configurable lag to trigger visible rollbacks.
///
/// Physics runs at exactly 60 Hz (project setting).
/// All sim logic is in Core.Rollback / Core.Sim — no engine calls there.
/// </summary>
public partial class RollbackDemo : Node2D
{
    // ─── Config ──────────────────────────────────────────────────────────────

    private const uint  Seed         = 1u;
    private const int   HistoryCap   = 64;

    // Viewport dimensions (must match project.godot display settings)
    private const float ViewportW    = 1280f;
    private const float ViewportH    = 600f;

    // Scale: fit arena height (12 wu) exactly into viewport height
    private const float ScalePxPerWu = ViewportH / 12f;                          // 50 px/wu
    private const float PxPerFixed   = ScalePxPerWu / SimConstants.FixedScale;   // 0.05 px/fixed-unit

    // Arena pixel dimensions and horizontal centering margin
    private const float ArenaPxW  = SimConstants.MaxX * PxPerFixed;              // 1000 px
    private const float MarginX   = (ViewportW - ArenaPxW) * 0.5f;               // 140 px

    // Player visual dimensions (derived from sim constants — stays correct if constants change)
    private const float PlayerWpx = SimConstants.PlayerWidth  * PxPerFixed;      // 30 px
    private const float PlayerHpx = SimConstants.PlayerHeight * PxPerFixed;      // 45 px

    private static readonly Color ColArena  = new(0.15f, 0.15f, 0.15f);
    private static readonly Color ColP1     = new(0.35f, 0.60f, 1.00f);  // blue
    private static readonly Color ColP2     = new(1.00f, 0.40f, 0.30f);  // red
    private static readonly Color ColHpFill = new(0.20f, 0.80f, 0.20f);
    private static readonly Color ColHpBg   = new(0.50f, 0.10f, 0.10f);

    // ─── Godot lifecycle ─────────────────────────────────────────────────────

    private RollbackEngine _engine    = null!;
    private Label          _debugLbl  = null!;
    private Label          _lagLbl    = null!;

    public override void _Ready()
    {
        _engine = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap);
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        uint f = _engine.CurrentFrame;

        FrameInput p1 = ReadP1Input();   // 1. Poll local input
        EnqueueP2(f);                    // 2. Generate + schedule P2's real input
        DeliverDue(f);                   // 3. Deliver any inputs due this frame (may rollback)
        _engine.Tick(p1);               // 4. Advance sim
        UpdateDebugLabel();             // 5. Refresh HUD text
        QueueRedraw();                  // 6. Request _Draw
    }

    public override void _Draw()
    {
        DrawArena();
        DrawPlayer(_engine.CurrentState.P1, ColP1);
        DrawPlayer(_engine.CurrentState.P2, ColP2);
    }

    // ─── Input mapping ───────────────────────────────────────────────────────

    private static FrameInput ReadP1Input() => FrameInput.FromButtons(
        left:   Input.IsKeyPressed(Key.A),
        right:  Input.IsKeyPressed(Key.D),
        jump:   Input.IsKeyPressed(Key.Space),
        attack: Input.IsKeyPressed(Key.J));

    // ─── Lag simulation + delivery ───────────────────────────────────────────

    private readonly Queue<(uint deliveryAt, uint simFrame, FrameInput input)> _lagBuffer = new();
    private int _delayFrames = 0;

    /// <summary>
    /// Deterministic 60-frame input cycle for P2.
    ///   Frames  0-29 → Right
    ///   Frames 30-44 → Left
    ///   Frames 45-59 → Jump
    /// The phase boundary at frame 30 (Right→Left) will reliably cause a mismatch
    /// whenever _delayFrames > 0, making rollbacks immediately visible.
    /// </summary>
    private static FrameInput ScriptP2Input(uint frame)
    {
        uint phase = frame % 60u;
        bool right = phase < 30u;
        bool left  = phase is >= 30u and < 45u;
        bool jump  = phase >= 45u;
        return FrameInput.FromButtons(left, right, jump, attack: false);
    }

    /// <summary>Schedules P2's real input for <paramref name="currentFrame"/> to arrive after the configured lag.</summary>
    private void EnqueueP2(uint currentFrame) =>
        _lagBuffer.Enqueue((currentFrame + (uint)_delayFrames, currentFrame, ScriptP2Input(currentFrame)));

    /// <summary>Delivers every enqueued P2 input whose delivery frame has arrived.</summary>
    private void DeliverDue(uint currentFrame)
    {
        while (_lagBuffer.Count > 0 && _lagBuffer.Peek().deliveryAt <= currentFrame)
        {
            var (_, simFrame, input) = _lagBuffer.Dequeue();
            _engine.SetRemoteInput(simFrame, input);
        }
    }

    // ─── Rendering (_Draw) ───────────────────────────────────────────────────

    private void DrawArena()
    {
        float left   = MarginX;
        float right  = MarginX + ArenaPxW;
        float bottom = ViewportH;

        DrawLine(new Vector2(left, bottom),  new Vector2(right, bottom), Colors.White,   2f); // ground
        DrawLine(new Vector2(left, 0f),      new Vector2(left, bottom),  Colors.DimGray, 2f); // left wall
        DrawLine(new Vector2(right, 0f),     new Vector2(right, bottom), Colors.DimGray, 2f); // right wall
    }

    private void DrawPlayer(PlayerState p, Color color)
    {
        float sx = MarginX + p.X * PxPerFixed;
        float sy = ViewportH - p.Y * PxPerFixed;  // flip Y: sim Y up, Godot Y down

        // Body
        var body = new Rect2(sx, sy - PlayerHpx, PlayerWpx, PlayerHpx);
        DrawRect(body, color);

        // Facing indicator — 4 px strip on the "front" edge
        float stripX = p.Facing > 0 ? sx + PlayerWpx - 4f : sx;
        DrawRect(new Rect2(stripX, sy - PlayerHpx, 4f, PlayerHpx), color.Darkened(0.4f));

        // HP bar (6 px above player)
        float barY = sy - PlayerHpx - 7f;
        DrawRect(new Rect2(sx, barY, PlayerWpx, 4f), ColHpBg);
        float hpFrac = Mathf.Clamp(p.Hp / (float)SimConstants.DefaultHp, 0f, 1f);
        DrawRect(new Rect2(sx, barY, PlayerWpx * hpFrac, 4f), ColHpFill);
    }

    // ─── UI creation ─────────────────────────────────────────────────────────

    private void BuildUi()
    {
        var hud = new CanvasLayer();
        AddChild(hud);

        // Debug label — top-left, fixed monospace readout
        _debugLbl = new Label();
        _debugLbl.Position = new Vector2(8f, 8f);
        _debugLbl.AddThemeFontSizeOverride("font_size", 14);
        hud.AddChild(_debugLbl);
        UpdateDebugLabel();

        // Lag slider row — top-right
        _lagLbl = new Label();
        _lagLbl.Text = "0 frames";

        var slider = new HSlider();
        slider.MinValue          = 0;
        slider.MaxValue          = 10;
        slider.Step              = 1;
        slider.Value             = 0;
        slider.CustomMinimumSize = new Vector2(160f, 20f);
        slider.ValueChanged     += v =>
        {
            _delayFrames   = (int)v;
            _lagLbl.Text   = $"{(int)v} frames";
        };

        var lagHeaderLbl = new Label();
        lagHeaderLbl.Text = "Lag: ";

        var row = new HBoxContainer();
        row.Position = new Vector2(ViewportW - 280f, 8f);
        row.AddChild(lagHeaderLbl);
        row.AddChild(slider);
        row.AddChild(_lagLbl);
        hud.AddChild(row);
    }

    private void UpdateDebugLabel()
    {
        _debugLbl.Text =
            $"Frame:         {_engine.CurrentFrame,7}\n"       +
            $"RollbackCount: {_engine.RollbackCount,7}\n"      +
            $"MaxRollback:   {_engine.MaxRollbackDepth,7} frames\n" +
            $"FramesRolled:  {_engine.RollbackFramesTotal,7}\n" +
            $"DelayFrames:   {_delayFrames,7}";
    }
}
```

**Step 2: Verify it compiles**

```bash
dotnet build game/game.csproj
```
Expected: `Build succeeded.` — 0 errors. Warnings from Godot SDK about missing editor binary are acceptable (runtime-only).

If the build fails because `Godot.NET.Sdk/4.6.1` cannot be resolved, check:
- Internet connection (first-time NuGet restore)
- NuGet source: `https://api.nuget.org/v3/index.json` must be in the active sources

**Step 3: Commit**

```bash
git add game/RollbackDemo.cs
git commit -m "feat(game): add RollbackDemo.cs — keyboard P1, scripted lag-delayed P2, debug HUD"
```

---

## Task 4: Final core verification + commit

**Goal:** Confirm that adding the game project has not touched or broken the core solution.

**Step 1: Run core test suite**

```bash
dotnet test RollbackPlayground.sln
```
Expected:
```
Passed! : 40 Core.Sim.Tests
Passed! : 20 Core.Rollback.Tests
Passed! :  8 Core.Replay.Tests
```

**Step 2: Release build**

```bash
dotnet build RollbackPlayground.sln -c Release
```
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 3: Commit**

Only if both steps passed — there should be nothing to commit (game files were already committed in tasks 1-3). If not:

```bash
git status   # should show: nothing to commit, working tree clean
```

**Step 4: Manual acceptance in Godot editor**

Open `game/` in Godot 4.6.1 editor, press Play (F5):
- [ ] Two coloured rectangles visible, P1 (blue) left, P2 (red) right
- [ ] A/D moves P1 left/right; Space jumps; J triggers attack animation
- [ ] P2 moves autonomously (Right 30 frames → Left 15 frames → Jump 15 frames → repeat)
- [ ] Lag slider = 0: `RollbackCount` stays 0
- [ ] Lag slider > 0: `RollbackCount` increments every ~30 frames when P2 changes phase
- [ ] Debug label updates every frame (Frame counter climbs)
- [ ] HP bars drain when J (P1 attack) lands on P2

---

## Coordinate Reference (for debugging)

| Quantity | Sim value | Screen px |
|---|---|---|
| P1 start X | 4 000 | `140 + 4000*0.05 = 340 px` |
| P2 start X | 16 000 | `140 + 16000*0.05 = 940 px` |
| Arena width | 20 000 | 1 000 px |
| Player width | 600 | 30 px |
| Player height | 900 | 45 px |
| Ground (Y=0) | 0 | 600 px (bottom) |
