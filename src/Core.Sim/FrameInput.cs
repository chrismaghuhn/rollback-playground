namespace Core.Sim;

/// <summary>
/// A single frame's worth of player input, stored as a bitmask of pressed buttons.
/// Readonly struct: no mutation, no allocation, safe to copy freely.
/// </summary>
public readonly struct FrameInput
{
    // ── Bit-flag constants ────────────────────────────────────────────────────
    //
    // Using ushort (16 bits) leaves room for 12 more buttons without changing the
    // wire format. Hex literals avoid the ambiguity of (1 << N) int→ushort narrowing.

    public const ushort Left   = 0x0001;
    public const ushort Right  = 0x0002;
    public const ushort Jump   = 0x0004;
    public const ushort Attack = 0x0008;

    // ── Data ─────────────────────────────────────────────────────────────────

    public ushort Buttons { get; }

    // ── Construction ─────────────────────────────────────────────────────────

    public FrameInput(ushort buttons) => Buttons = buttons;

    /// <summary>
    /// Builds a <see cref="FrameInput"/> from individual bool flags.
    /// Useful in tests and in game-loop input polling.
    /// </summary>
    public static FrameInput FromButtons(bool left, bool right, bool jump, bool attack)
    {
        ushort b = 0;
        if (left)   b |= Left;
        if (right)  b |= Right;
        if (jump)   b |= Jump;
        if (attack) b |= Attack;
        return new FrameInput(b);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public bool LeftPressed   => (Buttons & Left)   != 0;
    public bool RightPressed  => (Buttons & Right)  != 0;
    public bool JumpPressed   => (Buttons & Jump)   != 0;
    public bool AttackPressed => (Buttons & Attack) != 0;

    public override string ToString() =>
        $"[{(LeftPressed ? "L" : ".")}{(RightPressed ? "R" : ".")}" +
        $"{(JumpPressed  ? "J" : ".")}{(AttackPressed ? "A" : ".")}]";
}
