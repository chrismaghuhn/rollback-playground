namespace Core.Sim;

/// <summary>
/// The complete, deterministic state of one simulation tick.
///
/// Value-type struct: copying a <see cref="SimState"/> produces a fully independent
/// snapshot — no reference sharing. This is the foundation of the rollback engine:
/// <see cref="Core.Rollback.StateBuffer"/> stores these snapshots inline.
///
/// All fields are value types (int, uint, sbyte, byte, enums, nested structs).
/// No arrays, no collections, no heap objects.
/// </summary>
public struct SimState
{
    // ── Timeline ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Zero-based tick counter. Incremented by <c>SimStep.Step</c> each frame.
    /// uint allows ~828 days at 60 fps before wrapping (irrelevant for MVP, but
    /// uint is semantically correct for a monotonic counter).
    /// </summary>
    public uint Frame;

    // ── Players ───────────────────────────────────────────────────────────────

    /// <summary>Player 1 state. Starts on the left, faces right.</summary>
    public PlayerState P1;

    /// <summary>Player 2 state. Starts on the right, faces left.</summary>
    public PlayerState P2;

    // ── Randomness ────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic PRNG. Stored inside <see cref="SimState"/> so it is
    /// automatically saved/restored by the rollback engine with zero extra code.
    /// </summary>
    public Prng32 Rng;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the canonical starting state for a new match.
    /// </summary>
    /// <param name="seed">
    /// PRNG seed. Must not be zero (XorShift32 absorbing state).
    /// Different seeds produce different random sequences but identical
    /// deterministic starting positions.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="seed"/> is 0.</exception>
    public static SimState CreateInitial(uint seed)
    {
        if (seed == 0u)
            throw new ArgumentOutOfRangeException(nameof(seed),
                "SimState seed must not be zero (Prng32 absorbing state).");

        return new SimState
        {
            Frame = 0u,
            Rng   = new Prng32(seed),

            P1 = new PlayerState
            {
                X                    = SimConstants.P1StartX,
                Y                    = SimConstants.StartY,
                Vx                   = 0,
                Vy                   = 0,
                Facing               = 1,           // faces right →
                State                = ActionState.Idle,
                HitstunFrames        = 0,
                Hp                   = SimConstants.DefaultHp,
                AttackCooldownFrames = 0,
                AttackActiveFrames   = 0,
            },

            P2 = new PlayerState
            {
                X                    = SimConstants.P2StartX,
                Y                    = SimConstants.StartY,
                Vx                   = 0,
                Vy                   = 0,
                Facing               = -1,          // faces left ←
                State                = ActionState.Idle,
                HitstunFrames        = 0,
                Hp                   = SimConstants.DefaultHp,
                AttackCooldownFrames = 0,
                AttackActiveFrames   = 0,
            },
        };
    }
}
