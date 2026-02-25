using Core.Replay;
using Core.Sim;

namespace Core.Replay.Tests;

public class ReplayPipelineTests
{
    // ── Deterministic input scripts (identical to RollbackEngineTests) ────────
    // Kept in sync intentionally: same scripts allow cross-suite comparisons.

    private static readonly FrameInput Neutral = new(0);
    private static readonly FrameInput Left    = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput Right   = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput Jump    = FrameInput.FromButtons(false, false, true,  false);
    private static readonly FrameInput Attack  = FrameInput.FromButtons(false, false, false, true);

    /// <summary>P1: 0–49 Right, 50 Jump, 51–149 Right, 150–199 Attack every 20 frames, 200+ Left.</summary>
    private static FrameInput ScriptP1(uint f)
    {
        if (f < 50)   return Right;
        if (f == 50)  return Jump;
        if (f < 150)  return Right;
        if (f < 200)  return f % 20 == 0 ? Attack : Neutral;
        return Left;
    }

    /// <summary>P2: 0–99 Left, 100–119 Jump, 120+ neutral.</summary>
    private static FrameInput ScriptP2(uint f)
    {
        if (f < 100) return Left;
        if (f < 120) return Jump;
        return Neutral;
    }

    /// <summary>Reference run — direct SimStep loop, no Replay machinery involved.</summary>
    private static SimState GroundTruthRun(uint seed, int frames)
    {
        var state = SimState.CreateInitial(seed);
        for (int i = 0; i < frames; i++)
            state = SimStep.Step(in state, ScriptP1((uint)i), ScriptP2((uint)i));
        return state;
    }

    // ── 1) Record → Play produces the same final state as direct SimStep ──────

    [Fact]
    public void RecordThenPlay_EqualsGroundTruth()
    {
        const uint seed   = 1u;
        const int  frames = 300;

        var recorder = new ReplayRecorder(seed);
        for (uint f = 0; f < (uint)frames; f++)
            recorder.Append(ScriptP1(f), ScriptP2(f));

        Replay replay = recorder.Build();

        SimState fromReplay   = ReplayPlayer.Play(replay);
        SimState groundTruth  = GroundTruthRun(seed, frames);

        Assert.Equal(groundTruth.Frame,     fromReplay.Frame);
        Assert.Equal(groundTruth.P1.X,      fromReplay.P1.X);
        Assert.Equal(groundTruth.P1.Y,      fromReplay.P1.Y);
        Assert.Equal(groundTruth.P1.Hp,     fromReplay.P1.Hp);
        Assert.Equal(groundTruth.P2.X,      fromReplay.P2.X);
        Assert.Equal(groundTruth.P2.Y,      fromReplay.P2.Y);
        Assert.Equal(groundTruth.P2.Hp,     fromReplay.P2.Hp);
        Assert.Equal(groundTruth.Rng.State, fromReplay.Rng.State);

        uint csReplay = SimHash.Checksum(in fromReplay);
        uint csGt     = SimHash.Checksum(in groundTruth);
        Assert.Equal(csGt, csReplay);
    }

    // ── 2) Replay object carries correct metadata after Build ─────────────────

    [Fact]
    public void Replay_Metadata_IsConsistent()
    {
        const uint seed = 123u;
        const int  n    = 42;

        var recorder = new ReplayRecorder(seed);
        for (int i = 0; i < n; i++)
            recorder.Append(Neutral, Neutral);

        Replay replay = recorder.Build();

        Assert.Equal(seed, replay.Seed);
        Assert.Equal(0u,   replay.StartFrame);
        Assert.Equal(n,    replay.FrameCount);
        Assert.Equal(n,    replay.Frames.Count);
    }

    // ── 3) Playing the same Replay twice yields the same checksum ─────────────

    [Fact]
    public void Play_Twice_IsDeterministic()
    {
        const uint seed   = 7u;
        const int  frames = 150;

        var recorder = new ReplayRecorder(seed);
        for (uint f = 0; f < (uint)frames; f++)
            recorder.Append(ScriptP1(f), ScriptP2(f));

        Replay replay = recorder.Build();

        SimState run1 = ReplayPlayer.Play(replay);
        SimState run2 = ReplayPlayer.Play(replay);

        uint cs1 = SimHash.Checksum(in run1);
        uint cs2 = SimHash.Checksum(in run2);
        Assert.Equal(cs1, cs2);
    }
}
