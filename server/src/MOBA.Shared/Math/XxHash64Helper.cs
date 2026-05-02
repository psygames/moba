// SPDX-License-Identifier: MIT
using System;
using System.IO.Hashing;
using System.Buffers.Binary;

namespace MOBA.Shared.Math;

/// <summary>
/// Thin wrapper over <see cref="System.IO.Hashing.XxHash64"/> that exposes a one-shot
/// <c>Hash(ReadOnlySpan&lt;byte&gt;)</c>. Lives in MOBA.Shared so that MOBA.Logic
/// (whose analyzer forbids the System.IO namespace at large) can still compute
/// deterministic snapshot hashes.
/// </summary>
public static class XxHash64Helper
{
    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        var hasher = new XxHash64(0);
        hasher.Append(data);
        Span<byte> dst = stackalloc byte[8];
        hasher.GetHashAndReset(dst);
        return BinaryPrimitives.ReadUInt64BigEndian(dst);
    }
}
