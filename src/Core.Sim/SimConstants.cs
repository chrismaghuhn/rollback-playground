namespace Core.Sim;

/// <summary>
/// Every magic number in one place. No floats, no doubles — all values are
/// expressed as integers using the fixed-point convention described below.
///
/// ── Fixed-point convention ──────────────────────────────────────────────────
///
///   FixedScale = 1 000
///
///   1 fixed-point unit  =  1 / FixedScale  world unit
///                       =  1 / 1 000  world unit
///                       =  0.001 world unit
///
///   Example: a position of X = 5 000  means  5.0 world units.
///            a velocity  of Vx= 100   means  0.1 world units per tick.
///
///   Integer arithmetic with FixedScale avoids all floating-point, while still
///   providing sub-pixel-level precision (1 mm resolution at "1 unit = 1 metre").
///   Overflow check: MaxX = 20 000; MaxVx ≈ 600; max representable int ≈ 2 × 10⁹.
///   Running 60 ticks/s for one hour: 60 × 3 600 × 600 = 129 600 000 — well within
///   int range.
///
/// See docs/ASSUMPTIONS.md for rationale.
/// </summary>
public static class SimConstants
{
    // ── Timing ───────────────────────────────────────────────────────────────

    /// <summary>Simulation steps per real second (fixed, never read from clock).</summary>
    public const int TicksPerSecond = 60;

    // ── Fixed-point scale ────────────────────────────────────────────────────

    /// <summary>
    /// 1 world unit = FixedScale fixed-point units.
    /// All positions, velocities, and distances are stored multiplied by this value.
    /// </summary>
    public const int FixedScale = 1_000;

    // ── Arena bounds (in fixed-point units) ──────────────────────────────────

    /// <summary>Left wall.  0 world units.</summary>
    public const int MinX = 0;

    /// <summary>Right wall. 20 world units.</summary>
    public const int MaxX = 20_000;

    /// <summary>Floor. 0 world units.</summary>
    public const int GroundY = 0;

    /// <summary>Ceiling. 12 world units.</summary>
    public const int MaxY = 12_000;

    // ── Player defaults ───────────────────────────────────────────────────────

    public const int DefaultHp                   = 100;
    public const int DefaultAttackCooldownFrames  = 30;   // 0.5 s at 60 fps
    public const byte DefaultAttackActiveFrames   = 5;    // active hitbox window

    // ── Start positions (symmetric around centre X = 10 000) ─────────────────

    /// <summary>P1 spawn X.  4 world units from the left wall.</summary>
    public const int P1StartX = 4_000;

    /// <summary>P2 spawn X.  4 world units from the right wall (16 of 20).</summary>
    public const int P2StartX = 16_000;

    /// <summary>Both players spawn on the floor.</summary>
    public const int StartY = GroundY;
}
