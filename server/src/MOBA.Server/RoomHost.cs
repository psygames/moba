// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using kcp2k;
using MOBA.Logic.Sim;
using MOBA.Logic.Replay;
using MOBA.Shared.Protocol;

namespace MOBA.Server;

/// <summary>
/// Single-room frame-relay host that owns one <see cref="Room"/>, one
/// <see cref="DeterministicWorld"/> (used only to produce snapshots — server
/// does not authoritative-simulate per PRD §5.3) and one <see cref="KcpServer"/>.
/// Run on a dedicated thread; one instance per game room.
/// </summary>
public sealed class RoomHost
{
    public readonly Room Room;
    public readonly DeterministicWorld World;
    public readonly KcpServer Server;
    public readonly ushort Port;
    public bool LogDesync;
    /// <summary>When true, on hash mismatch the server pushes a fresh Snapshot
    /// to the divergent client (no client request needed). PRD §9.1 recovery hint.</summary>
    public bool AutoResyncOnDesync = true;
    public int AutoResyncCount;

    /// <summary>If non-null, the server records the match into a <c>.mreplay</c>
    /// file at this path on <see cref="Stop"/>. PRD §9.2.
    /// On GameOver, '<c>{stem}.{winner}{ext}</c>' is used (winner = blue/red/none).</summary>
    public string ReplayPath;
    private ReplayWriter _replay;
    private bool _replayHeaderWritten;

    /// <summary>True once the authoritative World reports GameOver. Pump stops broadcasting
    /// new batches and Stop() is safe to call. PRD §9.3 match lifecycle.</summary>
    public bool MatchEnded { get; private set; }
    public Team MatchWinner { get; private set; } = Team.Blue;
    public uint MatchEndFrame { get; private set; }
    public int GameOverBroadcasts;

    /// <summary>Minimum ms between two snapshot pushes to the SAME slot. Prevents
    /// the server from drowning a flapping client when many hash reports arrive in a burst.</summary>
    public long ResyncThrottleMs = 500;
    private readonly long[] _lastResyncMsBySlot = new long[Room.PlayerCount];
    public int ResyncThrottled;

    /// <summary>connectionId -> playerSlot mapping.</summary>
    private readonly Dictionary<int, byte> _slotByConn = new();
    private readonly int[] _connBySlot = new int[Room.PlayerCount];
    /// <summary>Tracks whether each slot has ever joined (survives disconnect).  Used to
    /// distinguish a genuine reconnect from a first-time join so we can auto-push a snapshot.</summary>
    private readonly bool[] _slotEverJoined = new bool[Room.PlayerCount];
    /// <summary>All spectator connection IDs (receive FrameBatch but hold no player slot).</summary>
    private readonly HashSet<int> _spectators = new();
    /// <summary>How many FrameBatch messages were forwarded to spectators.</summary>
    public int SpectatorFramesForwarded;
    public int SpectatorCount => _spectators.Count;
    /// <summary>How many times the server auto-pushed a snapshot on player slot reconnect.</summary>
    public int AutoReconnectPushCount;
    private readonly byte[] _scratch = new byte[1500];
    private readonly System.Buffers.ArrayBufferWriter<byte> _snapBuf = new(2048);

    /// <summary>Wall-clock ms between forced FrameBatch builds when not all 10 inputs have arrived.</summary>
    public long MaxWaitMs = 80; // ~one tick at 15Hz; 80ms keeps Lag at <=2 frames worst case

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastBroadcastMs;
    private bool _running;
    public long LastBroadcastMs => _lastBroadcastMs;
    public bool IsRunning => _running;

    public bool DesyncDetected { get; private set; }
    public uint DesyncFrame { get; private set; }
    public ulong DesyncHashA { get; private set; }
    public ulong DesyncHashB { get; private set; }

    public int JoinRoomCount;
    public int OnConnectedCount;
    public int OnDisconnectedCount;
    public int InputsReceived;
    public string SnapshotsByConn() {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (int i = 0; i < _connBySlot.Length; i++) { if (i>0) sb.Append(','); sb.Append(_connBySlot[i]); }
        sb.Append(']');
        return sb.ToString();
    }

    public RoomHost(uint roomId, ulong seed, ushort port)
    {
        // Server must mirror client gameplay so authoritative snapshots and hash arbitration
        // line up; otherwise auto-resync would push divergent state to clients.
        Items.RegisterDefaults();
        Room  = new Room(roomId, seed);
        World = new DeterministicWorld(seed) { EnableGameplay = true };
        Port  = port;
        for (int i = 0; i < _connBySlot.Length; i++) _connBySlot[i] = 0; // reset; meaningful only when slot is in _slotByConn

        var cfg = new KcpConfig(
            DualMode: false, RecvBufferSize: 1024 * 1024 * 7, SendBufferSize: 1024 * 1024 * 7,
            Mtu: 1200, NoDelay: true, Interval: 10, FastResend: 2, CongestionWindow: false,
            SendWindowSize: 256, ReceiveWindowSize: 256, Timeout: 15000, MaxRetransmits: 40);
        Server = new KcpServer(
            OnConnected:    OnConnected,
            OnData:         OnData,
            OnDisconnected: OnDisconnected,
            OnError:        (id, code, reason) => Console.Error.WriteLine($"[Room{Room.RoomId}] kcp err cid={id} {code} {reason}"),
            config:         cfg);
        Server.Start(port);
    }

    private void OnConnected(int connectionId)
    {
        OnConnectedCount++;
        // Slot is *not* assigned here. The client sends C2S_JoinRoom with its desired slot;
        // OnData performs the assignment so that loopback connect ordering doesn't matter.
    }

    private void OnDisconnected(int connectionId)
    {
        OnDisconnectedCount++;
        if (_slotByConn.TryGetValue(connectionId, out var slot))
        {
            _slotByConn.Remove(connectionId);
            // Don't reset _connBySlot[slot]; presence in _slotByConn is the source of truth.
        }
        _spectators.Remove(connectionId);
    }

    private void OnData(int connectionId, ArraySegment<byte> data, KcpChannel ch)
    {
        if (data.Count < 1) return;
        byte msg = data.Array![data.Offset];
        var span = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);
        switch (msg)
        {
            case MsgId.C2S_JoinRoom:
            {
                JoinRoomCount++;
                MessageCodec.ReadJoinRoom(span, out _, out var playerId, out _);
                byte slot = (byte)playerId;
                if (slot >= Room.PlayerCount) { Server.Disconnect(connectionId); return; }
                // Detect reconnect: room already running AND this slot has joined before.
                bool isReconnect = _running && _slotEverJoined[slot];
                if (_slotByConn.ContainsValue(slot) && _connBySlot[slot] != connectionId)
                {
                    // Old connection on the same slot — evict (this is the reconnect path).
                    int oldCid = _connBySlot[slot];
                    _slotByConn.Remove(oldCid);
                    Server.Disconnect(oldCid);
                }
                _connBySlot[slot] = connectionId;
                _slotByConn[connectionId] = slot;
                _slotEverJoined[slot] = true;
                Span<uint> ids = stackalloc uint[Room.PlayerCount];
                for (int i = 0; i < ids.Length; i++) ids[i] = (uint)i;
                int n = MessageCodec.WriteRoomStart(_scratch, Room.Seed, mapId: 0, tickRate: 15, ids);
                Server.Send(connectionId, new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
                // On reconnect, immediately push current snapshot so the client can catch up
                // without waiting to discover its own lag and send C2S_RequestResync.
                if (isReconnect) { AutoReconnectPushCount++; SendResync(connectionId, slot, 0, bypassThrottle: true); }
                break;
            }
            case MsgId.C2S_SpectateRoom:
            {
                // Spectators observe the room without occupying a player slot.
                _spectators.Add(connectionId);
                Span<byte> ack = stackalloc byte[MessageCodec.SpectateAckSize];
                MessageCodec.WriteSpectateAck(ack, Room.Seed, Room.NextOutputFrame);
                Server.Send(connectionId, new ArraySegment<byte>(ack.ToArray()), KcpChannel.Reliable);
                // Push current snapshot so spectator can jump to present state.
                if (_running) SendSnapshotToConn(connectionId);
                break;
            }
            case MsgId.C2S_Input:
            {
                InputsReceived++;
                MessageCodec.ReadInput(span, out var slot, out var frame, out var input);
                Room.OnInput(slot, frame, input);
                break;
            }
            case MsgId.C2S_HashReport:
            {
                MessageCodec.ReadHashReport(span, out var slot, out var frame, out var hash);
                CheckDesync(slot, frame, hash);
                Room.OnHashReport(slot, frame, hash);
                break;
            }
            case MsgId.C2S_RequestResync:
            {
                MessageCodec.ReadResyncRequest(span, out var slot, out var lastAcked);
                SendResync(connectionId, slot, lastAcked);
                break;
            }
            case MsgId.C2S_BuyItem:
            {
                MessageCodec.ReadBuyItem(span, out var slot, out var itemId);
                // Server is authoritative for BuyItem injection; per-tick last-write-wins.
                // Validate slot/connection match to prevent cross-slot spoofing.
                if (_slotByConn.TryGetValue(connectionId, out var ownerSlot) && ownerSlot == slot)
                    Room.OnBuyItemRequest(slot, itemId);
                break;
            }
        }
    }

    private void CheckDesync(byte slot, uint frame, ulong hash)
    {
        for (int p = 0; p < Room.PlayerCount; p++)
        {
            if (p == slot) continue;
            if (!Room.HasHashReport[p]) continue;          // slot has never reported — don't compare
            if (Room.LastHashFrame[p] == frame && Room.LastHash[p] != hash)
            {
                DesyncDetected = true; DesyncFrame = frame;
                DesyncHashA = Room.LastHash[p]; DesyncHashB = hash;
                if (LogDesync) Console.Error.WriteLine($"[Room{Room.RoomId}] DESYNC frame {frame}: slot {p} = 0x{Room.LastHash[p]:X16}, slot {slot} = 0x{hash:X16}");
                if (AutoResyncOnDesync) TryAutoResync(slot, frame);
                return;
            }
        }
    }

    /// <summary>Force-snapshot the divergent client to pull it back to authoritative state.</summary>
    private void TryAutoResync(byte slot, uint frame)
    {
        // Find this slot's connection (skip silently if it's no longer connected).
        if (slot >= _connBySlot.Length) return;
        int cid = _connBySlot[slot];
        if (!_slotByConn.ContainsKey(cid)) return;
        AutoResyncCount++;
        SendResync(cid, slot, frame);
    }

    private void SendResync(int connectionId, byte slot, uint lastAckedFrame, bool bypassThrottle = false)
    {
        if (slot < _lastResyncMsBySlot.Length)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!bypassThrottle)
            {
                long last = _lastResyncMsBySlot[slot];
                if (last != 0 && now - last < ResyncThrottleMs) { ResyncThrottled++; return; }
            }
            _lastResyncMsBySlot[slot] = _clock.ElapsedMilliseconds;
        }
        SendSnapshotToConn(connectionId);
    }

    /// <summary>Sends the latest snapshot + broadcast tail to <paramref name="connectionId"/>
    /// (used for both player resync and spectator catch-up).</summary>
    private void SendSnapshotToConn(int connectionId)
    {
        var (snapFrame, snapBytes) = Room.LatestSnapshot();
        if (snapBytes == null) return;
        Span<InputFrame> tail = stackalloc InputFrame[Room.BroadcastHistorySize * Room.PlayerCount];
        int copied = Room.CopyBroadcastTail(snapFrame + 1, tail, out int tailFrames);
        if (copied < 0)
        {
            Console.Error.WriteLine($"[Room{Room.RoomId}] resync abort: snapshot frame {snapFrame} too old vs current {Room.NextOutputFrame}");
            return;
        }
        int needed = MessageCodec.SnapshotHeaderSize + snapBytes.Length + tailFrames * Room.PlayerCount * InputFrame.Size;
        byte[] big = needed > _scratch.Length ? new byte[needed + 64] : _scratch;
        int n = MessageCodec.WriteSnapshot(big, snapFrame, snapBytes, (ushort)tailFrames, tail.Slice(0, tailFrames * Room.PlayerCount));
        Server.Send(connectionId, new ArraySegment<byte>(big, 0, n), KcpChannel.Reliable);
    }

    /// <summary>Pumps kcp + room. Call from a tight loop; safe to call many times per ms.</summary>
    public void Pump()
    {
        Server.Tick();

        // Don't start building batches until every slot has joined; otherwise the server's World
        // would advance with empty inputs while clients haven't initialised, leading to instant
        // hash divergence. Once started, the force-fill timer takes over normally.
        if (!_running)
        {
            // _connBySlot stores raw kcp connectionIds which can be negative; use the dict count.
            if (_slotByConn.Count < Room.PlayerCount) return;
            _running = true;
            _lastBroadcastMs = _clock.ElapsedMilliseconds; // reset force timer at start
        }

        long now = _clock.ElapsedMilliseconds;
        long sinceLast = now - _lastBroadcastMs;
        bool force = sinceLast >= MaxWaitMs;

        // Lazily start replay recording right after _running flips to true: snapshot of frame 0
        // captures the deterministic initial state before any input is applied.
        if (ReplayPath != null && !_replayHeaderWritten)
        {
            BeginReplay();
            _replayHeaderWritten = true;
        }

        // Build & broadcast as many frames as possible this pump.
        while (true)
        {
            if (MatchEnded) break;
            int written;
            if (!Room.TryBuildBatch(force, _scratch, out written)) break;
            BroadcastAll(_scratch, written);
            _lastBroadcastMs = now;

            // After broadcasting, advance the world for snapshotting cadence.
            // Server applies the SAME inputs the clients receive, so snapshots stay authoritative.
            // We re-read those inputs from the just-broadcast batch.
            ApplyBroadcastToWorld(_scratch, written);

            if (Room.ShouldSnapshot()) TakeSnapshot();
            // After first force-fill, don't keep forcing for the same tick.
            force = false;

            if (World.GameOver && !MatchEnded) BroadcastGameOver();
        }
    }

    private void ApplyBroadcastToWorld(byte[] batch, int len)
    {
        MessageCodec.ReadFrameBatchHeader(batch, out var startFrame, out var count);
        Span<InputFrame> tmp = stackalloc InputFrame[Room.PlayerCount];
        for (int p = 0; p < Room.PlayerCount; p++)
            tmp[p] = MessageCodec.ReadFrameBatchInput(new ReadOnlySpan<byte>(batch, 0, len), p);
        World.Tick(tmp);
        if (_replay != null && _replayHeaderWritten) _replay.RecordTick(tmp);
    }

    private void BeginReplay()
    {
        _replay = new ReplayWriter();
        _snapBuf.Clear();
        World.WriteSnapshot(_snapBuf); // pre-tick state = frame-0 snapshot
        Span<byte> slots = stackalloc byte[Room.PlayerCount];
        for (int i = 0; i < Room.PlayerCount; i++) slots[i] = (byte)i;
        ulong startUnix = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _replay.BeginRecording(mapId: 0, Room.Seed, slots, startUnix, _snapBuf.WrittenSpan);
    }

    private void TakeSnapshot()
    {
        _snapBuf.Clear();
        World.WriteSnapshot(_snapBuf);
        Room.StoreSnapshot(_snapBuf.WrittenSpan);
    }

    private void BroadcastGameOver()
    {
        MatchEnded = true;
        MatchWinner = World.Winner;
        MatchEndFrame = World.Frame;
        uint durationSec = MatchEndFrame / 15u;
        Span<byte> buf = stackalloc byte[MessageCodec.GameOverSize];
        MessageCodec.WriteGameOver(buf, (byte)MatchWinner, MatchEndFrame, durationSec);
        var arr = buf.ToArray();
        var seg = new ArraySegment<byte>(arr);
        for (int i = 0; i < _connBySlot.Length; i++)
        {
            if (!_slotByConn.ContainsValue((byte)i)) continue;
            Server.Send(_connBySlot[i], seg, KcpChannel.Reliable);
        }
        foreach (int cid in _spectators) Server.Send(cid, seg, KcpChannel.Reliable);
        GameOverBroadcasts++;
    }

    private void BroadcastAll(byte[] batch, int len)
    {
        var seg = new ArraySegment<byte>(batch, 0, len);
        for (int i = 0; i < _connBySlot.Length; i++)
        {
            if (!_slotByConn.ContainsValue((byte)i)) continue;
            int cid = _connBySlot[i];
            Server.Send(cid, seg, KcpChannel.Reliable);
        }
        foreach (int cid in _spectators)
        {
            Server.Send(cid, seg, KcpChannel.Reliable);
            SpectatorFramesForwarded++;
        }
    }

    public void Stop()
    {
        if (_replay != null && _replayHeaderWritten && ReplayPath != null)
        {
            try
            {
                var span = _replay.Finish();
                string outPath = ReplayPath;
                if (MatchEnded)
                {
                    string suffix = MatchWinner == Team.Blue ? "blue" : MatchWinner == Team.Red ? "red" : "none";
                    string dir = Path.GetDirectoryName(ReplayPath) ?? ".";
                    string stem = Path.GetFileNameWithoutExtension(ReplayPath);
                    string ext = Path.GetExtension(ReplayPath);
                    outPath = Path.Combine(dir, $"{stem}.{suffix}{ext}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
                File.WriteAllBytes(outPath, span.ToArray());
                ReplayPath = outPath;
            }
            catch (Exception e) { Console.Error.WriteLine($"[Room{Room.RoomId}] replay save failed: {e.Message}"); }
        }
        Server.Stop();
    }
}
