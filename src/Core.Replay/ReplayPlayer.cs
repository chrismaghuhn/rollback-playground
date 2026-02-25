using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Deterministic offline playback of a <see cref="Replay"/>.
///
/// ── Design ───────────────────────────────────────────────────────────────────
///
/// Playback is a pure function: the same <see cref="Replay"/> always yields
/// the same final <see cref="SimState"/>.  There is no mutable instance state —
/// <see cref="ReplayPlayer"/> is a static class to make this explicit.
///
/// ── Why only inputs are replayed (no snapshots) ──────────────────────────────
///
/// The simulation in <c>Core.Sim</c> is fully deterministic:
///   <c>SimState.CreateInitial(seed)</c> always produces the same starting
///   state, and <c>SimStep.Step</c> is a pure function with no hidden
///   side-effects.  Therefore the complete game history is uniquely determined
///   by the seed and the sequence of inputs — no intermediate snapshots are
///   needed and storing them would only waste space.
///
/// Playback loops over the flat <see cref="Replay.Frames"/> array with a plain
/// index variable.  No LINQ or iterator allocation occurs in the hot loop.
///
/// ── StartFrame != 0 ──────────────────────────────────────────────────────────
///
/// For MVP the player always constructs the initial state via
/// <c>SimState.CreateInitial(seed)</c> and then steps through every recorded
/// frame starting at index 0.  A non-zero <see cref="Replay.StartFrame"/>
/// therefore indicates a mid-session recording, which is not yet supported.
/// <see cref="Play"/> throws <see cref="NotSupportedException"/> in that case
/// rather than silently producing incorrect output.  Future work can support
/// this by accepting an initial <see cref="SimState"/> alongside the replay.
/// </summary>
public static class ReplayPlayer
{
    /// <summary>
    /// Replays <paramref name="replay"/> from the beginning and returns the
    /// final <see cref="SimState"/> after all recorded frames have been stepped.
    /// </summary>
    /// <param name="replay">The replay to play back.  Must not be <see langword="null"/>.</param>
    /// <returns>
    ///   The deterministic final simulation state after applying every frame
    ///   in <paramref name="replay"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///   Thrown when <paramref name="replay"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///   Thrown when <paramref name="replay"/>.<see cref="Replay.StartFrame"/> is
    ///   non-zero (mid-session replays are not supported in this MVP).
    /// </exception>
    public static SimState Play(Replay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);

        if (replay.StartFrame != 0u)
            throw new NotSupportedException(
                $"Mid-session replays (StartFrame={replay.StartFrame}) are not supported. " +
                "Pass a full-session replay with StartFrame=0.");

        SimState state = SimState.CreateInitial(replay.Seed);

        IReadOnlyList<ReplayFrame> frames = replay.Frames;
        int count = frames.Count;

        // Hot loop — no LINQ, no iterator, no heap allocation.
        for (int i = 0; i < count; i++)
        {
            ReplayFrame frame = frames[i];
            SimState    prev  = state;
            state = SimStep.Step(in prev, frame.P1, frame.P2);
        }

        return state;
    }

    /// <summary>
    /// Convenience overload that plays back <paramref name="replay"/> and
    /// returns the FNV-1a checksum of the final state.
    ///
    /// Useful for cheap determinism assertions: compare two checksums instead
    /// of comparing every field individually.
    /// </summary>
    /// <param name="replay">The replay to play back.</param>
    /// <returns>FNV-1a 32-bit checksum of the final <see cref="SimState"/>.</returns>
    public static uint PlayAndChecksum(Replay replay)
    {
        SimState final = Play(replay);
        return SimHash.Checksum(in final);
    }
}
