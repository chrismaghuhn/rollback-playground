namespace Core.Sim;

/// <summary>
/// Produces a 32-bit checksum of a <see cref="SimState"/> for determinism
/// verification and golden regression tests.
///
/// ── Why field-wise, not layout-based ────────────────────────────────────────
///
/// Alternatives such as <c>MemoryMarshal.AsBytes</c>, <c>BitConverter</c> on
/// the raw struct memory, or <c>unsafe</c> pointer casts all depend on the
/// in-memory layout of the struct (field ordering, padding bytes, alignment).
/// That layout is an implementation detail of the C# compiler and the CLR:
///
///   • The runtime may insert invisible padding bytes between fields.
///   • <c>[StructLayout]</c> defaults differ between platforms.
///   • Padding bytes are uninitialised and can contain garbage, making the
///     checksum non-deterministic across allocations.
///   • Reordering fields (a refactoring concern) silently changes the hash
///     without any test breakage.
///
/// This implementation mixes each logical field individually in a fixed,
/// documented order.  The only thing that can change the checksum is a change
/// to an actual simulation value — which is exactly what we want to detect.
///
/// ── Algorithm ────────────────────────────────────────────────────────────────
///
/// FNV-1a 32-bit, applied once per logical field (4 bytes treated as one unit):
///
///     hash = Offset
///     for each field v:
///         hash ^= (uint)v
///         hash *= Prime
///
/// FNV-1a is simple, has no external dependencies, is free of floats/doubles,
/// and its avalanche properties are adequate for regression detection.
/// </summary>
public static class SimHash
{
    // FNV-1a 32-bit constants (http://www.isthe.com/chongo/tech/comp/fnv/)
    private const uint Offset = 2166136261u;
    private const uint Prime  = 16777619u;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the FNV-1a checksum of <paramref name="s"/>.
    /// Passed by readonly reference to avoid copying a large struct.
    /// </summary>
    public static uint Checksum(in SimState s)
    {
        uint hash = Offset;

        // A) Frame counter
        AddUInt32(ref hash, s.Frame);

        // B) P1 — all fields in declaration order (mirrors PlayerState.cs)
        AddPlayer(ref hash, in s.P1);

        // C) P2
        AddPlayer(ref hash, in s.P2);

        // D) PRNG state
        AddUInt32(ref hash, s.Rng.State);

        return hash;
    }

    /// <summary>
    /// Returns the FNV-1a checksum of a single <see cref="PlayerState"/>.
    /// Useful for targeted diagnostics when a checksum mismatch is being
    /// investigated (e.g. "which player's state diverged?").
    /// </summary>
    public static uint ChecksumPlayer(in PlayerState p)
    {
        uint hash = Offset;
        AddPlayer(ref hash, in p);
        return hash;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Mixes all fields of <paramref name="p"/> into <paramref name="hash"/>
    /// in a fixed order that mirrors the field declarations in
    /// <see cref="PlayerState"/>.
    /// </summary>
    private static void AddPlayer(ref uint hash, in PlayerState p)
    {
        AddInt32 (ref hash, p.X);
        AddInt32 (ref hash, p.Y);
        AddInt32 (ref hash, p.Vx);
        AddInt32 (ref hash, p.Vy);
        AddInt32 (ref hash, p.Facing);              // sbyte → int (sign-extended)
        AddUInt32(ref hash, (uint)(byte)p.State);   // ActionState (enum:byte) → uint
        AddInt32 (ref hash, p.HitstunFrames);
        AddInt32 (ref hash, p.Hp);
        AddInt32 (ref hash, p.AttackCooldownFrames);
        AddInt32 (ref hash, p.AttackActiveFrames);
        AddUInt32(ref hash, p.AttackHasHit);        // byte → uint (zero-extended)
    }

    /// <summary>FNV-1a mix for a single 32-bit unsigned word.</summary>
    private static void AddUInt32(ref uint hash, uint v)
    {
        hash ^= v;
        hash *= Prime;
    }

    /// <summary>
    /// FNV-1a mix for a signed 32-bit integer.
    /// Reinterprets the bits as unsigned — no information is lost.
    /// </summary>
    private static void AddInt32(ref uint hash, int v) =>
        AddUInt32(ref hash, unchecked((uint)v));
}
