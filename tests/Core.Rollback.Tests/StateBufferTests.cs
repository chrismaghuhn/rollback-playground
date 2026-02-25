using Core.Sim;

namespace Core.Rollback.Tests;

public class StateBufferTests
{
    // ── 1) Save + load returns an exact field-for-field copy ──────────────────

    [Fact]
    public void SaveThenTryLoad_ReturnsExactCopy()
    {
        var buf   = new StateBuffer(8);
        var state = SimState.CreateInitial(1u);

        buf.Save(10u, in state);

        bool found = buf.TryLoad(10u, out SimState loaded);

        Assert.True(found);
        Assert.Equal(state.Frame,    loaded.Frame);
        Assert.Equal(state.P1.X,     loaded.P1.X);
        Assert.Equal(state.P1.Hp,    loaded.P1.Hp);
        Assert.Equal(state.P2.X,     loaded.P2.X);
        Assert.Equal(state.Rng.State, loaded.Rng.State);
    }

    [Fact]
    public void TryLoad_ReturnsValueCopy_ModifyingLoadedDoesNotAffectSnapshot()
    {
        // This verifies that SimState is stored and returned by value, not by
        // reference.  If the buffer accidentally exposed a reference to its
        // internal array element, mutating 'loaded' would corrupt the snapshot.
        var buf   = new StateBuffer(8);
        var state = SimState.CreateInitial(1u);
        int originalX = state.P1.X;

        buf.Save(10u, in state);

        buf.TryLoad(10u, out SimState loaded);
        loaded.P1.X += 999; // mutate the local copy

        // Re-load from buffer — snapshot must be unchanged.
        buf.TryLoad(10u, out SimState reload);
        Assert.Equal(originalX, reload.P1.X);
    }

    // ── 2) Wraparound evicts old snapshots ────────────────────────────────────

    [Fact]
    public void Wraparound_OverwritesOldFrames()
    {
        // capacity=4 → slots 0-3
        // Frame 6 maps to slot 2 (6%4=2) and evicts frame 2 (2%4=2).
        var buf = new StateBuffer(4);

        for (uint f = 1; f <= 6; f++)
            buf.Save(f, SimState.CreateInitial(1u));

        Assert.False(buf.TryLoad(2u, out _)); // evicted
        Assert.True (buf.TryLoad(6u, out _)); // present
    }

    // ── 3) Clear removes all snapshots ───────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllSnapshots()
    {
        var buf   = new StateBuffer(8);
        var state = SimState.CreateInitial(1u);

        buf.Save(5u, in state);
        buf.Clear();

        Assert.False(buf.TryLoad(5u, out _));
    }

    // ── 4) LatestFrame tracks the highest frame ever saved ───────────────────

    [Fact]
    public void LatestFrame_TracksNewest()
    {
        var buf = new StateBuffer(8);
        var s   = SimState.CreateInitial(1u);

        // Fresh buffer has no latest.
        Assert.Null(buf.LatestFrame);

        buf.Save(10u, in s);
        buf.Save( 8u, in s); // older frame — must NOT overwrite latest
        Assert.Equal(10u, buf.LatestFrame);

        buf.Save(12u, in s); // newer frame — should update latest
        Assert.Equal(12u, buf.LatestFrame);
    }

    [Fact]
    public void LatestFrame_IsNullAfterClear()
    {
        var buf = new StateBuffer(8);
        var s   = SimState.CreateInitial(1u);

        buf.Save(7u, in s);
        buf.Clear();

        Assert.Null(buf.LatestFrame);
    }

    // ── 5) Frame 0 is stored correctly and does not collide with sentinel ────

    [Fact]
    public void FrameZero_WorksAndDoesNotCollideWithSentinel()
    {
        // uint.MaxValue is the sentinel for "empty slot".
        // Saving frame 0 must not be confused with the sentinel value.
        var buf    = new StateBuffer(8);
        var state0 = SimState.CreateInitial(1u);

        buf.Save(0u, in state0);

        Assert.True (buf.TryLoad(0u, out _)); // frame 0 must be found
        Assert.False(buf.TryLoad(1u, out _)); // frame 1 was never saved
    }

    // ── 6) Capacity < 2 is rejected ─────────────────────────────────────────

    [Fact]
    public void Ctor_CapacityLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateBuffer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateBuffer(-5));
    }
}
