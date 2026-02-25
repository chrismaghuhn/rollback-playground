using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Accumulates per-frame inputs during a live session and produces an
/// immutable <see cref="Replay"/> snapshot via <see cref="Build"/>.
///
/// ── Responsibilities ─────────────────────────────────────────────────────────
///
///   • <see cref="Append"/> records both player inputs for the current frame
///     and advances <see cref="CurrentFrame"/> by one.
///   • <see cref="Build"/> hands ownership of a deep-copied <see cref="Replay"/>
///     to the caller; the recorder is still usable after calling Build (though
///     typical usage discards it).
///
/// ── No allocation inside Append ──────────────────────────────────────────────
///
/// The internal <see cref="List{T}"/> may double its backing array on a
/// capacity boundary, but that is a standard amortised O(1) operation.
/// There is no LINQ, iterator, or per-frame heap allocation inside
/// <see cref="Append"/>.
/// </summary>
public sealed class ReplayRecorder
{
    private readonly List<ReplayFrame> _frames;
    private readonly uint _seed;
    private readonly uint _startFrame;

    /// <summary>
    /// The absolute frame number that will be recorded by the next
    /// <see cref="Append"/> call.  Starts at <paramref name="startFrame"/>.
    /// </summary>
    public uint CurrentFrame { get; private set; }

    /// <summary>
    /// Initialises a new recorder.
    /// </summary>
    /// <param name="seed">PRNG seed — embedded in the resulting <see cref="Replay"/>.</param>
    /// <param name="startFrame">
    ///   Absolute frame number of the first recorded tick (normally <c>0</c>).
    /// </param>
    /// <param name="initialCapacity">
    ///   Pre-allocated frame-list capacity; avoids reallocations for known-length
    ///   sessions.  Defaults to 1024 (approx. 17 s at 60 Hz).
    /// </param>
    public ReplayRecorder(uint seed, uint startFrame = 0u, int initialCapacity = 1024)
    {
        if (initialCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                "initialCapacity must be >= 1.");

        _seed       = seed;
        _startFrame = startFrame;
        CurrentFrame = startFrame;
        _frames     = new List<ReplayFrame>(initialCapacity);
    }

    /// <summary>
    /// Records both player inputs for <see cref="CurrentFrame"/> and
    /// advances the frame counter by one.
    /// </summary>
    /// <param name="p1">Player 1 input this frame.</param>
    /// <param name="p2">Player 2 input this frame.</param>
    public void Append(FrameInput p1, FrameInput p2)
    {
        _frames.Add(new ReplayFrame(p1, p2));
        CurrentFrame++;
    }

    /// <summary>
    /// Constructs and returns an immutable <see cref="Replay"/> containing
    /// all frames recorded so far.
    ///
    /// The recorder remains usable after this call; subsequent
    /// <see cref="Append"/> calls will accumulate additional frames that
    /// will appear in the next <see cref="Build"/> result.
    /// </summary>
    public Replay Build() => new Replay(_seed, _startFrame, _frames);
}
