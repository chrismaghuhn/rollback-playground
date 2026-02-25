using Core.Sim;

namespace Core.Replay;

/// <summary>
/// Holds the two player inputs for a single simulated frame.
///
/// ── Why no explicit FrameIndex field ────────────────────────────────────────
///
/// A frame's absolute index can always be computed as
///     <c>Replay.StartFrame + i</c>
/// where <c>i</c> is the zero-based position of this <see cref="ReplayFrame"/>
/// inside <c>Replay.Frames</c>.
/// Storing a redundant <c>FrameIndex</c> per entry would waste 4 bytes per
/// frame and introduce an invariant that callers must maintain.  The current
/// design keeps the struct as small as possible.
///
/// ── Why a struct, not a class ────────────────────────────────────────────────
///
/// <see cref="ReplayFrame"/> is a pure value: two immutable <see cref="FrameInput"/>
/// flags bytes.  A struct avoids GC pressure for large (thousands-of-frame)
/// replays and is cheaper to copy than a heap-allocated object.
/// </summary>
public readonly struct ReplayFrame
{
    /// <summary>Player 1 input for this frame.</summary>
    public FrameInput P1 { get; }

    /// <summary>Player 2 input for this frame.</summary>
    public FrameInput P2 { get; }

    /// <summary>Initialises a frame with both player inputs.</summary>
    public ReplayFrame(FrameInput p1, FrameInput p2)
    {
        P1 = p1;
        P2 = p2;
    }
}
