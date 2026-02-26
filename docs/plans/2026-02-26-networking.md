# Godot Demo LAN Networking (UDP + PacketCodec) — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extend `game/RollbackDemo.cs` with real UDP networking — a minimal 3-way ASCII handshake (HELLO/ACK/START) that advances both LAN peers to `LanState.Connected`, followed by `PacketCodec`-encoded RBN1 input packets that drive `RollbackEngine.SetRemoteInput` on each side.

**Architecture:** Single-file extension of `game/RollbackDemo.cs`. `UdpClient` opened in `OnHost`/`OnJoin`, closed on disconnect/mode-switch. All network I/O is polled non-blocking inside `_PhysicsProcess` (no threads). Handshake: Joiner sends `HELLO` every 30 frames; Host replies `ACK`; Joiner replies `START`; Host receives `START` → both `Connected`. Gameplay: local inputs stored in a 256-slot ring buffer; each tick sends last 8 inputs as one redundant RBN1 packet; receive path calls `PacketCodec.TryDecodeInto` + `SetRemoteInput`. No changes to any `Core.*` project.

**Port topology:** Host listens on `_port` (default 7777), Joiner listens on `_port + 1` (7778). Both sides set the same port value in the UI. On the same machine both use `127.0.0.1`; on different LAN machines each sets `_remoteIp` to the other's address.

**Tech Stack:** Godot 4.6.1 / C# / .NET 8, `game/game.csproj`, `Core.Net.PacketCodec`

---

### Task 1: Add Core.Net reference + networking fields + socket lifecycle

**Files:**
- Modify: `game/game.csproj`
- Modify: `game/RollbackDemo.cs`

Adds the `Core.Net` project reference, all new networking fields, the `CloseSocket()` helper, and wires socket open/close into the existing callback methods.  No network I/O yet — the simulation gate ensures no behaviour change.

---

**Step 1: Add `Core.Net` project reference to `game/game.csproj`**

Open `game/game.csproj`. Add one line inside the existing `<ItemGroup>`:

```xml
<ProjectReference Include="../src/Core.Net/Core.Net.csproj" />
```

Full file after edit:

```xml
<Project Sdk="Godot.NET.Sdk/4.6.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/Core.Sim/Core.Sim.csproj" />
    <ProjectReference Include="../src/Core.Rollback/Core.Rollback.csproj" />
    <ProjectReference Include="../src/Core.Net/Core.Net.csproj" />
  </ItemGroup>
</Project>
```

---

**Step 2: Add `using Core.Net;` to RollbackDemo.cs**

The file currently begins:

```csharp
using Godot;
using Core.Sim;
using Core.Rollback;
using System.Collections.Generic;
```

Replace with:

```csharp
using Godot;
using Core.Net;
using Core.Sim;
using Core.Rollback;
using System.Collections.Generic;
```

`System.Net` and `System.Net.Sockets` types are referenced with fully-qualified names throughout to avoid any ambiguity with Godot networking types.

---

**Step 3: Add networking fields to RollbackDemo.cs**

After the `_simulationRunning` field (the last field in the "Mode + LAN connection state" block), insert a new field group:

```csharp
// ─── LAN networking ───────────────────────────────────────────────────────

/// <summary>UDP socket; null when disconnected.</summary>
private System.Net.Sockets.UdpClient?  _udpSocket              = null;

/// <summary>Remote peer endpoint; set in OnHost/OnJoin.</summary>
private System.Net.IPEndPoint?         _remoteEp               = null;

/// <summary>Pre-allocated send buffer — avoids per-tick allocation.</summary>
private readonly byte[]                _sendBuf                = new byte[PacketCodec.MaxPacketSize];

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
```

---

**Step 4: Add `CloseSocket()` helper**

Add the following private method after `OnDisconnect()` (i.e., after the closing `}` of `OnDisconnect`):

```csharp
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
```

---

**Step 5: Open socket in `OnHost()`**

In the existing `OnHost()` method, replace the body (keeping the same summary comment) with the version below.  The only additions are the two socket lines and `CloseSocket()` defensive guard:

```csharp
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
```

---

**Step 6: Open socket in `OnJoin()`**

Similarly replace `OnJoin()`:

```csharp
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
```

---

**Step 7: Close socket in `OnDisconnect()` and `SwitchMode()`**

In `OnDisconnect()`, add `CloseSocket();` as the **first** statement in the method body:

```csharp
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
```

In `SwitchMode()`, inside the `if (newMode == DemoMode.Offline)` branch, add `CloseSocket();` as the **first** statement:

```csharp
if (newMode == DemoMode.Offline)
{
    CloseSocket();
    _lanState          = LanState.Disconnected;
    _localPlayer       = LocalPlayer.P1;
    _simulationRunning = true;
    _engine                = new RollbackEngine(SimState.CreateInitial(Seed), HistoryCap, _localPlayer);
    _lagBuffer.Clear();
    _latestRemoteFrame     = uint.MaxValue;
    _hostBtn.Disabled      = false;
    _joinBtn.Disabled      = false;
    _disconnectBtn.Disabled = true;
}
```

The `else // Lan` branch does not open a socket (socket opens only on explicit Host/Join).

---

**Step 8: Build — expect 0 errors, 0 warnings**

```bash
dotnet build game/game.csproj -c Release
```

Expected: `Build succeeded.` — 0 error(s), 0 warning(s).

---

**Step 9: Commit**

```bash
git add game/game.csproj game/RollbackDemo.cs
git commit -m "feat(game): add Core.Net ref, UDP socket fields, and socket lifecycle"
```

---

### Task 2: Handshake — HELLO / ACK / START → Connected

**Files:**
- Modify: `game/RollbackDemo.cs`

Implements the 3-way ASCII handshake and the `TransitionToConnected()` helper.  After this task, two running instances can reach `LanState.Connected` without any gameplay yet.

**Handshake state machine recap:**
- `Hosting`: waits for `HELLO` → sends `ACK` → waits for `START` → `Connected`
- `Joining`: sends `HELLO` every 30 frames → on `ACK` sends `START` + `Connected`

---

**Step 1: Add `IsMessage` static helper**

Add after `CloseSocket()`:

```csharp
/// <summary>
/// Returns true when <paramref name="raw"/> contains exactly the bytes of
/// <paramref name="expected"/> (ASCII literal, e.g. <c>"HELLO"u8</c>).
/// </summary>
private static bool IsMessage(byte[] raw, System.ReadOnlySpan<byte> expected) =>
    raw.Length == expected.Length && raw.AsSpan().SequenceEqual(expected);
```

---

**Step 2: Add `TransitionToConnected()` helper**

Add after `IsMessage`:

```csharp
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
```

---

**Step 3: Add `ReceiveAll()` and `ProcessHandshakePacket()` methods**

Add after `TransitionToConnected()`:

```csharp
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
        _udpSocket!.Send(_ackBytes, _ackBytes.Length, _remoteEp);
        return;
    }

    // Joiner receives ACK from Host → reply START + go Connected
    if (_lanState == LanState.Joining && IsMessage(raw, "ACK"u8))
    {
        _udpSocket!.Send(_startBytes, _startBytes.Length, _remoteEp);
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
    // (populated in Task 3 — packets received before that task are silently ignored)
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
    var dst = new FrameInput[PacketCodec.MaxInputsPerPacket];
    if (!PacketCodec.TryDecodeInto(raw.AsSpan(), dst.AsSpan(), out var header, out int count))
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
```

---

**Step 4: Integrate `ReceiveAll()` and periodic HELLO into `_PhysicsProcess`**

Replace the entire `_PhysicsProcess` method:

```csharp
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
        // LAN Connected path — Task 3 will complete this branch
        FrameInput localInput = ReadP1Input(); // same keys for P1 or P2 role
        _localInputRing[f & 255] = localInput;
        _engine.Tick(localInput);
        // SendInputPacket() — added in Task 3
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
```

> **Note:** `ReadP1Input()` reads the same keyboard keys (A / D / Space / J) regardless of whether the local player is P1 or P2.  The engine's `MapInputs` helper handles the routing internally.

---

**Step 5: Build — expect 0 errors, 0 warnings**

```bash
dotnet build game/game.csproj -c Release
```

Expected: `Build succeeded.` — 0 error(s), 0 warning(s).

---

**Step 6: Commit**

```bash
git add game/RollbackDemo.cs
git commit -m "feat(game): add UDP handshake (HELLO/ACK/START) and TransitionToConnected"
```

---

### Task 3: Gameplay packets — redundant RBN1 send + _PhysicsProcess LAN path

**Files:**
- Modify: `game/RollbackDemo.cs`

Adds `SendInputPacket()` (redundant RBN1 send with last 8 inputs) and completes the LAN Connected branch in `_PhysicsProcess`.  After this task, both peers can exchange inputs and `RollbackEngine.SetRemoteInput` runs on received data.

---

**Step 1: Add `SendInputPacket()` method**

Add after `ProcessGameplayPacket()`:

```csharp
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

    // lastFrame = the frame we just ticked (CurrentFrame is already +1)
    uint lastFrame  = _engine.CurrentFrame == 0u ? 0u : _engine.CurrentFrame - 1u;
    uint startFrame = lastFrame >= 7u ? lastFrame - 7u : 0u;
    byte count      = (byte)(lastFrame - startFrame + 1u); // 1..8

    // Gather inputs from ring buffer
    var inputs = new FrameInput[count];
    for (int i = 0; i < count; i++)
        inputs[i] = _localInputRing[(startFrame + i) & 255];

    var header = new InputPacketHeader
    {
        StartFrame = startFrame,
        // AckFrame: tell remote the highest frame we have received from them
        AckFrame   = _latestRemoteFrame == uint.MaxValue ? 0u : _latestRemoteFrame,
    };

    var pkt     = new InputPacket(header, inputs, count);
    int written = PacketCodec.Encode(in pkt, _sendBuf.AsSpan());
    _udpSocket.Send(_sendBuf, written, _remoteEp);
}
```

---

**Step 2: Complete the LAN Connected branch in `_PhysicsProcess`**

In `_PhysicsProcess`, in the `if (_mode == DemoMode.Lan)` block, replace the body (including the `// SendInputPacket() — added in Task 3` comment) with the final version:

```csharp
if (_mode == DemoMode.Lan)
{
    // LAN Connected path: keyboard → engine → network
    FrameInput localInput    = ReadP1Input(); // same keys for P1 or P2 role
    _localInputRing[f & 255] = localInput;
    _engine.Tick(localInput);
    SendInputPacket();
}
```

The full `_PhysicsProcess` method now looks like:

```csharp
public override void _PhysicsProcess(double delta)
{
    // ── LAN: poll socket every tick regardless of _simulationRunning ──────
    if (_mode == DemoMode.Lan && _udpSocket is not null)
    {
        ReceiveAll();

        if (_lanState == LanState.Joining)
        {
            if (_helloSendTimer % 30 == 0)
                _udpSocket.Send(_helloBytes, _helloBytes.Length, _remoteEp);
            _helloSendTimer++;
        }
    }

    // ── Physics gate ──────────────────────────────────────────────────────
    if (!_simulationRunning)
    {
        UpdateDebugLabel();
        QueueRedraw();
        return;
    }

    uint f = _engine.CurrentFrame;

    if (_mode == DemoMode.Lan)
    {
        FrameInput localInput    = ReadP1Input();
        _localInputRing[f & 255] = localInput;
        _engine.Tick(localInput);
        SendInputPacket();
    }
    else
    {
        FrameInput p1 = ReadP1Input();
        EnqueueP2(f);
        DeliverDue(f);
        _engine.Tick(p1);
    }

    UpdateDebugLabel();
    QueueRedraw();
}
```

---

**Step 3: Build — expect 0 errors, 0 warnings**

```bash
dotnet build game/game.csproj -c Release
```

Expected: `Build succeeded.` — 0 error(s), 0 warning(s).

---

**Step 4: Run full test suite — verify 78 tests still pass**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=minimal"
```

Expected: **78 passed, 0 failed, 0 skipped**

---

**Step 5: Commit**

```bash
git add game/RollbackDemo.cs
git commit -m "feat(game): add redundant RBN1 send/receive and complete LAN Connected path"
```

---

### Task 4: Final verification + push + tag

**Step 1: Full game build with warnings-as-errors**

```bash
dotnet build game/game.csproj -c Release -warnaserror
```

Expected: Build succeeded, 0 errors, 0 warnings.

**Step 2: Full solution build — verify Core.* projects unaffected**

```bash
dotnet build RollbackPlayground.sln -warnaserror
```

Expected: Build succeeded, all projects 0 errors, 0 warnings.

**Step 3: Run full test suite — verify 78 tests still pass**

```bash
dotnet test RollbackPlayground.sln --logger "console;verbosity=minimal"
```

Expected: **78 passed, 0 failed, 0 skipped**

**Step 4: Push commits**

```bash
git push
```

**Step 5: Tag and push**

```bash
git tag v0.3-2-task3
git push origin v0.3-2-task3
```
