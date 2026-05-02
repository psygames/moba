// SPDX-License-Identifier: MIT
using System;
using System.Buffers;
using MOBA.Shared.Protocol;

namespace MOBA.Server;

/// <summary>
/// Per-room authoritative frame relay (PRD §5.3). Server itself does not run
/// physics; it collects 10 player inputs per logic frame, fills missing slots
/// with <see cref="InputFrame.Empty"/>, broadcasts <c>S2C_FrameBatch</c>, and
/// every <see cref="SnapshotInterval"/> frames asks the supplied snapshot
/// callback for a checkpoint kept in a ring buffer (used by reconnect).
/// </summary>
public sealed class Room
{
    public const int PlayerCount  = 10;
    public const int TickHz       = 15;
    public const int SnapshotInterval = 60;
    public const int SnapshotRing = 8;
    public const int InputBufferSize = 256; // ring of upcoming frames per player

    public readonly uint RoomId;
    public readonly ulong Seed;
    public uint NextOutputFrame;        // first frame not yet broadcast

    /// <summary>Input ring per player. Index = frame % InputBufferSize.</summary>
    private readonly InputFrame[,] _inputs = new InputFrame[PlayerCount, InputBufferSize];
    private readonly bool[,] _filled = new bool[PlayerCount, InputBufferSize];

    /// <summary>Last hash report received per player, for desync detection.</summary>
    public readonly ulong[] LastHash = new ulong[PlayerCount];
    public readonly uint[]  LastHashFrame = new uint[PlayerCount];
    public readonly bool[]  HasHashReport = new bool[PlayerCount];

    private readonly InputFrame[] _broadcastBuffer = new InputFrame[PlayerCount];

    /// <summary>
    /// Pending BuyItemId per slot (in wire form, i.e. <c>itemDefIdx + 1</c>).
    /// Set by <see cref="OnBuyItemRequest"/> when the server receives <c>C2S_BuyItem</c>;
    /// drained into the next broadcast frame's <see cref="InputFrame.BuyItemId"/> for that slot.
    /// Only the latest request per slot is honoured (newer overwrites older).
    /// </summary>
    private readonly ushort[] _pendingBuy = new ushort[PlayerCount];
    public int BuyItemRequestsReceived { get; private set; }
    public int BuyItemRequestsInjected { get; private set; }

    /// <summary>Snapshot ring: frame -> bytes. Newest at <c>(_snapHead - 1) % ring</c>.</summary>
    public readonly uint[]   SnapshotFrames  = new uint[SnapshotRing];
    public readonly byte[][] SnapshotBuffers = new byte[SnapshotRing][];
    private int _snapHead;
    public int  SnapshotsTaken { get; private set; }

    /// <summary>
    /// Rolling history of broadcast inputs, indexed by frame % BroadcastHistorySize.
    /// Used to fill the resync tail so a reconnecting client can catch up to current.
    /// </summary>
    public const int BroadcastHistorySize = 256;
    private readonly InputFrame[,] _broadcastHistory = new InputFrame[BroadcastHistorySize, PlayerCount];
    private readonly bool[] _broadcastHistoryFilled = new bool[BroadcastHistorySize];

    public Room(uint roomId, ulong seed)
    {
        RoomId = roomId; Seed = seed;
    }

    /// <summary>Records an input from <paramref name="playerSlot"/> for <paramref name="frame"/>.</summary>
    public void OnInput(byte playerSlot, uint frame, in InputFrame f)
    {
        if (playerSlot >= PlayerCount) return;
        if (frame < NextOutputFrame) return; // late, drop
        if (frame >= NextOutputFrame + InputBufferSize) return; // far future, drop
        int idx = (int)(frame % InputBufferSize);
        _inputs[playerSlot, idx] = f;
        _filled[playerSlot, idx] = true;
    }

    public void OnHashReport(byte playerSlot, uint frame, ulong hash)
    {
        if (playerSlot >= PlayerCount) return;
        LastHash[playerSlot] = hash;
        LastHashFrame[playerSlot] = frame;
        HasHashReport[playerSlot] = true;
    }

    /// <summary>
    /// Records a buy request from <paramref name="playerSlot"/> for wire-form
    /// <paramref name="itemIdWire"/> (= itemDefIdx + 1). 0 is treated as cancel.
    /// Will be injected into the next broadcast frame's <c>InputFrame.BuyItemId</c>.
    /// Last-write-wins per slot.
    /// </summary>
    public void OnBuyItemRequest(byte playerSlot, ushort itemIdWire)
    {
        if (playerSlot >= PlayerCount) return;
        _pendingBuy[playerSlot] = itemIdWire;
        BuyItemRequestsReceived++;
    }

    /// <summary>
    /// Returns true and writes a <c>S2C_FrameBatch</c> for exactly one frame into <paramref name="dst"/>
    /// when the next output frame is "ready". Ready = either every player slot has filled the frame OR
    /// <paramref name="forceMs"/> says we've waited too long (Lag-fill with empty).
    /// </summary>
    public bool TryBuildBatch(bool force, Span<byte> dst, out int written)
    {
        written = 0;
        uint frame = NextOutputFrame;
        int idx = (int)(frame % InputBufferSize);
        bool allReady = true;
        for (int p = 0; p < PlayerCount; p++)
            if (!_filled[p, idx]) { allReady = false; break; }
        if (!allReady && !force) return false;

        for (int p = 0; p < PlayerCount; p++)
        {
            _broadcastBuffer[p] = _filled[p, idx] ? _inputs[p, idx] : InputFrame.Empty;
            _filled[p, idx] = false; // consume so next wrap is empty
            _inputs[p, idx] = default;
            // Server is authoritative for BuyItemId — overwrite whatever the client put on the wire
            // (per InputFrame contract: "Server-injected; ignored on C2S input").
            ushort buy = _pendingBuy[p];
            _broadcastBuffer[p].BuyItemId = buy;
            if (buy != 0) BuyItemRequestsInjected++;
            _pendingBuy[p] = 0;
        }
        // Save into broadcast history before writing the wire batch.
        int hidx = (int)(frame % BroadcastHistorySize);
        for (int p = 0; p < PlayerCount; p++) _broadcastHistory[hidx, p] = _broadcastBuffer[p];
        _broadcastHistoryFilled[hidx] = true;

        written = MessageCodec.WriteFrameBatch(dst, frame, 1, _broadcastBuffer);
        NextOutputFrame = frame + 1;
        return true;
    }

    /// <summary>Indicates whether a snapshot should be taken at the just-broadcast frame.</summary>
    public bool ShouldSnapshot() => (NextOutputFrame - 1) % SnapshotInterval == 0;

    /// <summary>Stores a snapshot for the most recently broadcast frame.</summary>
    public void StoreSnapshot(ReadOnlySpan<byte> bytes)
    {
        uint frame = NextOutputFrame - 1;
        SnapshotFrames[_snapHead]  = frame;
        var buf = SnapshotBuffers[_snapHead];
        if (buf == null || buf.Length < bytes.Length)
            buf = new byte[bytes.Length];
        bytes.CopyTo(buf);
        SnapshotBuffers[_snapHead] = buf;
        _snapHead = (_snapHead + 1) % SnapshotRing;
        SnapshotsTaken++;
    }

    /// <summary>Latest stored snapshot, or (0, null) if none.</summary>
    public (uint frame, byte[] bytes) LatestSnapshot()
    {
        if (SnapshotsTaken == 0) return (0, null);
        int last = (_snapHead - 1 + SnapshotRing) % SnapshotRing;
        return (SnapshotFrames[last], SnapshotBuffers[last]);
    }

    /// <summary>
    /// Copies the broadcast inputs for frames [<paramref name="fromFrame"/> .. NextOutputFrame-1]
    /// into <paramref name="dst"/> in row-major order (frame * PlayerCount + slot).
    /// Returns the number of frames written. Returns -1 if any required frame has been evicted.
    /// </summary>
    public int CopyBroadcastTail(uint fromFrame, Span<InputFrame> dst, out int frameCount)
    {
        frameCount = 0;
        if (fromFrame >= NextOutputFrame) return 0;
        uint span = NextOutputFrame - fromFrame;
        if (span > BroadcastHistorySize) return -1;
        if (dst.Length < span * PlayerCount) return -1;
        for (uint f = 0; f < span; f++)
        {
            uint frame = fromFrame + f;
            int hidx = (int)(frame % BroadcastHistorySize);
            if (!_broadcastHistoryFilled[hidx]) return -1;
            for (int p = 0; p < PlayerCount; p++)
                dst[(int)(f * PlayerCount + p)] = _broadcastHistory[hidx, p];
        }
        frameCount = (int)span;
        return frameCount * PlayerCount;
    }
}
