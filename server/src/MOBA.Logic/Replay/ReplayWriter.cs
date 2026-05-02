// SPDX-License-Identifier: MIT
// PRD §9.2 — .mreplay writer.
//
// Layout (little-endian; total header = 41 bytes):
//   Magic       : "MRPL"          (4)
//   Version     : u16 = 1         (2)
//   MapId       : u32             (4)
//   Seed        : u64             (8)
//   PlayerCount : u8 (=10)        (1)
//   PlayerSlots : byte[10]        (10)
//   StartUnix   : u64 (sec)       (8)
//   DurationFr  : u32             (4)   patched in Finish()
//   ----- header end -----
//   SnapshotLen : u32             (4)
//   Snapshot    : byte[SnapshotLen]
//   InputStream : per frame InputFrame[10] = 100 bytes
//
// Owned byte buffer (no ArrayBufferWriter) so the duration field can be
// patched in place without reflection. MOBA.Logic stays I/O-free; the
// caller (MOBA.Server) is responsible for File.WriteAllBytes.

using System;
using System.Buffers.Binary;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Replay;

public sealed class ReplayWriter
{
    public const ushort CurrentVersion    = 1;
    public const int    HeaderSizeFixed   = 41;
    public const int    DurationFieldOff  = 37;
    public const int    PerTickBytes      = 10 * InputFrame.Size; // 100

    private byte[] _arr;
    private int    _len;
    private uint   _frames;
    private bool   _opened;

    public ReplayWriter(int initialCapacity = 64 * 1024)
    {
        _arr = new byte[initialCapacity];
    }

    public uint FramesWritten => _frames;

    public void BeginRecording(uint mapId, ulong seed, ReadOnlySpan<byte> playerSlots,
                                ulong startUnixSec, ReadOnlySpan<byte> initialSnapshot)
    {
        if (_opened) throw new InvalidOperationException("already recording");
        if (playerSlots.Length != 10) throw new ArgumentException("playerSlots must be length 10");

        Reset();
        EnsureCapacity(HeaderSizeFixed + 4 + initialSnapshot.Length);
        var hdr = _arr.AsSpan(_len, HeaderSizeFixed);
        hdr[0] = (byte)'M'; hdr[1] = (byte)'R'; hdr[2] = (byte)'P'; hdr[3] = (byte)'L';
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(4, 2),  CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(6, 4),  mapId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(10, 8), seed);
        hdr[18] = 10;
        playerSlots.CopyTo(hdr.Slice(19, 10));
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(29, 8), startUnixSec);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(37, 4), 0); // patched later
        _len += HeaderSizeFixed;

        BinaryPrimitives.WriteUInt32LittleEndian(_arr.AsSpan(_len, 4), (uint)initialSnapshot.Length);
        _len += 4;
        initialSnapshot.CopyTo(_arr.AsSpan(_len));
        _len += initialSnapshot.Length;

        _opened = true;
        _frames = 0;
    }

    /// <summary>Append one tick worth of inputs (10 players). Allocation-free
    /// once the underlying buffer has reached steady state. Caller must have invoked
    /// <see cref="BeginRecording"/> and pass exactly 10 frames.</summary>
    [NoGC]
    public void RecordTick(ReadOnlySpan<InputFrame> tick)
    {
        EnsureCapacity(PerTickBytes);
        var dst = _arr.AsSpan(_len, PerTickBytes);
        for (int i = 0; i < 10; i++)
            tick[i].Write(dst.Slice(i * InputFrame.Size, InputFrame.Size));
        _len    += PerTickBytes;
        _frames += 1;
    }

    public ReadOnlySpan<byte> Finish()
    {
        if (!_opened) throw new InvalidOperationException("nothing recorded");
        BinaryPrimitives.WriteUInt32LittleEndian(_arr.AsSpan(DurationFieldOff, 4), _frames);
        return _arr.AsSpan(0, _len);
    }

    public void Reset()
    {
        _len = 0;
        _opened = false;
        _frames = 0;
    }

    private void EnsureCapacity(int more)
    {
        int need = _len + more;
        if (need <= _arr.Length) return;
        int cap = _arr.Length;
        while (cap < need) cap *= 2;
        Array.Resize(ref _arr, cap);
    }
}
