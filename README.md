# Rollback Netcode Playground

> Deterministic 2D simulation + rollback netcode engine in pure C# — no floats, no engine APIs, no surprises.

## Architecture

| Project | Role |
|---------|------|
| `Core.Sim` | Pure step-function `(SimState, FrameInput) → SimState`. Integers only, explicit PRNG. |
| `Core.Rollback` | Input/state circular buffers, prediction (repeat-last), rollback + re-simulation. |
| `Core.Replay` | Record/playback pipeline + binary `.rplk` serializer. |

The `game/` directory contains a Godot 4 project (future visual frontend, out of scope for MVP v0.1).

## Quickstart

```bash
# Requires .NET 10 SDK  →  https://dotnet.microsoft.com/download
dotnet build RollbackPlayground.sln
dotnet test  RollbackPlayground.sln
```

## Hard Rules

1. **Determinism:** `Core.Sim` uses only `int`/`uint`, explicit PRNG state — zero floats.
2. **No time access:** Simulation never reads wall-clock or engine APIs.
3. **Inputs only:** Network layer sends `FrameInput`, never `SimState`.
4. **Tests required:** Every feature ships with passing `dotnet test`.

See [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md) for all design decisions and magic numbers.

## Status

| Milestone | Status |
|-----------|--------|
| MVP v0.1 (Core.Sim + Rollback + Replay) | 🚧 In progress |
| v0.2 (UDP netcode + desync detection)   | 📋 Planned |
