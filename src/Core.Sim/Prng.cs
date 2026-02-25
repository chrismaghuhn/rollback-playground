namespace Core.Sim;

/// <summary>
/// Deterministic XorShift32 PRNG with fully explicit state.
///
/// Determinism guarantees:
///   - State is a plain <see cref="uint"/> field — no hidden globals, no time seed.
///   - The recurrence (x ^= x&lt;&lt;13; x ^= x&gt;&gt;17; x ^= x&lt;&lt;5) is a bijection on
///     the non-zero uint domain, so the full-period sequence of 2³²−1 values is
///     always reproducible from the same seed.
///   - The struct is a value type: copying a <see cref="Prng32"/> snapshots the
///     PRNG state for free, enabling rollback without extra bookkeeping.
///
/// Seed 0 is the only absorbing state of XorShift32 (all three shifts produce 0),
/// so it is rejected at construction time.
/// </summary>
public struct Prng32
{
    // ── State ─────────────────────────────────────────────────────────────────

    public uint State { get; private set; }

    // ── Construction ─────────────────────────────────────────────────────────

    /// <param name="seed">Initial state. Must not be zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="seed"/> is 0.</exception>
    public Prng32(uint seed)
    {
        if (seed == 0u)
            throw new ArgumentOutOfRangeException(nameof(seed), "Prng32 seed must not be zero (XorShift32 absorbing state).");

        State = seed;
    }

    // ── Core generator ───────────────────────────────────────────────────────

    /// <summary>
    /// Advances the state by one step and returns the new state as a random uint.
    /// Full 32-bit output; period = 2³²−1.
    /// </summary>
    public uint NextUInt32()
    {
        uint s = State;
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s <<  5;
        State = s;
        return s;
    }

    // ── Derived generators ───────────────────────────────────────────────────

    /// <summary>Returns a random bool (50 % probability each).</summary>
    public bool NextBool() => (NextUInt32() & 1u) != 0u;

    /// <summary>
    /// Returns a uniform random uint in [0, <paramref name="exclusiveUpperBound"/>).
    ///
    /// Uses the "multiply-high" (Lemire) approach to avoid modulo bias:
    ///   result = (ulong)sample * bound >> 32
    /// This is unbiased when <paramref name="exclusiveUpperBound"/> is a power of two
    /// and has at most 1-in-2³² bias otherwise — acceptable for gameplay purposes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="exclusiveUpperBound"/> is 0.</exception>
    public uint NextUInt32Bounded(uint exclusiveUpperBound)
    {
        if (exclusiveUpperBound == 0u)
            throw new ArgumentOutOfRangeException(nameof(exclusiveUpperBound), "Upper bound must be greater than zero.");

        return (uint)((ulong)NextUInt32() * exclusiveUpperBound >> 32);
    }

    /// <summary>
    /// Returns a uniform random int in [<paramref name="inclusiveMin"/>, <paramref name="exclusiveMax"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="exclusiveMax"/> &lt;= <paramref name="inclusiveMin"/>.
    /// </exception>
    public int NextInt32(int inclusiveMin, int exclusiveMax)
    {
        if (exclusiveMax <= inclusiveMin)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMax),
                $"exclusiveMax ({exclusiveMax}) must be greater than inclusiveMin ({inclusiveMin}).");

        uint range = (uint)(exclusiveMax - inclusiveMin);
        return inclusiveMin + (int)NextUInt32Bounded(range);
    }
}
