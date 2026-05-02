// Helpers to convert Fix64 raw to/from byte buffers for hashing.
using System;
using System.Runtime.CompilerServices;
using FixMath.NET;

namespace MOBA.Shared.Math;

public static class Fix64Hash
{
    /// <summary>Append 8 little-endian bytes of the raw Fix64 value into the buffer at offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRaw(Span<byte> dst, int offset, Fix64 v)
    {
        long raw = v.RawValue;
        dst[offset + 0] = (byte)(raw);
        dst[offset + 1] = (byte)(raw >> 8);
        dst[offset + 2] = (byte)(raw >> 16);
        dst[offset + 3] = (byte)(raw >> 24);
        dst[offset + 4] = (byte)(raw >> 32);
        dst[offset + 5] = (byte)(raw >> 40);
        dst[offset + 6] = (byte)(raw >> 48);
        dst[offset + 7] = (byte)(raw >> 56);
    }
}
