# Rollback Netcode Playground

> Deterministic 2D simulation + rollback netcode engine in pure C# — no floats, no engine APIs, no surprises.

## Architecture

| Project | Role |
|---------|------|
| `Core.Sim` | Pure step-function `(SimState, FrameInput) → SimState`. Integers only, explicit PRNG. |
| `Core.Rollback` | Input/state circular buffers, prediction (repeat-last), rollback + re-simulation. |
| `Core.Replay` | Record/playback pipeline + binary `.rplk` serializer. |

The `game/` directory contains a Godot 4.6.1 `.NET` demo — see [Run the Demo](#run-the-demo).

## Quickstart

```bash
# Requires .NET 10 SDK  →  https://dotnet.microsoft.com/download
dotnet build RollbackPlayground.sln
dotnet test  RollbackPlayground.sln
```

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

## Hard Rules

1. **Determinism:** `Core.Sim` uses only `int`/`uint`, explicit PRNG state — zero floats.
2. **No time access:** Simulation never reads wall-clock or engine APIs.
3. **Inputs only:** Network layer sends `FrameInput`, never `SimState`.
4. **Tests required:** Every feature ships with passing `dotnet test`.

See [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md) for all design decisions and magic numbers.

## Status

| Milestone | Status |
|-----------|--------|
| v0.1-mvp (Core.Sim + Rollback + Replay)   | ✅ Done    |
| v0.2 (Godot offline demo + multi-target)  | ✅ Done    |
| v0.3 (LAN UDP netcode + desync detection) | 📋 Planned |

<!-- GIF: record ~12s — Lag 0 → 5 → 0, watch RollbackCount climb -->
<!-- Place recording at docs/rollback-demo.gif then uncomment: -->
<!-- ![Rollback Demo — Lag slider + debug overlay](docs/rollback-demo.gif) -->
