// SPDX-License-Identifier: MIT
// Project MOBA — deterministic PRNG.
// xorshift128+ by Sebastiano Vigna (https://prng.di.unimi.it/xorshift128plus.c).
// Pure managed, no allocation, deterministic across platforms.
using System.Runtime.CompilerServices;

namespace MOBA.Shared.Math;

/// <summary>
/// Deterministic 64-bit pseudo-random generator (xorshift128+).
/// State is two ulongs; both must not be zero simultaneously.
/// </summary>
public struct XorShift128Plus
{
    public ulong S0;
    public ulong S1;

    public XorShift128Plus(ulong seed)
    {
        // splitmix64 to expand single seed into two non-zero state words.
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        S0 = z ^ (z >> 31);
        z = (S0 + 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        S1 = z ^ (z >> 31);
        if (S0 == 0UL && S1 == 0UL) S1 = 1UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextULong()
    {
        ulong x = S0;
        ulong y = S1;
        S0 = y;
        x ^= x << 23;
        S1 = x ^ y ^ (x >> 17) ^ (y >> 26);
        return S1 + y;
    }

    /// <summary>Uniform int in [min, maxExclusive). Deterministic, no float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int min, int maxExclusive)
    {
        uint range = (uint)(maxExclusive - min);
        // unbiased modulo via 64-bit multiplication
        ulong product = (ulong)(uint)NextULong() * range;
        return min + (int)(product >> 32);
    }
}
