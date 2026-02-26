using Godot;
using Core.Net;
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

    // ─── Mode / connection state ──────────────────────────────────────────────

    /// <summary>Top-level demo mode.</summary>
    private enum DemoMode { Offline, Lan }

    /// <summary>LAN connection lifecycle state.</summary>
    private enum LanState { Disconnected, Hosting, Joining, Connected }

    // ─── Godot lifecycle ─────────────────────────────────────────────────────

    private RollbackEngine _engine    = null!;
    private CanvasLayer    _hud       = null!;
    private Label          _debugLbl  = null!;
    private Label          _lagLbl    = null!;

    // ─── LAN UI controls ──────────────────────────────────────────────────────

    private HBoxContainer _lagRow        = null!;   // reference so we can toggle visibility
    private VBoxContainer _lanPanel      = null!;
    private Label         _lanStatusLbl  = null!;
    private Button        _hostBtn       = null!;
    private Button        _joinBtn       = null!;
    private Button        _disconnectBtn = null!;
    private LineEdit      _ipField       = null!;
    private SpinBox       _portSpinBox   = null!;

    // ─── Mode + LAN connection state ─────────────────────────────────────────

    private DemoMode    _mode              = DemoMode.Offline;
    private LanState    _lanState          = LanState.Disconnected;
    private string      _remoteIp          = "127.0.0.1";
    private int         _port              = 7777;
    private LocalPlayer _localPlayer       = LocalPlayer.P1;
    /// <summary>
    /// True when the simulation should advance each physics tick.
    /// Always true in Offline mode; true in LAN mode only when Connected.
    /// The physics gate in _PhysicsProcess reads this flag.
    /// </summary>
    private bool        _simulationRunning = true;

    // ─── LAN networking ───────────────────────────────────────────────────────

    /// <summary>UDP socket; null when disconnected.</summary>
    private System.Net.Sockets.UdpClient?  _udpSocket              = null;

    /// <summary>Remote peer endpoint; set in OnHost/OnJoin.</summary>
    private System.Net.IPEndPoint?         _remoteEp               = null;

    /// <summary>Pre-allocated send buffer — avoids per-tick allocation.</summary>
    private readonly byte[]                _sendBuf                = new byte[PacketCodec.MaxPacketSize];

    /// <summary>
    /// Pre-allocated input array for <see cref="SendInputPacket"/> — avoids per-tick
    /// heap allocation. Passed to <see cref="InputPacket"/> with an explicit count;
    /// only elements [0..count-1] are written and read each tick.
    /// </summary>
    private readonly FrameInput[]          _inputsScratch          = new FrameInput[PacketCodec.MaxInputsPerPacket];

    /// <summary>
    /// 256-slot ring buffer of local inputs indexed by <c>frame &amp; 255</c>.
    /// Populated each tick before Tick(); used to build redundant outgoing packets.
    /// </summary>
    private readonly FrameInput[]          _localInputRing         = new FrameInput[256];

    /// <summary>
    /// Highest remote frame index confirmed received from the peer (AckFrame in
    /// packets we receive). Stored so we can fill AckFrame in our outgoing packets.
    /// uint.MaxValue = "never confirmed".
    /// </summary>
    private uint                           _latestRemoteFrameConfirmed = uint.MaxValue;

    /// <summary>
    /// Counts physics frames while in Joining state; drives the periodic HELLO timer.
    /// Reset to 0 in OnJoin().
    /// </summary>
    private int                            _helloSendTimer         = 0;

    // Pre-allocated ASCII handshake byte arrays — avoids per-tick Encoding.GetBytes.
    private static readonly byte[] _helloBytes = System.Text.Encoding.ASCII.GetBytes("HELLO");
    private static readonly byte[] _ackBytes   = System.Text.Encoding.ASCII.GetBytes("ACK");
    private static readonly byte[] _startBytes = System.Text.Encoding.ASCII.GetBytes("START");

    public override void _Ready()
    {
        // Viewport size (1280×600) is locked in project.godot [display] section.
        _engine = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap, _localPlayer);
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        // ── LAN: poll socket every tick regardless of _simulationRunning ──────
        // This covers handshake traffic (Hosting/Joining) and gameplay (Connected).
        if (_mode == DemoMode.Lan && _udpSocket is not null)
        {
            ReceiveAll();

            // Joining: send HELLO every 30 frames (= 0.5 s at 60 Hz) until ACK arrives
            if (_lanState == LanState.Joining)
            {
                if (_helloSendTimer % 30 == 0)
                    _udpSocket.Send(_helloBytes, _helloBytes.Length, _remoteEp);
                _helloSendTimer++;
            }
        }

        // ── Physics gate: halt engine until Connected (or always run in Offline) ──
        if (!_simulationRunning)
        {
            UpdateDebugLabel();
            QueueRedraw();
            return;
        }

        uint f = _engine.CurrentFrame;

        if (_mode == DemoMode.Lan)
        {
            // LAN Connected path: keyboard → engine → network
            FrameInput localInput    = ReadP1Input(); // same keys for P1 or P2 role
            _localInputRing[f & 255] = localInput;
            _engine.Tick(localInput);
            SendInputPacket();
        }
        else
        {
            // Offline path: scripted P2 with simulated lag (unchanged)
            FrameInput p1 = ReadP1Input();
            EnqueueP2(f);
            DeliverDue(f);
            _engine.Tick(p1);
        }

        UpdateDebugLabel();
        QueueRedraw();
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
    // Written from the UI thread (ValueChanged); read from the physics thread (_PhysicsProcess).
    // Safe only with Godot's single-threaded physics (the default).
    private int _delayFrames = 0;

    // Highest simFrame for which a confirmed remote input has been delivered.
    // uint.MaxValue = “never delivered” — same sentinel pattern as InputBuffer / StateBuffer.
    private uint _latestRemoteFrame = uint.MaxValue;

    // HUD visibility (F1 = debug label only; F2 = full HUD including slider).
    private bool _overlayVisible = true;
    private bool _hudVisible     = true;

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

            // Use Math.Max to stay monotone even if queue order shifts after a
            // live delay-slider change produces out-of-order deliveries.
            _latestRemoteFrame = _latestRemoteFrame == uint.MaxValue
                ? simFrame
                : Math.Max(_latestRemoteFrame, simFrame);
        }
    }

    // ─── Rendering (_Draw) ───────────────────────────────────────────────────

    private void DrawArena()
    {
        float left   = MarginX;
        float right  = MarginX + ArenaPxW;
        float bottom = ViewportH;

        // Arena background
        DrawRect(new Rect2(MarginX, 0f, ArenaPxW, ViewportH), ColArena);

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
        _hud = new CanvasLayer();
        AddChild(_hud);

        // Debug label — top-left, fixed monospace readout
        _debugLbl = new Label();
        _debugLbl.Position = new Vector2(8f, 8f);
        _debugLbl.AddThemeFontSizeOverride("font_size", 14);
        _hud.AddChild(_debugLbl);
        UpdateDebugLabel();

        // Mode switch — top-center (Offline | LAN)
        BuildModeSwitch();

        // Lag slider row — top-right, visible in Offline mode only
        _lagLbl = new Label { Text = "0 frames" };

        var slider = new HSlider();
        slider.MinValue          = 0;
        slider.MaxValue          = 10;
        slider.Step              = 1;
        slider.Value             = 0;
        slider.CustomMinimumSize = new Vector2(160f, 20f);
        slider.ValueChanged     += v =>
        {
            _delayFrames = (int)v;
            _lagLbl.Text = $"{(int)v} frames";
        };

        var lagHeaderLbl = new Label { Text = "Lag: " };

        _lagRow = new HBoxContainer();
        _lagRow.Position = new Vector2(ViewportW - 280f, 8f);
        _lagRow.AddChild(lagHeaderLbl);
        _lagRow.AddChild(slider);
        _lagRow.AddChild(_lagLbl);
        _hud.AddChild(_lagRow);

        // LAN panel — top-right, visible in LAN mode only (hidden initially)
        BuildLanPanel();
    }

    private void BuildModeSwitch()
    {
        var offlineBtn = new Button { Text = "Offline" };
        var lanBtn     = new Button { Text = "LAN" };

        offlineBtn.Pressed += () => SwitchMode(DemoMode.Offline);
        lanBtn.Pressed     += () => SwitchMode(DemoMode.Lan);

        var row = new HBoxContainer();
        row.Position = new Vector2(ViewportW / 2f - 50f, 8f);
        row.AddChild(offlineBtn);
        row.AddChild(lanBtn);
        _hud.AddChild(row);
    }

    private void BuildLanPanel()
    {
        _lanPanel = new VBoxContainer();
        _lanPanel.Position = new Vector2(ViewportW - 320f, 8f);
        _lanPanel.Visible  = false; // hidden until LAN mode is active
        _hud.AddChild(_lanPanel);

        // Status label — shows DISCONNECTED / HOSTING / JOINING / CONNECTED
        _lanStatusLbl = new Label { Text = "State: DISCONNECTED" };
        _lanPanel.AddChild(_lanStatusLbl);

        // Address row: IP + Port
        _ipField = new LineEdit { Text = _remoteIp, CustomMinimumSize = new Vector2(120f, 0f) };
        _ipField.TextChanged += t => _remoteIp = t;

        _portSpinBox = new SpinBox();
        _portSpinBox.MinValue          = 1;
        _portSpinBox.MaxValue          = 65535;
        _portSpinBox.Value             = _port;
        _portSpinBox.CustomMinimumSize = new Vector2(80f, 0f);
        _portSpinBox.ValueChanged     += v => _port = (int)v;

        var addrRow = new HBoxContainer();
        addrRow.AddChild(new Label { Text = "IP " });
        addrRow.AddChild(_ipField);
        addrRow.AddChild(new Label { Text = " Port " });
        addrRow.AddChild(_portSpinBox);
        _lanPanel.AddChild(addrRow);

        // Action buttons — Disconnect starts disabled (nothing to disconnect from yet)
        _hostBtn       = new Button { Text = "Host" };
        _joinBtn       = new Button { Text = "Join" };
        _disconnectBtn = new Button { Text = "Disconnect", Disabled = true };

        _hostBtn.Pressed       += OnHost;
        _joinBtn.Pressed       += OnJoin;
        _disconnectBtn.Pressed += OnDisconnect;

        var btnRow = new HBoxContainer();
        btnRow.AddChild(_hostBtn);
        btnRow.AddChild(_joinBtn);
        btnRow.AddChild(_disconnectBtn);
        _lanPanel.AddChild(btnRow);
    }

    /// <summary>
    /// Switches the top-level demo mode. Offline: shows lag slider, resets engine to P1.
    /// LAN: hides lag slider, shows LAN panel in Disconnected state.
    /// </summary>
    private void SwitchMode(DemoMode newMode)
    {
        if (_mode == newMode) return;
        _mode = newMode;

        if (newMode == DemoMode.Offline)
        {
            CloseSocket();
            _lanState          = LanState.Disconnected;
            _localPlayer       = LocalPlayer.P1;
            _simulationRunning = true;
            // Reset engine and lag-simulation state so Offline starts cleanly.
            _engine                = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap, _localPlayer);
            _lagBuffer.Clear();
            _latestRemoteFrame     = uint.MaxValue;
            _hostBtn.Disabled      = false;
            _joinBtn.Disabled      = false;
            _disconnectBtn.Disabled = true;
        }
        else // Lan
        {
            _lanState              = LanState.Disconnected;
            _simulationRunning     = false;
            _lagBuffer.Clear(); // discard stale offline-lag-sim entries
            _latestRemoteFrame     = uint.MaxValue;
            _lanStatusLbl.Text     = "State: DISCONNECTED";
        }

        _lagRow.Visible   = (newMode == DemoMode.Offline);
        _lanPanel.Visible = (newMode == DemoMode.Lan);
        UpdateDebugLabel();
    }

    /// <summary>
    /// Host button: become P1, enter Hosting state, freeze simulation until peer connects.
    /// Re-creates the engine so both sides start from the same initial state.
    /// </summary>
    private void OnHost()
    {
        CloseSocket(); // defensive: close any leftover socket from a previous attempt
        _lanState          = LanState.Hosting;
        _localPlayer       = LocalPlayer.P1;
        _simulationRunning = false;
        _engine            = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap, _localPlayer);
        _lagBuffer.Clear();
        _latestRemoteFrame = uint.MaxValue;

        // Open socket — Host listens on _port; sends to remote at _port + 1
        _udpSocket             = new System.Net.Sockets.UdpClient(_port);
        _udpSocket.Client.Blocking = false;
        _remoteEp              = new System.Net.IPEndPoint(
                                     System.Net.IPAddress.Parse(_remoteIp), _port + 1);

        _lanStatusLbl.Text      = "State: HOSTING  (waiting for peer)";
        _hostBtn.Disabled       = true;
        _joinBtn.Disabled       = true;
        _disconnectBtn.Disabled = false;
        UpdateDebugLabel();
    }

    /// <summary>
    /// Join button: become P2, enter Joining state, freeze simulation until host connects.
    /// Re-creates the engine so both sides start from the same initial state.
    /// </summary>
    private void OnJoin()
    {
        CloseSocket(); // defensive
        _lanState          = LanState.Joining;
        _localPlayer       = LocalPlayer.P2;
        _simulationRunning = false;
        _engine            = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap, _localPlayer);
        _lagBuffer.Clear();
        _latestRemoteFrame = uint.MaxValue;

        // Open socket — Joiner listens on _port + 1; sends to host at _port
        _udpSocket             = new System.Net.Sockets.UdpClient(_port + 1);
        _udpSocket.Client.Blocking = false;
        _remoteEp              = new System.Net.IPEndPoint(
                                     System.Net.IPAddress.Parse(_remoteIp), _port);
        _helloSendTimer        = 0;

        _lanStatusLbl.Text      = "State: JOINING  (waiting for host)";
        _hostBtn.Disabled       = true;
        _joinBtn.Disabled       = true;
        _disconnectBtn.Disabled = false;
        UpdateDebugLabel();
    }

    /// <summary>
    /// Disconnect button: return to Disconnected sub-state.
    /// Freezes the simulation at its current frame (does not reset engine).
    /// </summary>
    private void OnDisconnect()
    {
        CloseSocket();
        _lanState               = LanState.Disconnected;
        _simulationRunning      = false;
        _lanStatusLbl.Text      = "State: DISCONNECTED";
        _hostBtn.Disabled       = false;
        _joinBtn.Disabled       = false;
        _disconnectBtn.Disabled = true;
        UpdateDebugLabel();
    }

    // ─── Networking helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Closes and discards the UDP socket. Safe to call when socket is already null.
    /// Resets per-session networking state.
    /// </summary>
    private void CloseSocket()
    {
        _udpSocket?.Close();
        _udpSocket              = null;
        _remoteEp               = null;
        _helloSendTimer         = 0;
        _latestRemoteFrameConfirmed = uint.MaxValue;
    }

    /// <summary>
    /// Returns true when <paramref name="raw"/> contains exactly the bytes of
    /// <paramref name="expected"/> (ASCII literal, e.g. <c>"HELLO"u8</c>).
    /// </summary>
    private static bool IsMessage(byte[] raw, System.ReadOnlySpan<byte> expected) =>
        raw.Length == expected.Length && raw.AsSpan().SequenceEqual(expected);

    /// <summary>
    /// Moves to <see cref="LanState.Connected"/> from either Hosting or Joining.
    /// Re-creates the engine from Frame 0 with a 512-frame history buffer so that
    /// real network latency (up to ~200 ms at 60 fps ≈ 12 frames) has headroom.
    /// </summary>
    private void TransitionToConnected()
    {
        _lanState                   = LanState.Connected;
        _simulationRunning          = true;
        // Deep history for LAN: 60 fps × 200 ms RTT ≈ 12 frames; 512 gives ×40 headroom.
        _engine                     = new RollbackEngine(SimState.CreateInitial(Seed), 512, _localPlayer);
        _latestRemoteFrame          = uint.MaxValue;
        _latestRemoteFrameConfirmed = uint.MaxValue;
        _lanStatusLbl.Text          = "State: CONNECTED";
        // Host/Join stay disabled; Disconnect stays enabled (set in OnHost/OnJoin already)
        UpdateDebugLabel();
    }

    /// <summary>
    /// Drains the UDP socket without blocking.
    /// Routes each datagram to handshake or gameplay processing.
    /// </summary>
    private void ReceiveAll()
    {
        if (_udpSocket is null) return;

        // UdpClient.Available reports bytes ready to read; stops us blocking on Receive().
        var anyEp = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (_udpSocket.Available > 0)
        {
            System.Net.IPEndPoint sender = anyEp;
            byte[] raw = _udpSocket.Receive(ref sender);
            ProcessPacket(raw);
        }
    }

    /// <summary>
    /// Routes a received datagram:
    /// – ASCII handshake messages during Hosting/Joining states.
    /// – RBN1 gameplay packets during Connected state.
    /// Unrecognised packets are silently dropped.
    /// </summary>
    private void ProcessPacket(byte[] raw)
    {
        // ── Handshake ─────────────────────────────────────────────────────────

        // Host receives HELLO from Joiner → reply ACK
        if (_lanState == LanState.Hosting && IsMessage(raw, "HELLO"u8))
        {
            _udpSocket!.Send(_ackBytes, _ackBytes.Length, _remoteEp!);
            return;
        }

        // Joiner receives ACK from Host → reply START + go Connected
        if (_lanState == LanState.Joining && IsMessage(raw, "ACK"u8))
        {
            _udpSocket!.Send(_startBytes, _startBytes.Length, _remoteEp!);
            TransitionToConnected();
            return;
        }

        // Host receives START from Joiner → go Connected
        if (_lanState == LanState.Hosting && IsMessage(raw, "START"u8))
        {
            TransitionToConnected();
            return;
        }

        // ── Gameplay packets ──────────────────────────────────────────────────
        // Gameplay packets are decoded by ProcessGameplayPacket and fed to the engine.
        if (_lanState == LanState.Connected)
        {
            ProcessGameplayPacket(raw);
        }
    }

    /// <summary>
    /// Decodes an RBN1 gameplay packet and feeds inputs to the engine.
    /// Silently ignores malformed packets.
    /// </summary>
    private void ProcessGameplayPacket(byte[] raw)
    {
        // Zero-alloc decode into stack-allocated span
        Span<FrameInput> dst = stackalloc FrameInput[PacketCodec.MaxInputsPerPacket];
        if (!PacketCodec.TryDecodeInto(raw.AsSpan(), dst, out var header, out int count))
            return; // malformed — drop

        for (int i = 0; i < count; i++)
            _engine.SetRemoteInput(header.StartFrame + (uint)i, dst[i]);

        // Track highest remote frame received (for PRED/CONF display)
        uint highestReceived = header.StartFrame + (uint)count - 1u;
        _latestRemoteFrame = _latestRemoteFrame == uint.MaxValue
            ? highestReceived
            : Math.Max(_latestRemoteFrame, highestReceived);

        // Track what remote has confirmed from us (for AckFrame in outgoing packets)
        _latestRemoteFrameConfirmed = _latestRemoteFrameConfirmed == uint.MaxValue
            ? header.AckFrame
            : Math.Max(_latestRemoteFrameConfirmed, header.AckFrame);
    }

    /// <summary>
    /// Encodes and sends the current frame's local input, plus up to 7 preceding
    /// inputs (redundant retransmit), as one RBN1 packet to the remote peer.
    ///
    /// Called after <see cref="RollbackEngine.Tick"/> so <c>CurrentFrame</c> has
    /// already advanced; the just-ticked frame is <c>CurrentFrame - 1</c>.
    ///
    /// Packet layout:
    ///   StartFrame = currentFrame - min(7, currentFrame)
    ///   Count      = min(8, currentFrame + 1)   [1 .. 8]
    ///   AckFrame   = _latestRemoteFrame (highest frame received from peer so far)
    /// </summary>
    private void SendInputPacket()
    {
        if (_udpSocket is null || _remoteEp is null) return;

        // lastFrame = the frame we just ticked; CurrentFrame >= 1 because Tick() was just called
        uint lastFrame  = _engine.CurrentFrame - 1u;
        uint startFrame = lastFrame >= 7u ? lastFrame - 7u : 0u;
        byte count      = (byte)(lastFrame - startFrame + 1u); // 1..8

        // Gather inputs from ring buffer into pre-allocated scratch array
        for (int i = 0; i < count; i++)
            _inputsScratch[i] = _localInputRing[(startFrame + i) & 255];

        var header = new InputPacketHeader
        {
            StartFrame = startFrame,
            // AckFrame: tell remote the highest frame we have received from them
            AckFrame   = _latestRemoteFrame == uint.MaxValue ? 0u : _latestRemoteFrame,
        };

        var pkt     = new InputPacket(header, _inputsScratch, count);
        int written = PacketCodec.Encode(in pkt, _sendBuf.AsSpan());
        _udpSocket.Send(_sendBuf, written, _remoteEp!);
    }

    private void UpdateDebugLabel()
    {
        // Determine PRED/CONF for the frame that was just simulated.
        // Tick() has already incremented CurrentFrame, so the frame we care about
        // is CurrentFrame - 1.  Making this explicit prevents off-by-one "fixes".
        uint justSimulated = _engine.CurrentFrame == 0u ? 0u : _engine.CurrentFrame - 1u;
        bool confirmedForJustSimulated =
            _latestRemoteFrame != uint.MaxValue &&
            _latestRemoteFrame >= justSimulated;

        string remoteStatus    = _latestRemoteFrame == uint.MaxValue ? "N/A "
                               : confirmedForJustSimulated            ? "CONF" : "PRED";
        string latestRemoteTxt = _latestRemoteFrame == uint.MaxValue
                               ? "    ---"
                               : $"{_latestRemoteFrame,7}";

        string lanLines = _mode == DemoMode.Lan
            ? $"LAN State:     {_lanState}\n"
            + (_lanState != LanState.Connected ? "               ⏳ Waiting for peer…\n" : "")
            : "";

        _debugLbl.Text =
            $"Mode:            {(_mode == DemoMode.Offline ? "Offline" : "LAN")}\n"  +
            $"LocalPlayer:     {(_localPlayer == LocalPlayer.P1 ? "P1" : "P2")}\n"   +
            lanLines                                                                   +
            $"Frame:          {_engine.CurrentFrame,7}\n"                             +
            $"LatestRemote:  {latestRemoteTxt}\n"                                     +
            $"Remote:        {remoteStatus,7}\n"                                      +
            $"RollbackCount: {_engine.RollbackCount,7}\n"                             +
            $"MaxRollback:   {_engine.MaxRollbackDepth,7} frames\n"                   +
            $"FramesRolled:  {_engine.RollbackFramesTotal,7}\n"                       +
            $"DelayFrames:   {_delayFrames,7}";
    }

    // ─── Hotkeys ──────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode == Key.F1)
        {
            _overlayVisible   = !_overlayVisible;
            _debugLbl.Visible = _overlayVisible;
        }
        else if (key.Keycode == Key.F2)
        {
            _hudVisible  = !_hudVisible;
            _hud.Visible = _hudVisible;
        }
    }
}
