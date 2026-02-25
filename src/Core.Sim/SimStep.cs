namespace Core.Sim;

/// <summary>
/// Pure, stateless simulation step.
///
/// Takes the previous <see cref="SimState"/> and this frame's player inputs and
/// returns the next <see cref="SimState"/>. No heap allocation, no side effects,
/// no floating-point arithmetic.
///
/// Algorithm (executed in this order every tick):
///   A) Increment frame counter.
///   B) TickBegin  — decrement cooldowns and hitstun counters.
///   C) HandleAttackStart — start a new attack if the button is pressed and eligible.
///   D) Movement / Jump   — apply directional input; update facing; initiate jumps.
///   E) Gravity + Integrate — apply gravity to Vy, then add Vy to Y; clamp to ground.
///   F) AttackCountdown   — decrement active-hitbox window; end attack state when done.
///   G) Simultaneous hit resolution — compute both attack interactions before applying
///      either, so neither player has an ordering advantage.
/// </summary>
public static class SimStep
{
    /// <summary>
    /// Advance the simulation by one tick.
    /// </summary>
    /// <param name="prev">Previous state (passed by readonly reference to avoid copying).</param>
    /// <param name="p1Input">P1's input for this tick.</param>
    /// <param name="p2Input">P2's input for this tick.</param>
    /// <returns>The new state after applying all game logic.</returns>
    public static SimState Step(in SimState prev, FrameInput p1Input, FrameInput p2Input)
    {
        // Copy into a local mutable state. All modifications happen here; the
        // caller's snapshot is never touched.
        SimState s = prev;

        // ── A) Frame counter ──────────────────────────────────────────────────
        s.Frame++;

        // ── B) TickBegin — decrement cooldowns and hitstun ───────────────────
        s.P1.AttackCooldownFrames = Decrement(s.P1.AttackCooldownFrames);
        s.P2.AttackCooldownFrames = Decrement(s.P2.AttackCooldownFrames);

        DecrementHitstun(ref s.P1);
        DecrementHitstun(ref s.P2);

        // ── C) HandleAttackStart ─────────────────────────────────────────────
        TryStartAttack(ref s.P1, p1Input);
        TryStartAttack(ref s.P2, p2Input);

        // ── D) Movement and jump ─────────────────────────────────────────────
        ApplyMovement(ref s.P1, p1Input);
        ApplyMovement(ref s.P2, p2Input);

        // ── E) Gravity + integrate ───────────────────────────────────────────
        ApplyGravityAndIntegrate(ref s.P1);
        ApplyGravityAndIntegrate(ref s.P2);

        // ── F) AttackCountdown — tick the active-hitbox window ───────────────
        TickAttackWindow(ref s.P1);
        TickAttackWindow(ref s.P2);

        // ── G) Simultaneous hit resolution ───────────────────────────────────
        // Evaluate both directions BEFORE applying either result, so both
        // attacks land (or miss) based on the same pre-application state.
        bool p1HitsP2 = CanHit(s.P1, s.P2);
        bool p2HitsP1 = CanHit(s.P2, s.P1);

        if (p1HitsP2) ApplyHit(ref s.P1, ref s.P2);
        if (p2HitsP1) ApplyHit(ref s.P2, ref s.P1);

        return s;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Decrements a non-negative counter, clamping at zero.</summary>
    private static int Decrement(int value) => value > 0 ? value - 1 : 0;

    /// <summary>
    /// Ticks hitstun down by one. Transitions the player back to <see cref="ActionState.Idle"/>
    /// when the counter reaches zero.
    /// </summary>
    private static void DecrementHitstun(ref PlayerState p)
    {
        if (p.HitstunFrames <= 0) return;
        p.HitstunFrames--;
        if (p.HitstunFrames == 0)
            p.State = ActionState.Idle;
    }

    /// <summary>
    /// Starts a new attack if: the player is not in hitstun, the attack button is
    /// pressed, and the attack cooldown has expired.
    /// </summary>
    private static void TryStartAttack(ref PlayerState p, FrameInput input)
    {
        if (p.State == ActionState.Hitstun) return;
        if (!input.AttackPressed)           return;
        if (p.AttackCooldownFrames > 0)     return;

        p.State              = ActionState.Attack;
        p.AttackActiveFrames = SimConstants.AttackActiveFrames;
        p.AttackCooldownFrames = SimConstants.AttackCooldownFrames;
        p.AttackHasHit       = 0;
    }

    /// <summary>
    /// Applies directional movement and jump input.
    /// Hitstun completely suppresses all movement.
    /// Wall-clamping is applied at the end of this method.
    /// </summary>
    private static void ApplyMovement(ref PlayerState p, FrameInput input)
    {
        if (p.State == ActionState.Hitstun) return;

        // Horizontal movement
        if (input.RightPressed)
        {
            p.X     += SimConstants.MoveSpeedPerTick;
            p.Facing = 1;
            if (p.State != ActionState.Jump && p.State != ActionState.Attack)
                p.State = ActionState.Run;
        }
        else if (input.LeftPressed)
        {
            p.X     -= SimConstants.MoveSpeedPerTick;
            p.Facing = -1;
            if (p.State != ActionState.Jump && p.State != ActionState.Attack)
                p.State = ActionState.Run;
        }
        else
        {
            // No horizontal input: transition Run → Idle (other states are unchanged)
            if (p.State == ActionState.Run)
                p.State = ActionState.Idle;
        }

        // Jump — only from ground and not already jumping
        if (input.JumpPressed
            && p.Y   == SimConstants.GroundY
            && p.State != ActionState.Jump)
        {
            p.Vy    = SimConstants.JumpVelocityPerTick;
            p.State = ActionState.Jump;
        }

        // Clamp X to arena bounds (player left-edge in [MinX, MaxX − PlayerWidth])
        p.X = Clamp(p.X, SimConstants.MinX, SimConstants.MaxX - SimConstants.PlayerWidth);
    }

    /// <summary>
    /// Applies gravity each tick and integrates velocity into position.
    /// Clamps Y to the ground: landing resets Vy and transitions Jump → Idle.
    /// </summary>
    private static void ApplyGravityAndIntegrate(ref PlayerState p)
    {
        p.Vy += SimConstants.GravityPerTick;
        p.Y  += p.Vy;

        if (p.Y <= SimConstants.GroundY)
        {
            p.Y  = SimConstants.GroundY;
            p.Vy = 0;
            if (p.State == ActionState.Jump)
                p.State = ActionState.Idle;
        }
    }

    /// <summary>
    /// Decrements the active-hitbox window by one tick.
    /// When it reaches zero the attack state ends (transitions to Idle).
    /// </summary>
    private static void TickAttackWindow(ref PlayerState p)
    {
        if (p.AttackActiveFrames <= 0) return;
        p.AttackActiveFrames--;
        if (p.AttackActiveFrames == 0 && p.State == ActionState.Attack)
            p.State = ActionState.Idle;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="attacker"/>'s current hitbox
    /// overlaps <paramref name="defender"/>'s hurtbox and the attack has not already
    /// landed this swing.
    ///
    /// Hitbox placement (AABB, fixed-point):
    ///   facing right:  X ∈ [attacker.X + PlayerWidth,  attacker.X + PlayerWidth + AttackHitboxWidth]
    ///   facing left:   X ∈ [attacker.X − AttackHitboxWidth, attacker.X]
    ///   Y (both):      Y ∈ [attacker.Y, attacker.Y + AttackHitboxHeight]
    ///
    /// Defender hurtbox:
    ///   X ∈ [defender.X, defender.X + PlayerWidth]
    ///   Y ∈ [defender.Y, defender.Y + PlayerHeight]
    /// </summary>
    private static bool CanHit(PlayerState attacker, PlayerState defender)
    {
        if (attacker.AttackActiveFrames <= 0) return false;
        if (attacker.AttackHasHit       != 0) return false;

        // Attacker hitbox
        int hitLeft, hitRight;
        if (attacker.Facing == 1)   // facing right →
        {
            hitLeft  = attacker.X + SimConstants.PlayerWidth;
            hitRight = hitLeft    + SimConstants.AttackHitboxWidth;
        }
        else                         // facing left ←
        {
            hitRight = attacker.X;
            hitLeft  = hitRight - SimConstants.AttackHitboxWidth;
        }
        int hitBottom = attacker.Y;
        int hitTop    = attacker.Y + SimConstants.AttackHitboxHeight;

        // Defender hurtbox
        int hurtLeft   = defender.X;
        int hurtRight  = defender.X + SimConstants.PlayerWidth;
        int hurtBottom = defender.Y;
        int hurtTop    = defender.Y + SimConstants.PlayerHeight;

        // AABB intersection — open intervals so touching edges do NOT count as a hit
        bool xOverlap = hitLeft  < hurtRight && hurtLeft  < hitRight;
        bool yOverlap = hitBottom < hurtTop  && hurtBottom < hitTop;

        return xOverlap && yOverlap;
    }

    /// <summary>
    /// Applies a confirmed hit: marks the attacker's swing as spent and inflicts
    /// damage + hitstun on the defender.
    /// </summary>
    private static void ApplyHit(ref PlayerState attacker, ref PlayerState defender)
    {
        attacker.AttackHasHit  = 1;
        defender.Hp            = Clamp(defender.Hp - SimConstants.AttackDamage, 0, int.MaxValue);
        defender.HitstunFrames = SimConstants.HitstunFrames;
        defender.State         = ActionState.Hitstun;
    }

    /// <summary>Integer clamp (avoids a System.Math dependency in the hot path).</summary>
    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
