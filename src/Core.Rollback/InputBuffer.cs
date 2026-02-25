using Core.Sim;

namespace Core.Rollback;

/// <summary>
/// Fixed-capacity circular ring buffer of <see cref="FrameInput"/> values, one
/// slot per simulation frame.
///
/// ── Design rationale ─────────────────────────────────────────────────────────
///
/// The rollback layer needs fast access to inputs by frame number.  A ring
/// buffer of size <em>capacity</em> gives O(1) insert and O(1) exact lookup
/// without any heap allocation in the hot path.  The trade-off: entries older
/// than <em>capacity</em> frames are silently evicted (the slot is overwritten
/// by the next frame that maps to the same index).
///
/// ── Sentinel convention ──────────────────────────────────────────────────────
///
/// Slot validity is tracked with a parallel <c>_frames[]</c> array initialised
/// to <see cref="uint.MaxValue"/> (a frame number that can never legitimately
/// occur in a 32-bit counter before heat death of the universe at 60 fps).
/// This ensures that a freshly constructed buffer returns false for
/// <see cref="TryGet"/> at frame 0 without any extra boolean flag per slot.
///
/// ── Prediction rule ──────────────────────────────────────────────────────────
///
/// <c>GetOrPredict</c> implements "repeat-last-known" semantics:
///   • Exact hit in buffer  →  return that input (no prediction needed).
///   • frame &gt; latest known →  repeat the latest known input unchanged.
///   • frame ≤ latest known →  search backwards [frame−1 … frame−capacity+1]
///                             for the most-recent stored input.
///   • Nothing found        →  return <c>default</c> (neutral / no buttons).
///
/// This mirrors how a client-side rollback engine treats dropped or late
/// packets: assume the remote player kept doing whatever they were last seen
/// doing until proven otherwise.
/// </summary>
public sealed class InputBuffer
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly int          _capacity;
    private readonly FrameInput[] _inputs;
    private readonly uint[]       _frames;   // sentinel = uint.MaxValue

    // Tracks the single most-recent (highest frame number) Set call.
    private bool       _hasLatest;
    private uint       _latestFrame;
    private FrameInput _latestInput;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="InputBuffer"/> with the given capacity.
    /// </summary>
    /// <param name="capacity">
    /// Number of frame slots to allocate.  Must be ≥ 2; smaller values are
    /// rejected because a capacity of 1 makes wraparound detection impossible.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than 2.
    /// </exception>
    public InputBuffer(int capacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "InputBuffer capacity must be >= 2.");

        _capacity = capacity;
        _inputs   = new FrameInput[capacity];
        _frames   = new uint[capacity];

        // Sentinel: no valid frame will ever equal uint.MaxValue.
        Array.Fill(_frames, uint.MaxValue);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores <paramref name="input"/> for <paramref name="frame"/>.
    /// If an older entry occupied the same ring slot it is silently evicted.
    /// </summary>
    public void Set(uint frame, FrameInput input)
    {
        int idx = (int)(frame % (uint)_capacity);
        _frames[idx] = frame;
        _inputs[idx] = input;

        // Update "latest" only when this frame is at or after the current latest.
        // Out-of-order (older) inserts do NOT overwrite the latest pointer.
        if (!_hasLatest || frame >= _latestFrame)
        {
            _hasLatest   = true;
            _latestFrame = frame;
            _latestInput = input;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> and populates <paramref name="input"/>
    /// when an entry for exactly <paramref name="frame"/> is present in the
    /// buffer.  Returns <see langword="false"/> if the slot has been evicted or
    /// was never written.
    /// </summary>
    public bool TryGet(uint frame, out FrameInput input)
    {
        int idx = (int)(frame % (uint)_capacity);

        if (_frames[idx] == frame)
        {
            input = _inputs[idx];
            return true;
        }

        input = default;
        return false;
    }

    /// <summary>
    /// Returns the best available input for <paramref name="frame"/> using the
    /// "repeat-last-known" prediction strategy (see class remarks).
    /// Never allocates; bounded O(<see cref="_capacity"/>) search at worst.
    /// </summary>
    public FrameInput GetOrPredict(uint frame)
    {
        // Fast path — exact hit.
        if (TryGet(frame, out FrameInput exact))
            return exact;

        // No inputs have ever been set: return neutral.
        if (!_hasLatest)
            return default;

        // Future frame — the remote player probably held the same buttons.
        if (frame > _latestFrame)
            return _latestInput;

        // frame ≤ _latestFrame: search backwards within the ring buffer window.
        // We look from (frame − 1) down to max(0, frame − capacity + 1).
        // Any entry older than _capacity frames has been evicted, so there is
        // no point searching further.

        if (frame == 0)
            return default; // nothing exists before frame 0

        // Compute the inclusive lower bound of the search window.
        uint minSearch = frame >= (uint)_capacity
                         ? frame - (uint)_capacity + 1
                         : 0u;

        uint f = frame - 1; // safe: frame > 0 checked above

        while (true)
        {
            if (TryGet(f, out FrameInput found))
                return found;

            if (f == minSearch || f == 0)
                break;

            f--;
        }

        return default;
    }

    /// <summary>
    /// Resets the buffer to its initial empty state.
    /// All slots are invalidated (sentinels restored); the latest-pointer is
    /// cleared.  Existing array allocations are reused — no heap traffic.
    /// </summary>
    public void Clear()
    {
        _hasLatest = false;
        Array.Fill(_frames, uint.MaxValue);
        // _inputs values are irrelevant while slots are invalidated; leaving
        // them as-is avoids a second fill pass.
    }
}
