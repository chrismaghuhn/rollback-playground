using Core.Sim;

namespace Core.Rollback.Tests;

public class InputBufferTests
{
    // ── Shared input shortcuts ────────────────────────────────────────────────

    private static readonly FrameInput Neutral = new(0);
    private static readonly FrameInput Left    = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput Right   = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput Attack  = FrameInput.FromButtons(false, false, false, true);

    // ── 1) Basic set + get ────────────────────────────────────────────────────

    [Fact]
    public void SetThenTryGet_ReturnsExact()
    {
        var buf = new InputBuffer(8);

        buf.Set(5u, Right);

        bool found = buf.TryGet(5u, out FrameInput got);

        Assert.True(found);
        Assert.Equal(Right.Buttons, got.Buttons);
    }

    // ── 2) Wraparound evicts old frames ───────────────────────────────────────

    [Fact]
    public void Wraparound_OverwritesOldFrames()
    {
        // capacity=4 → slots 0-3
        // frames 1..6 written in order; frame 6 maps to slot 2 (6%4=2) and
        // overwrites frame 2 (2%4=2) → TryGet(2) must return false.
        var buf = new InputBuffer(4);

        buf.Set(1u, Right);
        buf.Set(2u, Left);
        buf.Set(3u, Right);
        buf.Set(4u, Left);
        buf.Set(5u, Right);
        buf.Set(6u, Left);

        Assert.False(buf.TryGet(2u, out _));    // evicted by frame 6
        Assert.True (buf.TryGet(6u, out var got));
        Assert.Equal(Left.Buttons, got.Buttons);
    }

    // ── 3) Predict returns neutral when buffer is empty ───────────────────────

    [Fact]
    public void Predict_NoInputs_ReturnsNeutral()
    {
        var buf = new InputBuffer(8);

        FrameInput predicted = buf.GetOrPredict(123u);

        Assert.Equal(Neutral.Buttons, predicted.Buttons);
    }

    // ── 4) Predict repeats the last known input for future frames ─────────────

    [Fact]
    public void Predict_RepeatsLastKnown_ForFutureFrames()
    {
        var buf = new InputBuffer(8);
        buf.Set(10u, Attack);

        // Both frame 11 and a far-future frame should repeat the last input.
        Assert.Equal(Attack.Buttons, buf.GetOrPredict(11u).Buttons);
        Assert.Equal(Attack.Buttons, buf.GetOrPredict(999u).Buttons);
    }

    // ── 5) Predict fills gap with last known input at or before the frame ─────

    [Fact]
    public void Predict_FillsGap_UsesLastKnownAtOrBefore()
    {
        var buf = new InputBuffer(16);
        buf.Set(5u, Left);
        buf.Set(7u, Right);

        // Frame 6 was never set; the nearest known input at or before frame 6
        // is frame 5 (Left).  Frame 7 (Right) is *after* the gap.
        FrameInput predicted = buf.GetOrPredict(6u);

        Assert.Equal(Left.Buttons, predicted.Buttons);
    }

    // ── 6) Out-of-order inserts do not corrupt the latest-known tracking ──────

    [Fact]
    public void OutOfOrderInsert_DoesNotBreakLatest()
    {
        var buf = new InputBuffer(16);

        buf.Set(10u, Right); // latest = frame 10 / Right
        buf.Set(8u,  Left);  // older frame — must NOT become the new latest

        // Future frame → should still repeat frame 10's input (Right).
        Assert.Equal(Right.Buttons, buf.GetOrPredict(11u).Buttons);

        // Gap fill: frame 9 never set; search back → finds frame 8 (Left).
        Assert.Equal(Left.Buttons, buf.GetOrPredict(9u).Buttons);
    }

    // ── 7) Capacity of 1 is rejected ─────────────────────────────────────────

    [Fact]
    public void Ctor_CapacityLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputBuffer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputBuffer(-1));
    }

    // ── 8) Clear resets buffer to empty state ────────────────────────────────

    [Fact]
    public void Clear_ResetsToEmptyState()
    {
        var buf = new InputBuffer(8);
        buf.Set(3u, Attack);

        buf.Clear();

        // After clear: TryGet should return false and prediction should be neutral.
        Assert.False(buf.TryGet(3u, out _));
        Assert.Equal(Neutral.Buttons, buf.GetOrPredict(3u).Buttons);
        Assert.Equal(Neutral.Buttons, buf.GetOrPredict(999u).Buttons);
    }
}
