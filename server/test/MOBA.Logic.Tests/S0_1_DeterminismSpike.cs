// SPDX-License-Identifier: MIT
// Milestone 0 — Spike S0.1
// Determinism check for FixedMath.Net (Fix64) + XorShift128Plus across platforms.
// Outputs an xxHash64 fingerprint of 100k randomized math ops; identical across
// Windows x64 / Linux x64 / Android arm64 means determinism is verified.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using FixMath.NET;
using MOBA.Shared.Math;

namespace MOBA.Logic.Tests;

internal static class S0_1_DeterminismSpike
{
    private const int Iterations = 100_000;
    private const ulong Seed = 0xC0FFEE_F00D_CAFEUL;

    /// <summary>Runs spike. Returns the final 64-bit hash. Writes baseline file in cwd.</summary>
    public static ulong Run()
    {
        var rng = new XorShift128Plus(Seed);
        var hasher = new XxHash64(seed: 0);

        // Operate on a small fixed-size scratch buffer to keep zero-GC during the loop.
        Span<byte> scratch = stackalloc byte[8];

        // Accumulators we feed into the hash so trivial dead-code elimination cannot skip ops.
        Fix64 acc = Fix64.Zero;

        for (int i = 0; i < Iterations; i++)
        {
            // Sample two Fix64 values in roughly [-100, 100] using raw bits.
            // We use the rng's high bits to derive a deterministic "fractional" raw value.
            long rawA = (long)(rng.NextULong()) % (200L << 32) - (100L << 32);
            long rawB = (long)(rng.NextULong()) % (200L << 32) - (100L << 32);
            Fix64 a = Fix64.FromRaw(rawA);
            Fix64 b = Fix64.FromRaw(rawB);

            switch (i & 7)
            {
                case 0: acc += a + b; break;
                case 1: acc += a - b; break;
                case 2: acc += a * b; break;
                case 3:
                    // avoid divide-by-zero
                    if (b.RawValue != 0) acc += a / b;
                    break;
                case 4: acc += Fix64.Sin(a); break;
                case 5: acc += Fix64.Cos(b); break;
                case 6:
                    Fix64 absA = Fix64.Abs(a);
                    acc += Fix64.Sqrt(absA);
                    break;
                case 7:
                    if (b.RawValue != 0) acc += Fix64.Atan2(a, b);
                    break;
            }

            // Feed (acc raw, rng.S0, rng.S1) into the hasher every iteration.
            Fix64Hash.WriteRaw(scratch, 0, acc);
            hasher.Append(scratch);
            BinaryPrimitives.WriteUInt64LittleEndian(scratch, rng.S0);
            hasher.Append(scratch);
            BinaryPrimitives.WriteUInt64LittleEndian(scratch, rng.S1);
            hasher.Append(scratch);
        }

        Span<byte> hashBytes = stackalloc byte[8];
        hasher.GetCurrentHash(hashBytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(hashBytes);
    }

    public static int Execute()
    {
        Console.WriteLine("S0.1 Determinism Spike: FixedMath.Net + XorShift128Plus");
        Console.WriteLine($"  Iterations : {Iterations:N0}");
        Console.WriteLine($"  Seed       : 0x{Seed:X16}");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  OS / Arch  : {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        ulong hash = Run();
        Console.WriteLine($"  HASH       : 0x{hash:X16}");

        // Persist for cross-platform diff (CI artifact).
        string platformTag = $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}-" +
                             $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        string outFile = Path.Combine(AppContext.BaseDirectory, $"S0_1_hash.{platformTag}.txt");
        File.WriteAllText(outFile, $"0x{hash:X16}\n");
        Console.WriteLine($"  Recorded   : {outFile}");

        // Baseline check: if a baseline file is present, compare and fail mismatched.
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "S0_1_baseline.txt");
        if (File.Exists(baselinePath))
        {
            string baseline = File.ReadAllText(baselinePath).Trim();
            string current  = $"0x{hash:X16}";
            if (!string.Equals(baseline, current, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"FAIL: baseline mismatch. expected={baseline} actual={current}");
                return 1;
            }
            Console.WriteLine($"  Baseline   : OK ({baseline})");
        }
        else
        {
            Console.WriteLine("  Baseline   : (not set — copy this run's hash into S0_1_baseline.txt to lock it in)");
        }
        return 0;
    }
}
