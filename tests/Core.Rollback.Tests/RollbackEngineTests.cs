using Core.Sim;

namespace Core.Rollback.Tests;

public class RollbackEngineTests
{
    // ── Deterministic input scripts ───────────────────────────────────────────

    private static readonly FrameInput Neutral = new(0);
    private static readonly FrameInput Left    = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput Right   = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput Jump    = FrameInput.FromButtons(false, false, true,  false);
    private static readonly FrameInput Attack  = FrameInput.FromButtons(false, false, false, true);

    /// <summary>
    /// P1: 0–49 Right, 50 Jump, 51–149 Right, 150–199 Attack every 20 frames, 200+ Left.
    /// </summary>
    private static FrameInput ScriptP1(uint f)
    {
        if (f < 50)   return Right;
        if (f == 50)  return Jump;
        if (f < 150)  return Right;
        if (f < 200)  return f % 20 == 0 ? Attack : Neutral;
        return Left;
    }

    /// <summary>
    /// P2: 0–99 Left, 100–119 Jump, 120+ neutral.
    /// </summary>
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
        Assert.Equal(expected.Frame,      actual.Frame);
        Assert.Equal(expected.P1.X,       actual.P1.X);
        Assert.Equal(expected.P1.Y,       actual.P1.Y);
        Assert.Equal(expected.P1.Hp,      actual.P1.Hp);
        Assert.Equal(expected.P1.State,   actual.P1.State);
        Assert.Equal(expected.P1.Facing,  actual.P1.Facing);
        Assert.Equal(expected.P2.X,       actual.P2.X);
        Assert.Equal(expected.P2.Y,       actual.P2.Y);
        Assert.Equal(expected.P2.Hp,      actual.P2.Hp);
        Assert.Equal(expected.P2.State,   actual.P2.State);
        Assert.Equal(expected.P2.Facing,  actual.P2.Facing);
        Assert.Equal(expected.Rng.State,  actual.Rng.State);
    }

    // ── 1) No-lag: engine equals ground truth with zero rollbacks ─────────────

    [Fact]
    public void NoLag_EqualsGroundTruth_AndNoRollbacks()
    {
        const uint seed   = 1u;
        const int  frames = 300;

        var gt     = GroundTruthRun(seed, frames);
        var engine = new RollbackEngine(SimState.CreateInitial(seed), historyCapacity: 512);

        for (uint f = 0; f < (uint)frames; f++)
        {
            // Real remote input delivered before the tick → no prediction needed.
            engine.SetRemoteInput(f, ScriptP2(f));
            engine.Tick(ScriptP1(f));
        }

        AssertStatesMatch(gt, engine.CurrentState);
        Assert.Equal(0, engine.RollbackCount);
    }

    // ── 2) Lag: delayed remote inputs converge to ground truth ────────────────

    [Fact]
    public void Lag_DelayedRemoteInputs_ConvergesToGroundTruth()
    {
        const int  delay  = 6;
        const int  frames = 300;
        const uint seed   = 1u;

        var gt     = GroundTruthRun(seed, frames);
        var engine = new RollbackEngine(SimState.CreateInitial(seed), historyCapacity: 512);

        // Remote input for frame f arrives at tick (f + delay).
        for (uint t = 0; t < (uint)frames; t++)
        {
            if (t >= (uint)delay)
            {
                uint deliveredFrame = t - (uint)delay;
                engine.SetRemoteInput(deliveredFrame, ScriptP2(deliveredFrame));
            }
            engine.Tick(ScriptP1(t));
        }

        // Drain the last <delay> remote inputs that arrive after the main loop.
        for (uint f = (uint)(frames - delay); f < (uint)frames; f++)
            engine.SetRemoteInput(f, ScriptP2(f));

        AssertStatesMatch(gt, engine.CurrentState);

        Assert.True(engine.RollbackCount > 0,
            $"Expected at least one rollback but RollbackCount={engine.RollbackCount}");
        Assert.True(engine.MaxRollbackDepth <= 64,
            $"MaxRollbackDepth={engine.MaxRollbackDepth} exceeded sanity bound of 64");
    }

    // ── 3) Out-of-order: engine converges regardless of delivery order ────────

    [Fact]
    public void OutOfOrderRemoteInsert_DoesNotBreakConvergence()
    {
        const int  frames = 120;
        const uint seed   = 1u;

        var gt     = GroundTruthRun(seed, frames);
        var engine = new RollbackEngine(SimState.CreateInitial(seed), historyCapacity: 512);

        // Tick 120 frames with prediction only (no real remote inputs yet).
        for (uint f = 0; f < (uint)frames; f++)
            engine.Tick(ScriptP1(f));

        // Deliver out of order: 50, 10, 80, then the rest in forward order.
        engine.SetRemoteInput(50u, ScriptP2(50u));
        engine.SetRemoteInput(10u, ScriptP2(10u));
        engine.SetRemoteInput(80u, ScriptP2(80u));

        for (uint f = 0; f < (uint)frames; f++)
        {
            if (f != 10u && f != 50u && f != 80u)
                engine.SetRemoteInput(f, ScriptP2(f));
        }

        AssertStatesMatch(gt, engine.CurrentState);
    }

    // ── 4) Near-zero underflow safety ─────────────────────────────────────────

    [Fact]
    public void UnderflowSafety_PredictNearFrameZero()
    {
        // Exercises the backward-search code path in InputBuffer.GetOrPredict
        // when frame is 0 or 1 — uint underflow must not occur.
        var engine = new RollbackEngine(SimState.CreateInitial(1u), historyCapacity: 32);

        engine.SetRemoteInput(0u, ScriptP2(0u));
        engine.Tick(ScriptP1(0u)); // 0 → 1

        engine.SetRemoteInput(1u, ScriptP2(1u));
        engine.Tick(ScriptP1(1u)); // 1 → 2

        Assert.Equal(2u, engine.CurrentFrame);
    }
}
