namespace Core.Sim.Tests;

public class PrngTests
{
    // ── Test 1: Zero-seed guard ──────────────────────────────────────────────

    [Fact]
    public void SeedZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Prng32(0u));
    }

    // ── Test 2: Deterministic sequence (golden values for seed = 1) ─────────
    //
    // XorShift32 recurrence:  x ^= x << 13;  x ^= x >> 17;  x ^= x << 5;
    // Golden sequence (seed = 1):
    //   step 1 →  270 369
    //   step 2 →   67 634 689
    //   step 3 →  2 647 435 461
    //   step 4 →  307 599 695
    //   step 5 →  2 398 689 233

    [Fact]
    public void SameSeed_SameSequence_First5MatchKnownValues()
    {
        uint[] expected = [270_369u, 67_634_689u, 2_647_435_461u, 307_599_695u, 2_398_689_233u];

        // Run A
        var rngA = new Prng32(1u);
        uint[] gotA = [rngA.NextUInt32(), rngA.NextUInt32(), rngA.NextUInt32(),
                       rngA.NextUInt32(), rngA.NextUInt32()];

        // Run B – must produce identical sequence
        var rngB = new Prng32(1u);
        uint[] gotB = [rngB.NextUInt32(), rngB.NextUInt32(), rngB.NextUInt32(),
                       rngB.NextUInt32(), rngB.NextUInt32()];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], gotA[i]);   // golden value check
            Assert.Equal(gotA[i],     gotB[i]);   // reproducibility check
        }
    }

    // ── Test 3: Bounded returns values strictly within [0, upperBound) ──────

    [Fact]
    public void Bounded_ReturnsWithinRange()
    {
        var rng = new Prng32(42u);
        const uint upperBound = 7u;

        for (int i = 0; i < 10_000; i++)
        {
            uint v = rng.NextUInt32Bounded(upperBound);
            Assert.InRange(v, 0u, upperBound - 1u);
        }
    }

    [Fact]
    public void Bounded_ZeroUpperBound_Throws()
    {
        var rng = new Prng32(1u);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextUInt32Bounded(0u));
    }

    // ── Test 4: NextInt32 handles negative min and respects [min, max) ───────

    [Fact]
    public void NextInt32_InRangeAndHandlesNegativeMin()
    {
        var rng = new Prng32(99u);
        const int min = -3;
        const int max =  4;

        for (int i = 0; i < 10_000; i++)
        {
            int v = rng.NextInt32(min, max);
            Assert.InRange(v, min, max - 1);   // [min, max)
        }
    }

    [Fact]
    public void NextInt32_EqualMinMax_Throws()
    {
        var rng = new Prng32(1u);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt32(5, 5));
    }

    [Fact]
    public void NextInt32_MaxLessThanMin_Throws()
    {
        var rng = new Prng32(1u);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt32(10, 5));
    }
}
