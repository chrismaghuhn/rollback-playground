# Design: v0.2-2 Release Hygiene + Mini-Polish

**Date:** 2026-02-26
**Scope:** Docs + `.gitignore` + `game/RollbackDemo.cs` additions
**Status:** Approved

---

## Overview

Four independent work items. No changes to `Core.Sim`, `Core.Rollback`, or `Core.Replay`.

| Item | Type | Files touched |
|---|---|---|
| 1A — CHANGELOG | Docs | `CHANGELOG.md` |
| 1B — README "Run the Demo" | Docs | `README.md` |
| 1C — `.claude/` gitignore | Config | `.gitignore` |
| 3 — Mini-Polish (overlay + F1/F2) | Code | `game/RollbackDemo.cs` |

Item 2 (GIF/screenshot) requires a manual screen-capture session in Godot — not automated.
Item 4 (v0.3 roadmap) is a one-line README status update bundled with 1B.

---

## Item 1A: CHANGELOG

Insert **above** `[v0.1-mvp]`, Keep-a-Changelog format:

```markdown
## [v0.2] — 2026-02-26

### Added
- Multi-target `net8.0;net10.0` for `Core.Sim`, `Core.Rollback`, `Core.Replay`
  (Godot 4 compatibility — Godot requires `net8.0`).
- **Godot 4.6.1 offline rollback demo** (`game/`)
  - Single-script `Node2D` — `RollbackDemo.cs`, six labelled sections.
  - P1 keyboard: A / D / Space / J (Left / Right / Jump / Attack).
  - P2 deterministic 60-frame script: Right 0–29 → Left 30–44 → Jump 45–59.
  - Lag slider 0–10 frames (simulates network delay, triggers visible rollbacks).
  - Debug overlay: Frame, LatestRemote, Remote (PRED/CONF), RollbackCount,
    MaxRollback, FramesRolled, DelayFrames.
  - F1 toggles debug overlay; F2 toggles full HUD (overlay + slider).
  - Dark arena background, HP bars, facing indicator strips.
```

Also update the footer link block:
```markdown
[Unreleased]: https://github.com/example/rollback-playground/compare/v0.2...HEAD
[v0.2]:       https://github.com/example/rollback-playground/compare/v0.1-mvp...v0.2
[v0.1-mvp]:   https://github.com/example/rollback-playground/releases/tag/v0.1-mvp
```

---

## Item 1B: README "Run the Demo"

Insert new section **after** `## Quickstart`, before `## Hard Rules`.
Also update the Status table and add a GIF placeholder.

### New section

````markdown
## Run the Demo

> **Prerequisites:** [Godot 4 .NET](https://godotengine.org/download/) (v4.6+, must include .NET support) · [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

1. Open Godot 4 → **Import** → select the `game/` folder → **Import & Edit**
2. Press **F5** (or ▶ Play) to run
3. Control P1 with **A / D** (move) · **Space** (jump) · **J** (attack)
4. Drag the **Lag slider** (top-right) to simulate network delay

| Lag slider | What you see |
|---|---|
| 0 frames | `RollbackCount` stays 0 — all inputs arrive on time |
| > 0 frames | `RollbackCount` increments every ~30 frames (P2 phase change triggers mismatch) |
| 5 frames | `MaxRollback` stabilises at 5 — 5-frame resimulation on each phase boundary |

**Hotkeys:** `F1` — toggle debug overlay · `F2` — toggle full HUD
````

### Status table update

Replace:
```
| MVP v0.1 (Core.Sim + Rollback + Replay) | 🚧 In progress |
| v0.2 (UDP netcode + desync detection)   | 📋 Planned |
```

With:
```
| v0.1-mvp (Core.Sim + Rollback + Replay) | ✅ Done |
| v0.2 (Godot offline demo + multi-target) | ✅ Done |
| v0.3 (LAN UDP netcode + desync detection) | 📋 Planned |
```

### GIF placeholder (after the Status table)

```markdown
<!-- GIF: record ~12s — Lag 0 → 5 → 0, watch RollbackCount climb -->
<!-- Place recording at docs/rollback-demo.gif then uncomment: -->
<!-- ![Rollback Demo — Lag slider + debug overlay](docs/rollback-demo.gif) -->
```

---

## Item 1C: `.gitignore`

Append to the `# ── OS` section at the bottom of the root `.gitignore`:

```
.claude/
```

---

## Item 3: Mini-Polish — `game/RollbackDemo.cs`

**No changes to Core.Sim, Core.Rollback, Core.Replay.**

### 3a. New fields (in `// ─── Lag simulation + delivery` section)

```csharp
// Tracks the highest simFrame for which a real remote input has been delivered.
// Initialised to uint.MaxValue (= "never delivered") — same sentinel pattern as InputBuffer/StateBuffer.
private uint _latestRemoteFrame = uint.MaxValue;
```

```csharp
// HUD visibility flags (F1 = debug label; F2 = full HUD).
private bool _overlayVisible = true;
private bool _hudVisible     = true;
```

Store a reference to the CanvasLayer for F2 toggle (add to `// ─── Godot lifecycle` field block):
```csharp
private CanvasLayer _hud = null!;
```

### 3b. `DeliverDue` — robust `_latestRemoteFrame` tracking

```csharp
private void DeliverDue(uint currentFrame)
{
    while (_lagBuffer.Count > 0 && _lagBuffer.Peek().deliveryAt <= currentFrame)
    {
        var (_, simFrame, input) = _lagBuffer.Dequeue();
        _engine.SetRemoteInput(simFrame, input);

        // Use Math.Max to remain monotone even if queue order shifts after
        // a live delay-slider change produces out-of-order deliveries.
        _latestRemoteFrame = _latestRemoteFrame == uint.MaxValue
            ? simFrame
            : Math.Max(_latestRemoteFrame, simFrame);
    }
}
```

### 3c. `UpdateDebugLabel` — extended 7-line overlay

```csharp
private void UpdateDebugLabel()
{
    // Determine PRED/CONF for the frame that was just simulated.
    // CurrentFrame has already been incremented by Tick, so the frame
    // we care about is CurrentFrame - 1.  Making this explicit prevents
    // accidental off-by-one "fixes".
    uint justSimulated = _engine.CurrentFrame == 0 ? 0u : _engine.CurrentFrame - 1u;
    bool confirmedForJustSimulated =
        _latestRemoteFrame != uint.MaxValue &&
        _latestRemoteFrame >= justSimulated;

    string remoteStatus = _latestRemoteFrame == uint.MaxValue
        ? "N/A "
        : confirmedForJustSimulated ? "CONF" : "PRED";

    string latestRemoteTxt = _latestRemoteFrame == uint.MaxValue
        ? "    ---"
        : $"{_latestRemoteFrame,7}";

    _debugLbl.Text =
        $"Frame:          {_engine.CurrentFrame,7}\n"           +
        $"LatestRemote:  {latestRemoteTxt}\n"                   +
        $"Remote:        {remoteStatus,7}\n"                    +
        $"RollbackCount: {_engine.RollbackCount,7}\n"           +
        $"MaxRollback:   {_engine.MaxRollbackDepth,7} frames\n" +
        $"FramesRolled:  {_engine.RollbackFramesTotal,7}\n"     +
        $"DelayFrames:   {_delayFrames,7}";
}
```

### 3d. `_UnhandledInput` — F1/F2 hotkeys

```csharp
public override void _UnhandledInput(InputEvent @event)
{
    if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        return;

    if (key.Keycode == Key.F1)
    {
        _overlayVisible   = !_overlayVisible;
        _debugLbl.Visible = _overlayVisible;
    }
    else if (key.Keycode == Key.F2)
    {
        _hudVisible   = !_hudVisible;
        _hud.Visible  = _hudVisible;
    }
}
```

### 3e. `BuildUi` — store `_hud` reference

In `BuildUi()`, replace:
```csharp
var hud = new CanvasLayer();
AddChild(hud);
```
With:
```csharp
_hud = new CanvasLayer();
AddChild(_hud);
```

And update all `hud.AddChild(...)` calls to `_hud.AddChild(...)`.

---

## Item 2: GIF/Screenshot (manual)

Not automated. Instructions for the user:

1. Open the demo in Godot, run at full speed
2. Record ~12 seconds:
   - Lag = 0 → hold for 2s (show RollbackCount = 0)
   - Drag slider to 5 → wait 3s (show RollbackCount climbing, MaxRollback = 5)
   - Drag back to 0
3. Save as `docs/rollback-demo.gif`
4. Uncomment the placeholder line in `README.md`

Recommended tools: ShareX (Windows), Kap (macOS), Peek (Linux).

---

## Acceptance Criteria

- [ ] CHANGELOG has `[v0.2] — 2026-02-26` with all listed additions
- [ ] README has "Run the Demo" section with prerequisites, steps, hotkeys table
- [ ] README Status table shows v0.1-mvp ✅, v0.2 ✅, v0.3 📋
- [ ] `.gitignore` ignores `.claude/`
- [ ] `_latestRemoteFrame` uses `Math.Max` in `DeliverDue`
- [ ] Overlay shows 7 lines including LatestRemote and REMOTE: PRED/CONF
- [ ] F1 toggles only `_debugLbl.Visible`; F2 toggles `_hud.Visible`
- [ ] `dotnet build game/game.csproj` — 0 errors
- [ ] `dotnet test RollbackPlayground.sln` — 68/68 pass
