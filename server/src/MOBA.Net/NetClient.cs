// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using kcp2k;
using MOBA.Shared.Protocol;

namespace MOBA.Net;

/// <summary>
/// kcp2k-based client. Sends one <c>C2S_Input</c> per logic frame and exposes
/// received <c>S2C_FrameBatch</c> entries via a FIFO queue (for the consumer's
/// world tick). Supports <c>S2C_RoomStart</c> and <c>S2C_Snapshot</c>.
/// Single-threaded; the consumer must call <see cref="Tick"/> from its own
/// loop.
/// </summary>
public sealed class NetClient
{
    public readonly KcpClient Client;
    public byte PlayerSlot = byte.MaxValue;
    public ulong Seed;
    public uint NextExpectedFrame;            // next frame number we want to consume
    public bool Connected;
    public bool RoomStarted;

    /// <summary>Queue of (frame, 10×InputFrame) pairs. Frames arrive in order over reliable channel.</summary>
    public readonly Queue<(uint frame, InputFrame[] inputs)> RxFrames = new();
    /// <summary>Snapshot received from server (frame, snapshot bytes).</summary>
    public (uint frame, byte[] bytes)? PendingSnapshot;

    /// <summary>Set when the server pushes <c>S2C_GameOver</c>. Consumers should stop sending input.</summary>
    public bool MatchEnded;
    public byte MatchWinner;
    public uint MatchEndFrame;
    public uint MatchDurationSec;

    private readonly byte[] _scratch = new byte[1500];

    public NetClient()
    {
        var cfg = new KcpConfig(
            DualMode: false, RecvBufferSize: 1024 * 1024 * 7, SendBufferSize: 1024 * 1024 * 7,
            Mtu: 1200, NoDelay: true, Interval: 10, FastResend: 2, CongestionWindow: false,
            SendWindowSize: 256, ReceiveWindowSize: 256, Timeout: 15000, MaxRetransmits: 40);
        Client = new KcpClient(
            OnConnected:    OnConnectedInternal,
            OnData:         OnData,
            OnDisconnected: () => { Connected = false; RoomStarted = false; },
            OnError:        (code, reason) => Console.Error.WriteLine($"[NetClient] kcp err {code} {reason}"),
            config:         cfg);
    }

    private void OnConnectedInternal()
    {
        Connected = true;
        // Auto-join with PlayerSlot as the playerId so the server's slot table maps correctly.
        if (PlayerSlot != byte.MaxValue) SendJoinRoom(roomId: 1, playerId: PlayerSlot);
    }

    public void Connect(string host, ushort port) => Client.Connect(host, port);
    public void Disconnect() => Client.Disconnect();
    public void Tick() => Client.Tick();

    public void SendJoinRoom(uint roomId, uint playerId)
    {
        int n = MessageCodec.WriteJoinRoom(_scratch, roomId, playerId, ReadOnlySpan<byte>.Empty);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    public void SendInput(uint frame, in InputFrame f)
    {
        if (PlayerSlot == byte.MaxValue) return;
        int n = MessageCodec.WriteInput(_scratch, PlayerSlot, frame, f);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    public void SendHashReport(uint frame, ulong hash)
    {
        if (PlayerSlot == byte.MaxValue) return;
        int n = MessageCodec.WriteHashReport(_scratch, PlayerSlot, frame, hash);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    public void SendResyncRequest(uint lastAckedFrame)
    {
        if (PlayerSlot == byte.MaxValue) return;
        int n = MessageCodec.WriteResyncRequest(_scratch, PlayerSlot, lastAckedFrame);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    /// <summary>Send <c>C2S_BuyItem</c> for the local slot. <paramref name="itemIdWire"/>
    /// is the wire-form id (= itemDefIdx + 1); 0 cancels any pending request.</summary>
    public void SendBuyItem(ushort itemIdWire)
    {
        if (PlayerSlot == byte.MaxValue) return;
        int n = MessageCodec.WriteBuyItem(_scratch, PlayerSlot, itemIdWire);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    /// <summary>Request spectator access to the room. Server will acknowledge with
    /// <c>S2C_SpectateAck</c> followed by a snapshot push. Use when
    /// <see cref="PlayerSlot"/> is <see cref="byte.MaxValue"/> (no player slot).</summary>
    public void SendSpectateRoom(uint roomId)
    {
        int n = MessageCodec.WriteSpectateRoom(_scratch, roomId);
        Client.Send(new ArraySegment<byte>(_scratch, 0, n), KcpChannel.Reliable);
    }

    /// <summary>True once <c>S2C_SpectateAck</c> is received from the server.</summary>
    public bool IsSpectating;
    public uint SpectateFrame;

    private void OnData(ArraySegment<byte> data, KcpChannel ch)
    {
        if (data.Count < 1) return;
        byte msg = data.Array![data.Offset];
        var span = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);
        switch (msg)
        {
            case MsgId.S2C_RoomStart:
            {
                Span<uint> ids = stackalloc uint[MessageCodec.PlayerCount];
                MessageCodec.ReadRoomStart(span, out Seed, out _, out _, ids);
                // Slot is implicit: server assigns the slot at connect time.
                // We learn ours from the first matching id, or default to 0 in single-room test.
                // For the test, the consumer assigns PlayerSlot externally before calling SendInput.
                RoomStarted = true;
                break;
            }
            case MsgId.S2C_FrameBatch:
            {
                MessageCodec.ReadFrameBatchHeader(span, out var startFrame, out var count);
                for (int f = 0; f < count; f++)
                {
                    var inputs = new InputFrame[MessageCodec.PlayerCount];
                    for (int p = 0; p < MessageCodec.PlayerCount; p++)
                        inputs[p] = MessageCodec.ReadFrameBatchInput(span, f * MessageCodec.PlayerCount + p);
                    RxFrames.Enqueue((startFrame + (uint)f, inputs));
                }
                break;
            }
            case MsgId.S2C_Snapshot:
            {
                MessageCodec.ReadSnapshotHeader(span, out var snapFrame, out var snapLen, out var following, out var snapOff, out var tailOff);
                var bytes = new byte[snapLen];
                span.Slice(snapOff, snapLen).CopyTo(bytes);
                PendingSnapshot = (snapFrame, bytes);
                // Drop any stale broadcasts the client already enqueued before the snapshot
                // was applied; the consumer rebuilds World from snapshot + tail.
                RxFrames.Clear();
                // Tail = inputs for frames (snapFrame+1 .. snapFrame+following). Enqueue them so
                // the consumer can replay forward from the snapshot frame.
                var tailSpan = span.Slice(tailOff);
                for (int f = 0; f < following; f++)
                {
                    var inputs = new InputFrame[MessageCodec.PlayerCount];
                    for (int p = 0; p < MessageCodec.PlayerCount; p++)
                        inputs[p] = InputFrame.Read(tailSpan.Slice((f * MessageCodec.PlayerCount + p) * InputFrame.Size, InputFrame.Size));
                    RxFrames.Enqueue((snapFrame + 1 + (uint)f, inputs));
                }
                break;
            }
            case MsgId.S2C_GameOver:
            {
                MessageCodec.ReadGameOver(span, out var winner, out var endFrame, out var durSec);
                MatchEnded = true; MatchWinner = winner;
                MatchEndFrame = endFrame; MatchDurationSec = durSec;
                break;
            }
            case MsgId.S2C_SpectateAck:
            {
                MessageCodec.ReadSpectateAck(span, out var seed, out var frame);
                Seed = seed;
                IsSpectating = true; SpectateFrame = frame;
                break;
            }
        }
    }
}
