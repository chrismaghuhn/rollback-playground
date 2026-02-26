# Design: Godot 4 Rollback Demo (v0.2-2)

**Date:** 2026-02-26
**Scope:** `game/` directory — Godot 4.6.1 C# offline demo
**Status:** Approved

---

## Goal

A single-scene Godot 4 demo that makes rollback-netcode mechanics visible and interactive:
- Two character rectangles (P1 keyboard, P2 scripted)
- Adjustable simulated lag (DelayFrames slider 0–10)
- Debug overlay showing live rollback statistics
- No Godot APIs anywhere in `Core.Sim` / `Core.Rollback`

---

## Files

```
game/
├── game.csproj          ← NEW  Godot.NET.Sdk/4.6.1, net8.0, refs Core.Sim + Core.Rollback
├── project.godot        ← UPDATE  main_scene, C# dotnet section, physics 60 Hz
├── RollbackDemo.tscn    ← NEW  minimal: Node2D root + attached script
└── RollbackDemo.cs      ← NEW  ~260 lines, 6 labelled sections
```

---

## Coordinate System

| | Value |
|---|---|
| Viewport | 1280 × 600 px |
| Arena world-units | 20 × 12 wu |
| `scalePxPerWu` | `600f / 12f = 50 px/wu` (height-driven, exact vertical fit) |
| `pxPerFixed` | `scalePxPerWu / SimConstants.FixedScale = 50f / 1000f = 0.05f` |
| `arenaPxW` | `SimConstants.MaxX * pxPerFixed = 20 000 * 0.05 = 1000 px` |
| `marginX` | `(1280 - 1000) * 0.5f = 140 px` (centred) |

**Coordinate conversion:**
```
screenX(simX) = marginX + simX * pxPerFixed
screenY(simY) = viewportH - simY * pxPerFixed
```

Godot Y grows downward; sim Y grows upward → flip via `viewportH - ...`.
Player rect drawn at `(screenX, screenY - playerHpx)` so the bottom edge sits on `screenY`.

**Player pixel sizes (derived from SimConstants):**
```
playerWpx = SimConstants.PlayerWidth  * pxPerFixed   // 600 * 0.05 = 30 px
playerHpx = SimConstants.PlayerHeight * pxPerFixed   // 900 * 0.05 = 45 px
```

---

## RollbackDemo.cs Structure

Six clearly labelled sections inside a single `Node2D` subclass:

### `// --- Config ---`
```csharp
private const uint   Seed           = 1u;
private const int    HistoryCap     = 64;
private const float  ViewportW      = 1280f;
private const float  ViewportH      = 600f;
private const float  ScalePxPerWu   = ViewportH / 12f;          // 50 px/wu
private const float  PxPerFixed     = ScalePxPerWu / SimConstants.FixedScale;
private const float  ArenaPxW       = SimConstants.MaxX * PxPerFixed;
private const float  MarginX        = (ViewportW - ArenaPxW) * 0.5f;
private const float  PlayerWpx      = SimConstants.PlayerWidth  * PxPerFixed;
private const float  PlayerHpx      = SimConstants.PlayerHeight * PxPerFixed;
```

### `// --- Godot lifecycle ---`
- `_Ready()`: create engine, build UI nodes, set window size hint
- `_PhysicsProcess(double delta)`: the single authoritative tick loop
- `_Draw()`: pure rendering, no logic

### `// --- Input mapping ---`
```csharp
private static FrameInput ReadP1Input() => FrameInput.FromButtons(
    left:   Input.IsKeyPressed(Key.A),
    right:  Input.IsKeyPressed(Key.D),
    jump:   Input.IsKeyPressed(Key.Space),
    attack: Input.IsKeyPressed(Key.J));
```

### `// --- Lag simulation + delivery ---`
```csharp
// P2 scripted input (60-frame cycle)
private static FrameInput ScriptP2Input(uint frame) {
    uint phase = frame % 60;
    bool right = phase < 30;
    bool left  = phase is >= 30 and < 45;
    bool jump  = phase >= 45;
    return FrameInput.FromButtons(left, right, jump, attack: false);
}

// Lag buffer: (deliveryFrame, simFrame, input)
private readonly Queue<(uint deliveryAt, uint simFrame, FrameInput input)> _lagBuffer = new();
private int _delayFrames = 0;

private void EnqueueP2(uint currentFrame) {
    _lagBuffer.Enqueue((currentFrame + (uint)_delayFrames, currentFrame, ScriptP2Input(currentFrame)));
}

private void DeliverDue(uint currentFrame) {
    while (_lagBuffer.Count > 0 && _lagBuffer.Peek().deliveryAt <= currentFrame) {
        var (_, simFrame, input) = _lagBuffer.Dequeue();
        _engine.SetRemoteInput(simFrame, input);
    }
}
```

Delay-slider change at runtime: queue is **not** flushed (residual items produce realistic jitter; acceptable for a demo).

### `// --- Rendering (_Draw) ---`
- `DrawArena()` — ground line (white), left/right walls (grey)
- `DrawPlayer(ref PlayerState p, Color c)` — filled rect, facing indicator (darker rect on "front" side), HP bar above
- `_Draw()` calls both, then no UI (UI is CanvasLayer nodes, not `_Draw()`)

### `// --- UI creation ---`
Built procedurally in `_Ready()` as children of a `CanvasLayer` (always on top):
- **Debug label** — top-left, fixed-width Monospace font, 5 lines
- **Lag HSlider** — top-right, range 0–10, step 1, value 0; `ValueChanged` → `_delayFrames = (int)v`
- **Lag label** — next to slider: `"Lag: N frames"`

---

## `_PhysicsProcess` — Strict Sequence

```
1. p1Input  ← ReadP1Input()
2. EnqueueP2(currentFrame)
3. DeliverDue(currentFrame)       // BEFORE Tick — may trigger rollback
4. _engine.Tick(p1Input)
5. UpdateDebugLabel()
6. QueueRedraw()
```

---

## Debug Label Content

```
Frame:           1234
RollbackCount:   42
MaxRollback:     5 frames
FramesRolled:    210
DelayFrames:     5
```

---

## `game.csproj`

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

---

## `project.godot` Changes

1. `[application] run/main_scene = "res://RollbackDemo.tscn"`
2. `[dotnet] project/assembly_name = "game"`
3. `[physics] common/physics_ticks_per_second = 60`

---

## Acceptance Criteria

- [ ] Demo opens in Godot 4.6.1 and runs at 60 Hz
- [ ] P1 (A/D/Space/J) controls left rectangle visibly
- [ ] P2 rectangle moves autonomously (60-frame cycle)
- [ ] Lag slider 0 → no rollbacks; slider > 0 → RollbackCount increases on phase boundaries
- [ ] Debug label shows live Frame, RollbackCount, MaxRollback, FramesRolled, DelayFrames
- [ ] `dotnet test RollbackPlayground.sln` remains green (core untouched)
- [ ] `dotnet build RollbackPlayground.sln -c Release` 0 warnings, 0 errors
