using Core.Sim;

namespace Core.Rollback;

/// <summary>
/// Drives a rollback-netcode session for a two-player deterministic simulation.
///
/// ── Frame semantics ───────────────────────────────────────────────────────────
///
/// Inputs are tagged with the frame they act upon:
///
///   Input for frame f  →  SimStep.Step(state_f, p1Input_f, p2Input_f)  →  state_{f+1}
///
/// <see cref="SimStep.Step"/> increments <c>state.Frame</c> internally, so
/// after one call to <see cref="Tick"/>, <see cref="CurrentFrame"/> is exactly
/// one higher.  Snapshots are saved BEFORE the step so that a rollback to
/// frame f restores the exact state that existed when inputs for frame f were
/// first applied — and the resimulation can re-apply updated inputs correctly.
///
/// ── Why predicted inputs are written back into the buffer ────────────────────
///
/// When a real remote input for a past frame arrives via
/// <see cref="SetRemoteInput"/>, the engine compares it with whatever was used
/// during the original simulation.  To make this comparison possible, every
/// predicted (i.e. not-yet-confirmed) remote input is stored in
/// <c>_remoteInputs</c> via <see cref="InputBuffer.Set"/> at prediction time.
///
/// Without this, <see cref="InputBuffer.TryGet"/> would return false for the
/// predicted frame, and the mismatch — however large — would go undetected.
/// Storing predictions enables precise, per-frame mismatch detection:
///
///   "The engine assumed Left at frame 42 but the real input was Right →
///    roll back to frame 42 and re-simulate forward."
///
/// ── Rollback guarantee ────────────────────────────────────────────────────────
///
/// If the snapshot for the target frame is not found in <see cref="StateBuffer"/>
/// (evicted because <paramref name="historyCapacity"/> is too small), an
/// <see cref="InvalidOperationException"/> is thrown.  Increase
/// <paramref name="historyCapacity"/> or reduce the maximum supported lag.
/// </summary>
public sealed class RollbackEngine
{
    // ── Internal buffers ──────────────────────────────────────────────────────

    private readonly InputBuffer _localInputs;
    private readonly InputBuffer _remoteInputs;
    private readonly StateBuffer _stateBuffer;
    private readonly LocalPlayer _localPlayer;

    // ── Public state and statistics ───────────────────────────────────────────

    /// <summary>The simulation state at the current (most-recently stepped) frame.</summary>
    public SimState CurrentState  { get; private set; }

    /// <summary><see cref="CurrentState"/>.Frame — the next frame to be ticked.</summary>
    public uint CurrentFrame      => CurrentState.Frame;

    /// <summary>Total number of rollback operations performed.</summary>
    public int RollbackCount       { get; private set; }

    /// <summary>Total number of frames re-simulated across all rollbacks.</summary>
    public int RollbackFramesTotal { get; private set; }

    /// <summary>The deepest single rollback, measured in frames.</summary>
    public int MaxRollbackDepth    { get; private set; }

    /// <summary>Which player this engine instance controls locally.</summary>
    public LocalPlayer LocalPlayer { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new engine starting from <paramref name="initialState"/>.
    /// </summary>
    /// <param name="initialState">
    /// The starting state (usually from <see cref="SimState.CreateInitial"/>).
    /// Its <c>Frame</c> value determines the first frame number.
    /// </param>
    /// <param name="historyCapacity">
    /// Number of frames kept in the snapshot and input ring buffers.
    /// Must be ≥ 2.  Should be greater than the maximum expected remote-input
    /// lag in frames; e.g. 60 fps × 200 ms RTT = 12 frames of lag → use ≥ 32.
    /// </param>
    /// <param name="localPlayer">
    /// Which player this instance controls locally. Defaults to <see cref="LocalPlayer.P1"/>
    /// for backward compatibility with existing callers. Pass <see cref="LocalPlayer.P2"/>
    /// on the remote peer's machine.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="historyCapacity"/> is less than 2, or when
    /// <paramref name="localPlayer"/> is not <see cref="LocalPlayer.P1"/> or
    /// <see cref="LocalPlayer.P2"/>.
    /// </exception>
    public RollbackEngine(SimState initialState, int historyCapacity,
                          LocalPlayer localPlayer = LocalPlayer.P1)
    {
        if (historyCapacity < 2)
            throw new ArgumentOutOfRangeException(
                nameof(historyCapacity),
                historyCapacity,
                "historyCapacity must be >= 2.");

        if (localPlayer != LocalPlayer.P1 && localPlayer != LocalPlayer.P2)
            throw new ArgumentOutOfRangeException(
                nameof(localPlayer), localPlayer, "LocalPlayer must be P1 or P2.");

        _localPlayer = localPlayer;
        LocalPlayer  = localPlayer;

        CurrentState  = initialState;
        _localInputs  = new InputBuffer(historyCapacity);
        _remoteInputs = new InputBuffer(historyCapacity);
        _stateBuffer  = new StateBuffer(historyCapacity);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the simulation by one frame using <paramref name="localInput"/>
    /// for the local player (see <see cref="LocalPlayer"/>). The remote player's
    /// input is taken from the buffer (real if already delivered, otherwise
    /// predicted via repeat-last-known).
    ///
    /// The pre-step snapshot is archived so that a later call to
    /// <see cref="SetRemoteInput"/> can roll back to this frame.
    /// </summary>
    public void Tick(FrameInput localInput)
    {
        uint frame = CurrentState.Frame;

        // ── 1. Record local input ──────────────────────────────────────────
        _localInputs.Set(frame, localInput);

        // ── 2. Get or predict remote input ────────────────────────────────
        FrameInput remoteInput;
        if (!_remoteInputs.TryGet(frame, out remoteInput))
        {
            // No real input yet — predict and write back so we can detect
            // mismatch when the real input eventually arrives.
            remoteInput = _remoteInputs.GetOrPredict(frame);
            _remoteInputs.Set(frame, remoteInput);
        }

        // ── 3. Archive pre-step snapshot ──────────────────────────────────
        _stateBuffer.Save(frame, CurrentState);

        // ── 4. Advance simulation ─────────────────────────────────────────
        SimState prev = CurrentState;
        MapInputs(localInput, remoteInput, out var p1, out var p2);
        CurrentState = SimStep.Step(in prev, p1, p2);
    }

    /// <summary>
    /// Delivers a confirmed remote input for <paramref name="frame"/>.
    ///
    /// If the engine has already simulated past this frame using a different
    /// (predicted) input, it rolls back to <paramref name="frame"/> and
    /// re-simulates forward with the corrected input.  If the confirmed input
    /// matches the prediction (or no prediction was made), no rollback occurs.
    ///
    /// Inputs may arrive out of order; monotonically-increasing delivery is not
    /// required.
    /// </summary>
    public void SetRemoteInput(uint frame, FrameInput remoteInput)
    {
        if (_remoteInputs.TryGet(frame, out FrameInput existing))
        {
            if (existing.Buttons == remoteInput.Buttons)
                return; // prediction was correct — nothing to do

            // Mismatch: update the buffer, then roll back if we're past this frame.
            _remoteInputs.Set(frame, remoteInput);

            if (frame < CurrentFrame)
                RollbackTo(frame);
        }
        else
        {
            // Slot was never written (future frame) or evicted (too-small capacity).
            // Store unconditionally; no mismatch can be detected without the old value.
            _remoteInputs.Set(frame, remoteInput);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the snapshot at <paramref name="rollbackFrame"/>, updates rollback
    /// statistics, and re-simulates every frame from <paramref name="rollbackFrame"/>
    /// back up to (but not including) <c>endFrame</c>.
    /// </summary>
    private void RollbackTo(uint rollbackFrame)
    {
        uint endFrame = CurrentFrame; // capture before mutating CurrentState

        if (!_stateBuffer.TryLoad(rollbackFrame, out SimState snapshot))
            throw new InvalidOperationException(
                $"Cannot roll back to frame {rollbackFrame}: snapshot not found in history. " +
                $"Current frame: {endFrame}. " +
                $"The snapshot was likely evicted — increase historyCapacity.");

        // ── Stats ──────────────────────────────────────────────────────────
        int depth = (int)(endFrame - rollbackFrame);
        RollbackCount++;
        RollbackFramesTotal += depth;
        if (depth > MaxRollbackDepth)
            MaxRollbackDepth = depth;

        // ── Restore ────────────────────────────────────────────────────────
        CurrentState = snapshot; // CurrentState.Frame == rollbackFrame

        // ── Resimulate rollbackFrame .. endFrame-1 ─────────────────────────
        while (CurrentState.Frame < endFrame)
        {
            uint f = CurrentState.Frame;

            // Local input must have been recorded in Tick; absence is a bug.
            if (!_localInputs.TryGet(f, out FrameInput li))
                throw new InvalidOperationException(
                    $"Missing local input for frame {f} during resimulation " +
                    $"(rollback target {rollbackFrame}, end frame {endFrame}). " +
                    $"Ensure Tick() was called for every frame before SetRemoteInput().");

            // Remote input: use real if available, otherwise predict + store.
            FrameInput ri;
            if (!_remoteInputs.TryGet(f, out ri))
            {
                ri = _remoteInputs.GetOrPredict(f);
                _remoteInputs.Set(f, ri);
            }

            // Save pre-step snapshot (overwrites the original; new path is canonical).
            _stateBuffer.Save(f, CurrentState);

            SimState prevReplay = CurrentState;
            MapInputs(li, ri, out var p1r, out var p2r);
            CurrentState = SimStep.Step(in prevReplay, p1r, p2r);
        }
    }

    /// <summary>
    /// Maps local/remote inputs to the p1/p2 order expected by <see cref="SimStep.Step"/>.
    /// When this instance is P1: p1 = localInput, p2 = remoteInput.
    /// When this instance is P2: p1 = remoteInput, p2 = localInput.
    /// </summary>
    private void MapInputs(
        FrameInput localInput,  FrameInput remoteInput,
        out FrameInput p1,      out FrameInput p2)
    {
        if (_localPlayer == LocalPlayer.P1) { p1 = localInput;  p2 = remoteInput; }
        else                                { p1 = remoteInput; p2 = localInput;  }
    }
}
