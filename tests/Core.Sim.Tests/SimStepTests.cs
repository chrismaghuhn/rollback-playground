namespace Core.Sim.Tests;

public class SimStepTests
{
    // ── Shared helpers ───────────────────────────────────────────────────────

    private static readonly FrameInput NoInput     = new(0);
    private static readonly FrameInput PressRight  = FrameInput.FromButtons(false, true,  false, false);
    private static readonly FrameInput PressLeft   = FrameInput.FromButtons(true,  false, false, false);
    private static readonly FrameInput PressJump   = FrameInput.FromButtons(false, false, true,  false);
    private static readonly FrameInput PressAttack = FrameInput.FromButtons(false, false, false, true);

    /// <summary>
    /// Builds a SimState with fully-configurable P1 fields; P2 defaults to a
    /// stationary opponent on the right side of the arena.
    /// </summary>
    private static SimState MakeState(
        int         p1X             = SimConstants.P1StartX,
        int         p2X             = SimConstants.P2StartX,
        int         p1Y             = SimConstants.GroundY,
        int         p1Vy            = 0,
        ActionState p1State         = ActionState.Idle,
        sbyte       p1Facing        = 1,
        int         p1HitstunFrames = 0,
        int         p1AttackCooldown= 0,
        int         p1Hp            = SimConstants.DefaultHp,
        int         p2Hp            = SimConstants.DefaultHp)
    {
        // Start from a valid seed-1 initial state and replace the player fields
        // we care about so every field is properly initialised.
        SimState s = SimState.CreateInitial(1u);

        s.P1 = new PlayerState
        {
            X                    = p1X,
            Y                    = p1Y,
            Vx                   = 0,
            Vy                   = p1Vy,
            Facing               = p1Facing,
            State                = p1State,
            HitstunFrames        = p1HitstunFrames,
            Hp                   = p1Hp,
            AttackCooldownFrames = p1AttackCooldown,
            AttackActiveFrames   = 0,
            AttackHasHit         = 0,
        };

        s.P2 = new PlayerState
        {
            X                    = p2X,
            Y                    = SimConstants.GroundY,
            Vx                   = 0,
            Vy                   = 0,
            Facing               = -1,
            State                = ActionState.Idle,
            HitstunFrames        = 0,
            Hp                   = p2Hp,
            AttackCooldownFrames = 0,
            AttackActiveFrames   = 0,
            AttackHasHit         = 0,
        };

        return s;
    }

    // ── Frame counter ────────────────────────────────────────────────────────

    [Fact]
    public void Step_IncrementsFrameCounter()
    {
        SimState s    = MakeState();
        SimState next = SimStep.Step(in s, NoInput, NoInput);

        Assert.Equal(1u, next.Frame);
    }

    // ── Movement ─────────────────────────────────────────────────────────────

    [Fact]
    public void Movement_Right_IncreasesX()
    {
        SimState s    = MakeState(p1X: 5_000);
        SimState next = SimStep.Step(in s, PressRight, NoInput);

        Assert.Equal(5_000 + SimConstants.MoveSpeedPerTick, next.P1.X);
    }

    [Fact]
    public void Movement_Left_DecreasesX()
    {
        SimState s    = MakeState(p1X: 5_000);
        SimState next = SimStep.Step(in s, PressLeft, NoInput);

        Assert.Equal(5_000 - SimConstants.MoveSpeedPerTick, next.P1.X);
    }

    // ── Wall clamping ─────────────────────────────────────────────────────────

    [Fact]
    public void WallClamp_DoesNotExceedMaxX()
    {
        // One rightward step from this position would overshoot the right boundary.
        int nearRight = SimConstants.MaxX - SimConstants.PlayerWidth
                      - SimConstants.MoveSpeedPerTick + 1;
        SimState s    = MakeState(p1X: nearRight);
        SimState next = SimStep.Step(in s, PressRight, NoInput);

        Assert.Equal(SimConstants.MaxX - SimConstants.PlayerWidth, next.P1.X);
    }

    [Fact]
    public void WallClamp_DoesNotGoBelowMinX()
    {
        // One leftward step from this position would produce a negative X.
        int nearLeft  = SimConstants.MoveSpeedPerTick - 1;
        SimState s    = MakeState(p1X: nearLeft);
        SimState next = SimStep.Step(in s, PressLeft, NoInput);

        Assert.Equal(SimConstants.MinX, next.P1.X);
    }

    // ── Jump and gravity ──────────────────────────────────────────────────────

    [Fact]
    public void Jump_FromGround_SetsUpwardVelocityAndActionState()
    {
        SimState s    = MakeState(p1Y: SimConstants.GroundY);
        SimState next = SimStep.Step(in s, PressJump, NoInput);

        // Gravity is applied to Vy in the same tick as the jump, then integrated:
        //   Vy = JumpVelocityPerTick + GravityPerTick = 500 + (−40) = 460
        //   Y  = 0 + 460 = 460
        int expectedVy = SimConstants.JumpVelocityPerTick + SimConstants.GravityPerTick;
        Assert.Equal(ActionState.Jump, next.P1.State);
        Assert.Equal(expectedVy,       next.P1.Vy);
        Assert.Equal(expectedVy,       next.P1.Y);
    }

    [Fact]
    public void Gravity_PullsPlayerToGround_WhenAirborne()
    {
        SimState s    = MakeState(p1Y: 1_000, p1Vy: 100, p1State: ActionState.Jump);
        SimState next = SimStep.Step(in s, NoInput, NoInput);

        // Gravity is applied first, then position integrated:
        //   Vy_new = 100 + GravityPerTick = 100 + (−40) = 60
        //   Y_new  = 1000 + 60 = 1060
        int expectedVy = 100 + SimConstants.GravityPerTick;
        Assert.Equal(expectedVy,         next.P1.Vy);
        Assert.Equal(1_000 + expectedVy, next.P1.Y);
    }

    [Fact]
    public void Player_LandsOnGround_AndTransitionsToIdle()
    {
        // Vy is negative enough that Y would drop below ground in one tick.
        //   Vy_new = −50 + GravityPerTick = −90
        //   Y_new  = 10 + (−90) = −80  →  clamped to 0
        SimState s    = MakeState(p1Y: 10, p1Vy: -50, p1State: ActionState.Jump);
        SimState next = SimStep.Step(in s, NoInput, NoInput);

        Assert.Equal(0,                next.P1.Y);
        Assert.Equal(0,                next.P1.Vy);
        Assert.Equal(ActionState.Idle, next.P1.State);
    }

    // ── Attack ────────────────────────────────────────────────────────────────

    [Fact]
    public void Attack_Hits_WhenInRange_AppliesDamageAndHitstun()
    {
        // P1 at X=8_000, facing right  →  hitbox  X ∈ [8600, 9300]
        // P2 at X=8_800                →  hurtbox X ∈ [8800, 9400]
        // X-overlap: max(8600,8800)=8800 < min(9300,9400)=9300  ← overlaps
        SimState s    = MakeState(p1X: 8_000, p2X: 8_800, p1AttackCooldown: 0);
        SimState next = SimStep.Step(in s, PressAttack, NoInput);

        Assert.Equal(SimConstants.DefaultHp - SimConstants.AttackDamage, next.P2.Hp);
        Assert.Equal(SimConstants.HitstunFrames,                         next.P2.HitstunFrames);
        Assert.Equal(ActionState.Hitstun,                                next.P2.State);
        Assert.Equal((byte)1,                                            next.P1.AttackHasHit);
    }

    [Fact]
    public void Attack_DoesNotHit_WhenOutOfRange()
    {
        // P1 at X=4_000, facing right  →  hitbox  X ∈ [4600, 5300]
        // P2 at X=15_000               →  hurtbox X ∈ [15000, 15600]
        // No X-overlap: 5300 < 15000
        SimState s    = MakeState(p1X: 4_000, p2X: 15_000, p1AttackCooldown: 0);
        SimState next = SimStep.Step(in s, PressAttack, NoInput);

        Assert.Equal(SimConstants.DefaultHp, next.P2.Hp);
        Assert.Equal(0,                      next.P2.HitstunFrames);
    }

    [Fact]
    public void Attack_OnCooldown_CannotAttack()
    {
        SimState s    = MakeState(p1AttackCooldown: 5);
        SimState next = SimStep.Step(in s, PressAttack, NoInput);

        // TickBegin decrements 5 → 4; still non-zero so attack is blocked.
        Assert.NotEqual(ActionState.Attack, next.P1.State);
        Assert.Equal(4,                     next.P1.AttackCooldownFrames);
    }

    // ── Hitstun ───────────────────────────────────────────────────────────────

    [Fact]
    public void Hitstun_PreventsMovement()
    {
        SimState s    = MakeState(p1X: 5_000,
                                  p1State: ActionState.Hitstun,
                                  p1HitstunFrames: 10);
        SimState next = SimStep.Step(in s, PressRight, NoInput);

        Assert.Equal(5_000, next.P1.X);           // position must not change
        Assert.Equal(9,     next.P1.HitstunFrames); // hitstun counted down
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void Determinism_SameInputsSameFinalState_300Ticks()
    {
        SimState a = SimState.CreateInitial(42u);
        SimState b = SimState.CreateInitial(42u);

        // Apply the same non-trivial inputs on every tick.
        FrameInput p1In = PressRight;
        FrameInput p2In = PressLeft;

        for (int i = 0; i < 300; i++)
        {
            a = SimStep.Step(in a, p1In, p2In);
            b = SimStep.Step(in b, p1In, p2In);
        }

        // Every field must be identical — value-type copy semantics guarantee this
        // only if the step function is fully deterministic.
        Assert.Equal(a.Frame,     b.Frame);
        Assert.Equal(a.P1.X,      b.P1.X);
        Assert.Equal(a.P1.Y,      b.P1.Y);
        Assert.Equal(a.P1.Hp,     b.P1.Hp);
        Assert.Equal(a.P2.X,      b.P2.X);
        Assert.Equal(a.P2.Y,      b.P2.Y);
        Assert.Equal(a.P2.Hp,     b.P2.Hp);
        Assert.Equal(a.Rng.State, b.Rng.State);
    }
}
