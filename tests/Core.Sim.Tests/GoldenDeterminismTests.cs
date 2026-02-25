namespace Core.Sim.Tests;

/// <summary>
/// Golden / pinned-checksum tests.
///
/// Purpose: if any change to SimStep, SimState fields, or SimHash causes the
/// simulation to produce a different final state after 1 000 scripted frames,
/// the build breaks immediately — catching determinism regressions before merge.
///
/// How the golden value was obtained:
///   1. Write test with placeholder expected = 0u.
///   2. Implement SimHash.cs.
///   3. Run dotnet test → failure message shows actual checksum.
///   4. Replace 0u with that value.
///   5. Run again → green.  Never recompute automatically.
/// </summary>
public class GoldenDeterminismTests
{
    // ── Deterministic input scripts ───────────────────────────────────────────

    /// <summary>
    /// P1 input for the given frame index (0-based, pre-Step).
    ///   0–199   : Right
    ///  200–219  : Jump
    ///  220–399  : Right
    ///  400–449  : Attack every 15 frames; otherwise no input
    ///  450–999  : Left for one 20-frame block, Right for the next (alternating)
    /// </summary>
    private static FrameInput ScriptP1(int frame)
    {
        if (frame < 200) return FrameInput.FromButtons(false, true,  false, false);
        if (frame < 220) return FrameInput.FromButtons(false, false, true,  false);
        if (frame < 400) return FrameInput.FromButtons(false, true,  false, false);
        if (frame < 450) return frame % 15 == 0
                                ? FrameInput.FromButtons(false, false, false, true)
                                : new FrameInput(0);
        // 450–999: alternate Left / Right every 20 frames
        return (frame / 20) % 2 == 0
               ? FrameInput.FromButtons(true,  false, false, false)
               : FrameInput.FromButtons(false, true,  false, false);
    }

    /// <summary>
    /// P2 input for the given frame index (0-based, pre-Step).
    ///   0–299   : Left
    ///  300–399  : Jump
    ///  400–499  : Attack every 17 frames; otherwise no input
    ///  500–999  : no input
    /// </summary>
    private static FrameInput ScriptP2(int frame)
    {
        if (frame < 300) return FrameInput.FromButtons(true, false, false, false);
        if (frame < 400) return FrameInput.FromButtons(false, false, true, false);
        if (frame < 500) return frame % 17 == 0
                                ? FrameInput.FromButtons(false, false, false, true)
                                : new FrameInput(0);
        return new FrameInput(0);
    }

    // ── Golden test ───────────────────────────────────────────────────────────

    [Fact]
    public void Golden_Seed1_ScriptedInputs_1000Frames_FinalChecksumMatches()
    {
        SimState state = SimState.CreateInitial(1u);

        for (int frame = 0; frame < 1_000; frame++)
            state = SimStep.Step(in state, ScriptP1(frame), ScriptP2(frame));

        uint checksum = SimHash.Checksum(in state);

        // ── PINNED ────────────────────────────────────────────────────────────
        // This constant must NEVER be updated without a deliberate, reviewed
        // change to the simulation logic.  Any accidental drift (wrong operator,
        // renamed constant, reordered field) will change this value and break the
        // build, alerting reviewers before the regression reaches production.
        //
        // To re-pin intentionally:
        //   1. Run dotnet test, read the "Actual" value from the failure message.
        //   2. Replace the constant below with that value.
        //   3. Commit with a clear message explaining WHY the simulation changed.
        const uint Pinned = 0x41B73DB7u; // 1102527927  — pinned 2026-02-25
        Assert.Equal(Pinned, checksum);
    }

    // ── Sensitivity test ──────────────────────────────────────────────────────

    [Fact]
    public void Checksum_Changes_WhenP1X_IsModified()
    {
        SimState s = SimState.CreateInitial(1u);

        uint before = SimHash.Checksum(in s);
        s.P1.X++;
        uint after  = SimHash.Checksum(in s);

        Assert.NotEqual(before, after);
    }
}
