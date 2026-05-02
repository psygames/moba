// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;

namespace MOBA.Shared.Protocol;

/// <summary>
/// Wire layout helpers shared by all messages. Each message is prefixed with one
/// <see cref="MsgId"/> byte; the rest is little-endian fixed-size fields followed
/// by length-prefixed payloads.
/// </summary>
public static class MessageCodec
{
    public const int PlayerCount = 10;

    // -------- C2S_JoinRoom : msgId(1) | roomId(4) | playerId(4) | tokenLen(2) | token[]
    public static int WriteJoinRoom(Span<byte> dst, uint roomId, uint playerId, ReadOnlySpan<byte> token)
    {
        dst[0] = MsgId.C2S_JoinRoom;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(1, 4), roomId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(5, 4), playerId);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(9, 2), (ushort)token.Length);
        token.CopyTo(dst.Slice(11));
        return 11 + token.Length;
    }
    public static void ReadJoinRoom(ReadOnlySpan<byte> src, out uint roomId, out uint playerId, out ReadOnlySpan<byte> token)
    {
        roomId   = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(1, 4));
        playerId = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(5, 4));
        int len  = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(9, 2));
        token    = src.Slice(11, len);
    }

    // -------- S2C_RoomStart : msgId(1) | seed(8) | mapId(4) | rate(1) | playerSlots[10]*4
    public static int WriteRoomStart(Span<byte> dst, ulong seed, uint mapId, byte tickRate, ReadOnlySpan<uint> playerIds)
    {
        if (playerIds.Length != PlayerCount) throw new ArgumentException("need 10 ids");
        dst[0] = MsgId.S2C_RoomStart;
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(1, 8), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(9, 4), mapId);
        dst[13] = tickRate;
        for (int i = 0; i < PlayerCount; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(14 + i * 4, 4), playerIds[i]);
        return 14 + PlayerCount * 4;
    }
    public static void ReadRoomStart(ReadOnlySpan<byte> src, out ulong seed, out uint mapId, out byte tickRate, Span<uint> playerIds)
    {
        seed     = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(1, 8));
        mapId    = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(9, 4));
        tickRate = src[13];
        for (int i = 0; i < PlayerCount; i++)
            playerIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(14 + i * 4, 4));
    }

    // -------- C2S_Input : msgId(1) | playerSlot(1) | frame(4) | InputFrame(10)
    public const int InputSize = 1 + 1 + 4 + InputFrame.Size; // 16
    public static int WriteInput(Span<byte> dst, byte playerSlot, uint frame, in InputFrame f)
    {
        dst[0] = MsgId.C2S_Input;
        dst[1] = playerSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), frame);
        f.Write(dst.Slice(6, InputFrame.Size));
        return InputSize;
    }
    public static void ReadInput(ReadOnlySpan<byte> src, out byte playerSlot, out uint frame, out InputFrame f)
    {
        playerSlot = src[1];
        frame      = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(2, 4));
        f          = InputFrame.Read(src.Slice(6, InputFrame.Size));
    }

    // -------- S2C_FrameBatch : msgId(1) | startFrame(4) | frameCount(2) | InputFrame[count*10]
    public static int FrameBatchSize(int frameCount) => 1 + 4 + 2 + frameCount * PlayerCount * InputFrame.Size;

    public static int WriteFrameBatch(Span<byte> dst, uint startFrame, ushort frameCount, ReadOnlySpan<InputFrame> frames)
    {
        if (frames.Length != frameCount * PlayerCount) throw new ArgumentException("frame count mismatch");
        dst[0] = MsgId.S2C_FrameBatch;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(1, 4), startFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(5, 2), frameCount);
        for (int i = 0; i < frames.Length; i++)
            frames[i].Write(dst.Slice(7 + i * InputFrame.Size, InputFrame.Size));
        return FrameBatchSize(frameCount);
    }
    public static void ReadFrameBatchHeader(ReadOnlySpan<byte> src, out uint startFrame, out ushort frameCount)
    {
        startFrame = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(1, 4));
        frameCount = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(5, 2));
    }
    public static InputFrame ReadFrameBatchInput(ReadOnlySpan<byte> src, int offsetIndex) =>
        InputFrame.Read(src.Slice(7 + offsetIndex * InputFrame.Size, InputFrame.Size));

    // -------- C2S_HashReport : msgId(1) | playerSlot(1) | frame(4) | hash(8)
    public const int HashReportSize = 14;
    public static int WriteHashReport(Span<byte> dst, byte playerSlot, uint frame, ulong hash)
    {
        dst[0] = MsgId.C2S_HashReport;
        dst[1] = playerSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), frame);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(6, 8), hash);
        return HashReportSize;
    }
    public static void ReadHashReport(ReadOnlySpan<byte> src, out byte playerSlot, out uint frame, out ulong hash)
    {
        playerSlot = src[1];
        frame      = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(2, 4));
        hash       = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(6, 8));
    }

    // -------- C2S_RequestResync : msgId(1) | playerSlot(1) | lastAckedFrame(4)
    public const int ResyncReqSize = 6;
    public static int WriteResyncRequest(Span<byte> dst, byte playerSlot, uint lastAckedFrame)
    {
        dst[0] = MsgId.C2S_RequestResync;
        dst[1] = playerSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), lastAckedFrame);
        return ResyncReqSize;
    }
    public static void ReadResyncRequest(ReadOnlySpan<byte> src, out byte playerSlot, out uint lastAckedFrame)
    {
        playerSlot     = src[1];
        lastAckedFrame = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(2, 4));
    }

    // -------- S2C_Snapshot : msgId(1) | snapshotFrame(4) | snapLen(4) | snapBytes[] | followingCount(2) | InputFrame[count*10]
    public static int SnapshotHeaderSize => 1 + 4 + 4 + 2;
    public static int WriteSnapshot(Span<byte> dst, uint snapshotFrame, ReadOnlySpan<byte> snapshot, ushort followingCount, ReadOnlySpan<InputFrame> tail)
    {
        if (tail.Length != followingCount * PlayerCount) throw new ArgumentException("tail count mismatch");
        dst[0] = MsgId.S2C_Snapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(1, 4), snapshotFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(5, 4), (uint)snapshot.Length);
        snapshot.CopyTo(dst.Slice(9));
        int afterSnap = 9 + snapshot.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(afterSnap, 2), followingCount);
        for (int i = 0; i < tail.Length; i++)
            tail[i].Write(dst.Slice(afterSnap + 2 + i * InputFrame.Size, InputFrame.Size));
        return afterSnap + 2 + tail.Length * InputFrame.Size;
    }
    public static void ReadSnapshotHeader(ReadOnlySpan<byte> src, out uint snapshotFrame, out int snapLen, out ushort followingCount, out int snapOffset, out int tailOffset)
    {
        snapshotFrame  = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(1, 4));
        snapLen        = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(5, 4));
        snapOffset     = 9;
        followingCount = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(9 + snapLen, 2));
        tailOffset     = 9 + snapLen + 2;
    }

    // -------- C2S_BuyItem : msgId(1) | playerSlot(1) | itemId(2)
    public const int BuyItemSize = 4;
    public static int WriteBuyItem(Span<byte> dst, byte playerSlot, ushort itemId)
    {
        dst[0] = MsgId.C2S_BuyItem;
        dst[1] = playerSlot;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2, 2), itemId);
        return BuyItemSize;
    }
    public static void ReadBuyItem(ReadOnlySpan<byte> src, out byte playerSlot, out ushort itemId)
    {
        playerSlot = src[1];
        itemId     = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2, 2));
    }

    // -------- S2C_GameOver : msgId(1) | winner(1) | endFrame(4) | durationSec(4)
    public const int GameOverSize = 10;
    public static int WriteGameOver(Span<byte> dst, byte winner, uint endFrame, uint durationSec)
    {
        dst[0] = MsgId.S2C_GameOver;
        dst[1] = winner;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), endFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(6, 4), durationSec);
        return GameOverSize;
    }
    public static void ReadGameOver(ReadOnlySpan<byte> src, out byte winner, out uint endFrame, out uint durationSec)
    {
        winner      = src[1];
        endFrame    = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(2, 4));
        durationSec = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(6, 4));
    }

    // -------- C2S_SpectateRoom : msgId(1) | roomId(4)
    public static int WriteSpectateRoom(Span<byte> dst, uint roomId)
    {
        dst[0] = MsgId.C2S_SpectateRoom;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(1, 4), roomId);
        return 5;
    }
    public static void ReadSpectateRoom(ReadOnlySpan<byte> src, out uint roomId)
        => roomId = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(1, 4));

    // -------- S2C_SpectateAck : msgId(1) | seed(8) | currentFrame(4)
    public const int SpectateAckSize = 13;
    public static int WriteSpectateAck(Span<byte> dst, ulong seed, uint currentFrame)
    {
        dst[0] = MsgId.S2C_SpectateAck;
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(1, 8), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(9, 4), currentFrame);
        return SpectateAckSize;
    }
    public static void ReadSpectateAck(ReadOnlySpan<byte> src, out ulong seed, out uint currentFrame)
    {
        seed         = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(1, 8));
        currentFrame = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(9, 4));
    }
}
