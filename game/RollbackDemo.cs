using Godot;
using Core.Sim;
using Core.Rollback;
using System.Collections.Generic;

/// <summary>
/// Rollback-netcode demo scene.
///
/// P1 = keyboard  (A = left, D = right, Space = jump, J = attack).
/// P2 = scripted  (deterministic 60-frame cycle), delivered with
///      configurable lag to trigger visible rollbacks.
///
/// Physics runs at exactly 60 Hz (project setting).
/// All sim logic is in Core.Rollback / Core.Sim — no engine calls there.
/// </summary>
public partial class RollbackDemo : Node2D
{
    // ─── Config ──────────────────────────────────────────────────────────────

    private const uint  Seed         = 1u;
    private const int   HistoryCap   = 64;

    // Viewport dimensions (must match project.godot display settings)
    private const float ViewportW    = 1280f;
    private const float ViewportH    = 600f;

    // Scale: fit arena height (12 wu) exactly into viewport height
    private const float ScalePxPerWu = ViewportH / 12f;                          // 50 px/wu
    private const float PxPerFixed   = ScalePxPerWu / SimConstants.FixedScale;   // 0.05 px/fixed-unit

    // Arena pixel dimensions and horizontal centering margin
    private const float ArenaPxW  = SimConstants.MaxX * PxPerFixed;              // 1000 px
    private const float MarginX   = (ViewportW - ArenaPxW) * 0.5f;               // 140 px

    // Player visual dimensions (derived from sim constants — stays correct if constants change)
    private const float PlayerWpx = SimConstants.PlayerWidth  * PxPerFixed;      // 30 px
    private const float PlayerHpx = SimConstants.PlayerHeight * PxPerFixed;      // 45 px

    private static readonly Color ColArena  = new(0.15f, 0.15f, 0.15f);
    private static readonly Color ColP1     = new(0.35f, 0.60f, 1.00f);  // blue
    private static readonly Color ColP2     = new(1.00f, 0.40f, 0.30f);  // red
    private static readonly Color ColHpFill = new(0.20f, 0.80f, 0.20f);
    private static readonly Color ColHpBg   = new(0.50f, 0.10f, 0.10f);

    // ─── Godot lifecycle ─────────────────────────────────────────────────────

    private RollbackEngine _engine    = null!;
    private Label          _debugLbl  = null!;
    private Label          _lagLbl    = null!;

    public override void _Ready()
    {
        _engine = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap);
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        uint f = _engine.CurrentFrame;

        FrameInput p1 = ReadP1Input();   // 1. Poll local input
        EnqueueP2(f);                    // 2. Generate + schedule P2's real input
        DeliverDue(f);                   // 3. Deliver any inputs due this frame (may rollback)
        _engine.Tick(p1);               // 4. Advance sim
        UpdateDebugLabel();             // 5. Refresh HUD text
        QueueRedraw();                  // 6. Request _Draw
    }

    public override void _Draw()
    {
        DrawArena();
        DrawPlayer(_engine.CurrentState.P1, ColP1);
        DrawPlayer(_engine.CurrentState.P2, ColP2);
    }

    // ─── Input mapping ───────────────────────────────────────────────────────

    private static FrameInput ReadP1Input() => FrameInput.FromButtons(
        left:   Input.IsKeyPressed(Key.A),
        right:  Input.IsKeyPressed(Key.D),
        jump:   Input.IsKeyPressed(Key.Space),
        attack: Input.IsKeyPressed(Key.J));

    // ─── Lag simulation + delivery ───────────────────────────────────────────

    private readonly Queue<(uint deliveryAt, uint simFrame, FrameInput input)> _lagBuffer = new();
    private int _delayFrames = 0;

    /// <summary>
    /// Deterministic 60-frame input cycle for P2.
    ///   Frames  0-29 → Right
    ///   Frames 30-44 → Left
    ///   Frames 45-59 → Jump
    /// The phase boundary at frame 30 (Right→Left) will reliably cause a mismatch
    /// whenever _delayFrames > 0, making rollbacks immediately visible.
    /// </summary>
    private static FrameInput ScriptP2Input(uint frame)
    {
        uint phase = frame % 60u;
        bool right = phase < 30u;
        bool left  = phase is >= 30u and < 45u;
        bool jump  = phase >= 45u;
        return FrameInput.FromButtons(left, right, jump, attack: false);
    }

    /// <summary>Schedules P2's real input for <paramref name="currentFrame"/> to arrive after the configured lag.</summary>
    private void EnqueueP2(uint currentFrame) =>
        _lagBuffer.Enqueue((currentFrame + (uint)_delayFrames, currentFrame, ScriptP2Input(currentFrame)));

    /// <summary>Delivers every enqueued P2 input whose delivery frame has arrived.</summary>
    private void DeliverDue(uint currentFrame)
    {
        while (_lagBuffer.Count > 0 && _lagBuffer.Peek().deliveryAt <= currentFrame)
        {
            var (_, simFrame, input) = _lagBuffer.Dequeue();
            _engine.SetRemoteInput(simFrame, input);
        }
    }

    // ─── Rendering (_Draw) ───────────────────────────────────────────────────

    private void DrawArena()
    {
        float left   = MarginX;
        float right  = MarginX + ArenaPxW;
        float bottom = ViewportH;

        DrawLine(new Vector2(left, bottom),  new Vector2(right, bottom), Colors.White,   2f); // ground
        DrawLine(new Vector2(left, 0f),      new Vector2(left, bottom),  Colors.DimGray, 2f); // left wall
        DrawLine(new Vector2(right, 0f),     new Vector2(right, bottom), Colors.DimGray, 2f); // right wall
    }

    private void DrawPlayer(PlayerState p, Color color)
    {
        float sx = MarginX + p.X * PxPerFixed;
        float sy = ViewportH - p.Y * PxPerFixed;  // flip Y: sim Y up, Godot Y down

        // Body
        var body = new Rect2(sx, sy - PlayerHpx, PlayerWpx, PlayerHpx);
        DrawRect(body, color);

        // Facing indicator — 4 px strip on the "front" edge
        float stripX = p.Facing > 0 ? sx + PlayerWpx - 4f : sx;
        DrawRect(new Rect2(stripX, sy - PlayerHpx, 4f, PlayerHpx), color.Darkened(0.4f));

        // HP bar (6 px above player)
        float barY = sy - PlayerHpx - 7f;
        DrawRect(new Rect2(sx, barY, PlayerWpx, 4f), ColHpBg);
        float hpFrac = Mathf.Clamp(p.Hp / (float)SimConstants.DefaultHp, 0f, 1f);
        DrawRect(new Rect2(sx, barY, PlayerWpx * hpFrac, 4f), ColHpFill);
    }

    // ─── UI creation ─────────────────────────────────────────────────────────

    private void BuildUi()
    {
        var hud = new CanvasLayer();
        AddChild(hud);

        // Debug label — top-left, fixed monospace readout
        _debugLbl = new Label();
        _debugLbl.Position = new Vector2(8f, 8f);
        _debugLbl.AddThemeFontSizeOverride("font_size", 14);
        hud.AddChild(_debugLbl);
        UpdateDebugLabel();

        // Lag slider row — top-right
        _lagLbl = new Label();
        _lagLbl.Text = "0 frames";

        var slider = new HSlider();
        slider.MinValue          = 0;
        slider.MaxValue          = 10;
        slider.Step              = 1;
        slider.Value             = 0;
        slider.CustomMinimumSize = new Vector2(160f, 20f);
        slider.ValueChanged     += v =>
        {
            _delayFrames   = (int)v;
            _lagLbl.Text   = $"{(int)v} frames";
        };

        var lagHeaderLbl = new Label();
        lagHeaderLbl.Text = "Lag: ";

        var row = new HBoxContainer();
        row.Position = new Vector2(ViewportW - 280f, 8f);
        row.AddChild(lagHeaderLbl);
        row.AddChild(slider);
        row.AddChild(_lagLbl);
        hud.AddChild(row);
    }

    private void UpdateDebugLabel()
    {
        _debugLbl.Text =
            $"Frame:         {_engine.CurrentFrame,7}\n"       +
            $"RollbackCount: {_engine.RollbackCount,7}\n"      +
            $"MaxRollback:   {_engine.MaxRollbackDepth,7} frames\n" +
            $"FramesRolled:  {_engine.RollbackFramesTotal,7}\n" +
            $"DelayFrames:   {_delayFrames,7}";
    }
}
