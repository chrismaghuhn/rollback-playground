using System.Reflection;

namespace Core.Sim.Tests;

public class SimTypesTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // FrameInput
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FrameInput_Flags_Work()
    {
        // Individual directions
        var left   = FrameInput.FromButtons(left: true,  right: false, jump: false, attack: false);
        var right  = FrameInput.FromButtons(left: false, right: true,  jump: false, attack: false);
        var jump   = FrameInput.FromButtons(left: false, right: false, jump: true,  attack: false);
        var attack = FrameInput.FromButtons(left: false, right: false, jump: false, attack: true);

        Assert.True(left.LeftPressed);
        Assert.False(left.RightPressed);
        Assert.False(left.JumpPressed);
        Assert.False(left.AttackPressed);

        Assert.True(right.RightPressed);
        Assert.False(right.LeftPressed);

        Assert.True(jump.JumpPressed);
        Assert.True(attack.AttackPressed);

        // Combined flags – both Left+Right simultaneously
        var leftRight = FrameInput.FromButtons(left: true, right: true, jump: false, attack: false);
        Assert.True(leftRight.LeftPressed);
        Assert.True(leftRight.RightPressed);
        Assert.False(leftRight.JumpPressed);

        // No buttons pressed
        var none = new FrameInput(0);
        Assert.False(none.LeftPressed);
        Assert.False(none.RightPressed);
        Assert.False(none.JumpPressed);
        Assert.False(none.AttackPressed);
    }

    [Fact]
    public void FrameInput_FromButtons_MatchesBitPattern()
    {
        // Left=1, Jump=4 → combined Buttons = 0b0101 = 5
        var fi = FrameInput.FromButtons(left: true, right: false, jump: true, attack: false);
        Assert.Equal((ushort)(FrameInput.Left | FrameInput.Jump), fi.Buttons);
    }

    [Fact]
    public void FrameInput_DefaultConstructor_ProducesNoPressedButtons()
    {
        var fi = new FrameInput(0);
        Assert.Equal((ushort)0, fi.Buttons);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SimConstants – no floats in entire Core.Sim assembly
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SimConstants_NoFloatOrDoubleOrDecimalFieldsInAssembly()
    {
        // Verify that the Core.Sim assembly contains no float/double/decimal fields.
        // This is the compile-time guarantee enforced at runtime via reflection.
        var assembly = typeof(SimConstants).Assembly;

        var forbidden = new HashSet<Type> { typeof(float), typeof(double), typeof(decimal) };

        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            // Skip compiler-generated types (e.g. <PrivateImplementationDetails>)
            if (type.Name.StartsWith('<')) continue;

            foreach (var field in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            {
                if (forbidden.Contains(field.FieldType))
                    violations.Add($"{type.FullName}.{field.Name} : {field.FieldType.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Float/double/decimal fields found in Core.Sim:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SimConstants_FixedScaleAndTicksPerSecond_ArePositiveInts()
    {
        Assert.True(SimConstants.FixedScale      > 0);
        Assert.True(SimConstants.TicksPerSecond  > 0);
    }

    [Fact]
    public void SimConstants_ArenaHasPositiveWidth()
    {
        Assert.True(SimConstants.MaxX > SimConstants.MinX);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SimState – factory + value-type copy semantics
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SimState_CreateInitial_SeedZeroThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SimState.CreateInitial(0u));
    }

    [Fact]
    public void SimState_CreateInitial_StartsAtFrameZero()
    {
        var s = SimState.CreateInitial(1u);
        Assert.Equal(0u, s.Frame);
    }

    [Fact]
    public void SimState_CreateInitial_IsDeterministicForSameSeed()
    {
        var a = SimState.CreateInitial(123u);
        var b = SimState.CreateInitial(123u);

        Assert.Equal(a.Frame,     b.Frame);
        Assert.Equal(a.Rng.State, b.Rng.State);
        Assert.Equal(a.P1.X,      b.P1.X);
        Assert.Equal(a.P1.Y,      b.P1.Y);
        Assert.Equal(a.P1.Facing, b.P1.Facing);
        Assert.Equal(a.P1.Hp,     b.P1.Hp);
        Assert.Equal(a.P2.X,      b.P2.X);
        Assert.Equal(a.P2.Y,      b.P2.Y);
        Assert.Equal(a.P2.Facing, b.P2.Facing);
        Assert.Equal(a.P2.Hp,     b.P2.Hp);
    }

    [Fact]
    public void SimState_CreateInitial_DifferentSeedsProduceDifferentRngStates()
    {
        var a = SimState.CreateInitial(1u);
        var b = SimState.CreateInitial(2u);
        Assert.NotEqual(a.Rng.State, b.Rng.State);
    }

    [Fact]
    public void SimState_CreateInitial_P1FacesRight_P2FacesLeft()
    {
        var s = SimState.CreateInitial(1u);
        Assert.Equal((sbyte)1,   s.P1.Facing);
        Assert.Equal((sbyte)(-1), s.P2.Facing);
    }

    [Fact]
    public void SimState_CreateInitial_SymmetricStartPositions()
    {
        var s = SimState.CreateInitial(1u);
        int center = (SimConstants.MinX + SimConstants.MaxX) / 2;
        Assert.True(s.P1.X < center, $"P1.X={s.P1.X} should be left of center={center}");
        Assert.True(s.P2.X > center, $"P2.X={s.P2.X} should be right of center={center}");
    }

    [Fact]
    public void SimState_IsValueType_CopyIsIndependent()
    {
        // Struct copy must not alias the original
        var a = SimState.CreateInitial(1u);
        var b = a;          // struct copy
        b.Frame = 999u;
        Assert.Equal(0u, a.Frame);  // a must be unaffected
    }

    [Fact]
    public void SimState_CreateInitial_PlayersStartWithFullHp()
    {
        var s = SimState.CreateInitial(1u);
        Assert.Equal(SimConstants.DefaultHp, s.P1.Hp);
        Assert.Equal(SimConstants.DefaultHp, s.P2.Hp);
    }
}
