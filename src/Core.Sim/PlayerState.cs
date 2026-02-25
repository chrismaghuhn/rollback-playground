namespace Core.Sim;

/// <summary>
/// All per-player simulation state, stored as a flat struct.
///
/// Mutable by design: <see cref="SimStep"/> modifies a local copy then returns it,
/// and <see cref="Core.Rollback.StateBuffer"/> snapshots the entire <see cref="SimState"/>
/// (which contains two <see cref="PlayerState"/> values inline) on every frame.
///
/// No heap allocations, no arrays, no collections — safe to copy at 60 fps.
/// </summary>
public struct PlayerState
{
    // ── Position and velocity (fixed-point, see SimConstants.FixedScale) ─────

    /// <summary>Horizontal position. 0 = left wall, MaxX = right wall.</summary>
    public int X;

    /// <summary>Vertical position. 0 = floor (GroundY), positive = up.</summary>
    public int Y;

    /// <summary>Horizontal velocity in fixed-point units per tick.</summary>
    public int Vx;

    /// <summary>Vertical velocity in fixed-point units per tick.</summary>
    public int Vy;

    // ── Orientation ───────────────────────────────────────────────────────────

    /// <summary>+1 = facing right, −1 = facing left.</summary>
    public sbyte Facing;

    // ── Logic state ───────────────────────────────────────────────────────────

    /// <summary>Current animation/logic state (Idle, Run, Jump, Attack, Hitstun).</summary>
    public ActionState State;

    /// <summary>Frames remaining in hitstun. 0 = not in hitstun.</summary>
    public int HitstunFrames;

    // ── Combat ────────────────────────────────────────────────────────────────

    /// <summary>Hit points. 0 = KO.</summary>
    public int Hp;

    /// <summary>Frames until next attack is allowed. 0 = can attack now.</summary>
    public int AttackCooldownFrames;

    /// <summary>
    /// Frames remaining in the active hitbox window of an ongoing attack.
    /// 0 = no active hitbox.
    /// </summary>
    public int AttackActiveFrames;

    /// <summary>
    /// Set to 1 when this attack has already landed a hit; prevents multi-hit within
    /// a single attack window. Reset to 0 when a new attack starts.
    /// </summary>
    public byte AttackHasHit;
}
