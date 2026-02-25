namespace Core.Sim;

/// <summary>
/// The animation/logic state of a player. Underlying type is <see cref="byte"/>
/// so it occupies one byte inside <see cref="PlayerState"/> (no hidden floats,
/// no allocation, packs tightly in the struct).
/// </summary>
public enum ActionState : byte
{
    Idle     = 0,
    Run      = 1,
    Jump     = 2,
    Attack   = 3,
    Hitstun  = 4,
}
