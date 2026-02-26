// src/Core.Rollback/LocalPlayer.cs
namespace Core.Rollback;

/// <summary>
/// Identifies which player this <see cref="RollbackEngine"/> instance controls locally.
/// <c>default(LocalPlayer) == 0</c> is intentionally not a valid value; the constructor
/// validates and throws, preventing silent wrong-branch execution.
/// </summary>
public enum LocalPlayer : byte
{
    /// <summary>The local machine controls Player 1 (default for existing callers).</summary>
    P1 = 1,

    /// <summary>The local machine controls Player 2 (remote peer is P1).</summary>
    P2 = 2,
}
