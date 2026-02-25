# Release Guide

How to verify, cut, and communicate a release of Rollback Netcode Playground.

---

## Preflight Checklist

Run through every item **in order** before tagging. All commands run from the
repo root.

```bash
# 1. Working tree must be clean (no uncommitted changes, no untracked files).
git status
# Expected: "nothing to commit, working tree clean"

# 2. Confirm the exact SDK version in use.
dotnet --version
# Expected: matches the version in global.json  (currently 10.0.103)

# 3. Run the full test suite in Debug configuration.
dotnet test RollbackPlayground.sln
# Expected: all tests pass, 0 warnings, 0 failures.
# Minimum counts (v0.1-mvp): 68 tests (36 Sim + 20 Rollback + 8 Replay + 4 Audit)

# 4. Full Release build — must also produce zero warnings.
dotnet build RollbackPlayground.sln -c Release
# Expected: Build succeeded. 0 Warning(s). 0 Error(s).
```

If any step fails, **do not tag**. Fix the issue, re-run from step 1.

---

## How to Cut v0.1-mvp

Once the preflight checklist is fully green:

```bash
# Create an annotated tag (the -m message appears in `git show`).
git tag -a v0.1-mvp -m "v0.1-mvp: deterministic sim + rollback core + replay format"

# Verify the tag was created correctly.
git show v0.1-mvp

# Push commits and the new tag to the remote.
git push origin main
git push origin v0.1-mvp
# Or push all tags at once:
# git push origin main --tags
```

> **Note**: Never use `git tag -f` to move an existing release tag.
> If a tag was applied to the wrong commit, delete it explicitly
> (`git tag -d v0.1-mvp`, push the deletion, re-create), and record
> the reason in `CHANGELOG.md`.

---

## How to Verify Determinism

Three automated checks confirm the simulation is deterministic:

### 1. Golden Checksum (regression lock)

```bash
dotnet test --filter "Golden_Seed1_ScriptedInputs_1000Frames_FinalChecksumMatches"
```

Runs 1 000 scripted frames from seed 1 and asserts the final FNV-1a checksum
equals the pinned value `0x41B73DB7`. Fails immediately if `SimStep` logic,
`SimConstants`, or `SimHash` field order changes.

### 2. Replay Round-Trip

```bash
dotnet test --filter "RecordThenPlay_EqualsGroundTruth"
```

Records 300 frames via `ReplayRecorder`, plays back via `ReplayPlayer`, and
asserts the result equals a direct `SimStep` loop over the same inputs.
Verifies the recorder/player pipeline introduces no divergence.

### 3. Rollback Lag Convergence

```bash
dotnet test --filter "Lag_DelayedRemoteInputs_ConvergesToGroundTruth"
```

Simulates 300 frames with a 6-frame remote-input delay, then drains the
backlog, and asserts the engine converges to the ground-truth final state.
Verifies that rollback + re-simulation is bit-exact.

---

## If You Intentionally Change Sim Behaviour

Any change to `SimStep`, `SimConstants`, or the field order in `SimHash` will
cause `Golden_Seed1_ScriptedInputs_1000Frames_FinalChecksumMatches` to fail.
This is intentional — the golden test is a regression lock, not a bug.

When you **deliberately** change sim behaviour, follow these steps:

1. **Make the change** to `Core.Sim`.

2. **Record the new checksum** by running a temporary assertion-free script or
   by reading the actual vs. expected values from the test failure message.

3. **Update the pinned constant** in
   `tests/Core.Sim.Tests/GoldenDeterminismTests.cs`:
   ```csharp
   const uint Pinned = 0xNEWVALUEu; // <new-value>  — pinned YYYY-MM-DD
   ```

4. **Update `docs/ASSUMPTIONS.md`** — edit the Constants Table (§6) and the
   golden-checksum row with the new value and the date.

5. **Add a `CHANGELOG.md` entry** under `[Unreleased]` (or the new version)
   describing what changed and why the checksum was updated.

6. **Run the full test suite** to confirm everything is green again.

7. **Commit all three files** (`SimStep`/`SimConstants` change + updated pinned
   constant + updated docs) in a single atomic commit so `git bisect` can
   isolate the exact change.
