namespace Core.Replay;

/// <summary>
/// Immutable, self-contained record of a single game session.
///
/// ── What a Replay stores ─────────────────────────────────────────────────────
///
/// A <see cref="Replay"/> stores only the minimal information needed to
/// reproduce the exact simulation outcome:
///
///   • <see cref="Seed"/>        — initialises the deterministic PRNG inside
///                                 <c>SimState.CreateInitial</c>.
///   • <see cref="StartFrame"/>  — the frame at which recording began (normally 0).
///   • A flat array of <see cref="ReplayFrame"/> values, one per simulated tick.
///
/// ── Why no GameState snapshots ───────────────────────────────────────────────
///
/// Snapshots would bloat storage, duplicate information that can always be
/// recomputed from the inputs, and introduce a redundant serialisation concern.
/// The entire simulation is deterministic: given the same seed and inputs, the
/// same final <c>SimState</c> is always produced.  Storing mid-run snapshots is
/// only needed for desync diagnostics — a separate, optional concern.
///
/// ── Immutability ─────────────────────────────────────────────────────────────
///
/// The constructor copies the supplied frames into a private array.  Callers
/// cannot mutate <see cref="Frames"/> after construction, ensuring that a
/// <see cref="Replay"/> value is safe to play back multiple times or share
/// across threads.
/// </summary>
public sealed class Replay
{
    private readonly ReplayFrame[] _frames;

    /// <summary>PRNG seed passed to <c>SimState.CreateInitial</c> during playback.</summary>
    public uint Seed { get; }

    /// <summary>
    /// Absolute frame number at which recording started.
    /// <c>0</c> for a full-session replay (the common case).
    /// </summary>
    public uint StartFrame { get; }

    /// <summary>Number of frames stored in <see cref="Frames"/>.</summary>
    public int FrameCount => _frames.Length;

    /// <summary>
    /// Read-only view of the recorded frames.
    /// Element <c>i</c> holds inputs for absolute frame
    /// <c>StartFrame + i</c>.
    /// </summary>
    public IReadOnlyList<ReplayFrame> Frames => _frames;

    /// <summary>
    /// Constructs a <see cref="Replay"/> by copying <paramref name="frames"/>
    /// into an internal array.
    /// </summary>
    /// <param name="seed">PRNG seed (must be &gt; 0 — enforced by <c>SimState.CreateInitial</c>).</param>
    /// <param name="startFrame">First frame index covered by this replay (typically <c>0</c>).</param>
    /// <param name="frames">Inputs to record; copied so the caller may reuse the source collection.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frames"/> is <see langword="null"/>.</exception>
    public Replay(uint seed, uint startFrame, IReadOnlyList<ReplayFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        Seed       = seed;
        StartFrame = startFrame;

        _frames = new ReplayFrame[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            _frames[i] = frames[i];
    }
}
