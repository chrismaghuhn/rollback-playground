using Core.Sim;

namespace Core.Rollback;

/// <summary>
/// Fixed-capacity circular ring buffer of <see cref="SimState"/> snapshots,
/// indexed by simulation frame number.
///
/// ── Why value-copy snapshots matter for rollback ─────────────────────────────
///
/// Rollback netcode works by saving a snapshot of the entire game state at every
/// frame, then rewinding to an earlier snapshot when a misprediction is detected
/// and re-simulating forward with the corrected inputs.
///
/// For this to be safe, every stored snapshot must be a fully independent copy of
/// the state at the time it was saved.  If <see cref="SimState"/> were a class
/// (reference type), all slots would alias the same heap object and overwriting
/// one frame's snapshot would silently corrupt every other one — exactly the
/// kind of bug that is invisible until the wrong frame gets loaded during a
/// rollback and the simulation diverges.
///
/// Because <see cref="SimState"/> is a <c>struct</c>, the assignment
/// <c>_states[idx] = state</c> in <see cref="Save"/> copies every field by
/// value — no aliasing, no separate <c>Clone()</c> call needed, zero additional
/// heap allocation.  <see cref="TryLoad"/> likewise returns a copy via the
/// <c>out</c> parameter, so callers can mutate the loaded state freely without
/// touching the archive.
///
/// ── Sentinel convention ──────────────────────────────────────────────────────
///
/// <c>_frames[]</c> is initialised to <see cref="uint.MaxValue"/>.  A real
/// frame counter starts at 0 and increments by 1 per tick; it would take
/// 2 147 483 648 hours at 60 fps to reach <see cref="uint.MaxValue"/>, so the
/// sentinel never collides with a legitimate frame number in practice.
/// </summary>
public sealed class StateBuffer
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly int        _capacity;
    private readonly SimState[] _states;
    private readonly uint[]     _frames; // sentinel = uint.MaxValue

    private bool _hasLatest;
    private uint _latestFrame;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="StateBuffer"/> with the given capacity.
    /// </summary>
    /// <param name="capacity">
    /// Number of snapshot slots to allocate.  Must be ≥ 2.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than 2.
    /// </exception>
    public StateBuffer(int capacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "StateBuffer capacity must be >= 2.");

        _capacity = capacity;
        _states   = new SimState[capacity];
        _frames   = new uint[capacity];

        Array.Fill(_frames, uint.MaxValue);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The highest frame number for which a snapshot was ever saved, or
    /// <see langword="null"/> if the buffer is empty (or has been cleared).
    /// </summary>
    public uint? LatestFrame => _hasLatest ? _latestFrame : null;

    /// <summary>
    /// Stores a value-copy snapshot of <paramref name="state"/> at
    /// <paramref name="frame"/>.  If another snapshot already occupies the same
    /// ring slot it is silently evicted.
    /// </summary>
    /// <param name="frame">The simulation frame this snapshot belongs to.</param>
    /// <param name="state">
    /// The state to archive.  Passed by readonly reference to avoid copying on
    /// the caller side; an internal copy is made into the array slot.
    /// </param>
    public void Save(uint frame, in SimState state)
    {
        int idx = (int)(frame % (uint)_capacity);
        _frames[idx] = frame;
        _states[idx] = state; // struct assignment = full value copy, no aliasing

        if (!_hasLatest || frame >= _latestFrame)
        {
            _hasLatest   = true;
            _latestFrame = frame;
        }
    }

    /// <summary>
    /// Attempts to load the snapshot saved for exactly <paramref name="frame"/>.
    /// </summary>
    /// <param name="frame">The frame whose snapshot is requested.</param>
    /// <param name="state">
    /// On success, receives a value-copy of the stored snapshot.
    /// On failure, receives <c>default(SimState)</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the snapshot is present (not evicted);
    /// <see langword="false"/> otherwise.
    /// </returns>
    public bool TryLoad(uint frame, out SimState state)
    {
        int idx = (int)(frame % (uint)_capacity);

        if (_frames[idx] == frame)
        {
            state = _states[idx]; // struct assignment = independent copy
            return true;
        }

        state = default;
        return false;
    }

    /// <summary>
    /// Invalidates all stored snapshots and resets the latest-frame pointer.
    /// The backing arrays are retained for reuse — no heap traffic.
    /// </summary>
    public void Clear()
    {
        _hasLatest = false;
        Array.Fill(_frames, uint.MaxValue);
        // _states elements are stale but harmless; TryLoad will reject them
        // because their corresponding _frames slots now hold the sentinel.
    }
}
