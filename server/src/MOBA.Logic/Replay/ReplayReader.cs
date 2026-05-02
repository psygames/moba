// SPDX-License-Identifier: MIT
// PRD §9.2 — .mreplay reader. Layout mirrors ReplayWriter.

using System;
using System.Buffers.Binary;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Replay;

public sealed class ReplayReader
{
    private byte[] _bytes = Array.Empty<byte>();
    private int    _inputStart;        // offset of the first InputFrame[10] block
    private int    _frameStride = ReplayWriter.PerTickBytes;

    public ushort Version       { get; private set; }
    public uint   MapId         { get; private set; }
    public ulong  Seed          { get; private set; }
    public byte   PlayerCount   { get; private set; }
    public ulong  StartUnixSec  { get; private set; }
    public uint   DurationFrames{ get; private set; }
    public int    SnapshotOffset{ get; private set; }
    public int    SnapshotLength{ get; private set; }

    /// <summary>Returns a slice of the snapshot blob owned by this reader.</summary>
    public ReadOnlySpan<byte> SnapshotSpan => _bytes.AsSpan(SnapshotOffset, SnapshotLength);

    public byte[] PlayerSlots { get; } = new byte[10];

    public void Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ReplayWriter.HeaderSizeFixed + 4)
            throw new InvalidOperationException("replay too short");
        if (!(bytes[0] == 'M' && bytes[1] == 'R' && bytes[2] == 'P' && bytes[3] == 'L'))
            throw new InvalidOperationException("bad magic; not a .mreplay file");

        // Copy into owned storage so SnapshotSpan/Inputs slices remain valid.
        if (_bytes.Length < bytes.Length) _bytes = new byte[bytes.Length];
        bytes.CopyTo(_bytes);

        var b = _bytes.AsSpan(0, bytes.Length);
        Version       = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4, 2));
        MapId         = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(6, 4));
        Seed          = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(10, 8));
        PlayerCount   = b[18];
        b.Slice(19, 10).CopyTo(PlayerSlots);
        StartUnixSec  = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(29, 8));
        DurationFrames= BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(37, 4));

        int snapLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(ReplayWriter.HeaderSizeFixed, 4));
        SnapshotOffset = ReplayWriter.HeaderSizeFixed + 4;
        SnapshotLength = snapLen;
        _inputStart    = SnapshotOffset + snapLen;

        long expected = (long)_inputStart + (long)DurationFrames * _frameStride;
        if (expected > bytes.Length)
            throw new InvalidOperationException(
                $"input stream truncated: need {expected} bytes, have {bytes.Length}");
    }

    /// <summary>Decode the InputFrame[10] block for the given frame index into <paramref name="dst"/>.
    /// Caller must ensure <paramref name="frame"/> is in range and <paramref name="dst"/> has length ≥ 10.</summary>
    [NoGC]
    public void GetTick(uint frame, Span<InputFrame> dst)
    {
        int off = _inputStart + (int)frame * _frameStride;
        var src = _bytes.AsSpan(off, _frameStride);
        for (int i = 0; i < 10; i++)
            dst[i] = InputFrame.Read(src.Slice(i * InputFrame.Size, InputFrame.Size));
    }
}
