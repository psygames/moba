// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MOBA.Shared.Protocol;

/// <summary>
/// Per-player input for one logic frame. Fixed-size 10 bytes (PRD §5.2). Little-endian on the wire.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 10)]
public struct InputFrame : IEquatable<InputFrame>
{
    public sbyte JoyX;          // -100..100
    public sbyte JoyY;          // -100..100
    public byte SkillBits;      // bit0..3 = skills 1..4, bit4 = autoattack, bit5 = cancel
    public byte TargetSlot;     // 0 = none, 1..32 = locked entity slot
    public ushort AimAngleDeg;  // 0..359 for direction skills
    public byte Flags;          // bit0=recall bit1=upgrade skill1 ...
    public byte Pad;
    public ushort BuyItemId;    // 0 = none; otherwise (itemId+1). Server-injected; ignored on C2S input.

    public const int Size = 10;

    public static readonly InputFrame Empty = default;

    public void Write(Span<byte> dst)
    {
        if (dst.Length < Size) throw new ArgumentException("dst too small");
        dst[0] = (byte)JoyX;
        dst[1] = (byte)JoyY;
        dst[2] = SkillBits;
        dst[3] = TargetSlot;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), AimAngleDeg);
        dst[6] = Flags;
        dst[7] = Pad;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(8, 2), BuyItemId);
    }

    public static InputFrame Read(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size) throw new ArgumentException("src too small");
        return new InputFrame
        {
            JoyX = (sbyte)src[0],
            JoyY = (sbyte)src[1],
            SkillBits = src[2],
            TargetSlot = src[3],
            AimAngleDeg = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2)),
            Flags = src[6],
            Pad = src[7],
            BuyItemId = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(8, 2)),
        };
    }

    public bool Equals(InputFrame o) =>
        JoyX == o.JoyX && JoyY == o.JoyY && SkillBits == o.SkillBits && TargetSlot == o.TargetSlot &&
        AimAngleDeg == o.AimAngleDeg && Flags == o.Flags && Pad == o.Pad && BuyItemId == o.BuyItemId;
    public override bool Equals(object obj) => obj is InputFrame f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(JoyX, JoyY, SkillBits, TargetSlot, AimAngleDeg, Flags);
}
