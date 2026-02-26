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
        Assert.True(engine.MaxRollbackDepth <= 64,
            $"MaxRollbackDepth={engine.MaxRollbackDepth} exceeded sanity bound of 64");
    }
}
