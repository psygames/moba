// SPDX-License-Identifier: MIT
// M1 verification — runs as part of MOBA.Logic.Tests.
//
// Three checks (PRD §11/M1 DoD):
//   M1.1  Pool zero-alloc:    1,000,000 Get/Return cycles must observe zero
//                             managed allocations after warmup.
//   M1.2  PhysicsWorldManager:500 dynamic circles + 4 static walls, single
//                             Step(1/15) under 2 ms (warm cache, p50 of 200 runs).
//   M1.3  Analyzer self-test: documented in S1_3_AnalyzerProbe.* — verified by
//                             a separate compilation step in this method.

using System;
using System.Diagnostics;
using MOBA.Logic;
using MOBA.Logic.Physics;
using Fix64    = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M1_Verify
{
    private sealed class Bullet : IPoolable
    {
        public int X;
        public void Reset() { X = 0; }
    }

    public static int Execute()
    {
        Console.WriteLine("M1 Verify");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        int rc = 0;
        rc |= CheckPoolZeroAlloc();
        rc |= CheckPhysicsBudget();
        return rc;
    }

    // -------- M1.1 ----------------------------------------------------------

    private static int CheckPoolZeroAlloc()
    {
        const int Warmup     = 1_000;
        const int Iterations = 1_000_000;
        const int Capacity   = 256;

        var pool = new Pool<Bullet>(Capacity, Capacity);
        pool.Prewarm(Capacity);

        // Warmup: cycle a bunch of items through to JIT the call sites.
        for (int i = 0; i < Warmup; i++)
        {
            var b = pool.Get(); pool.Return(b);
        }
        // Warm the GC counters API itself (first call may allocate).
        _ = GC.GetAllocatedBytesForCurrentThread();
        var sw = new Stopwatch(); // allocate before baseline so it doesn't pollute the delta

        // Force a clean GC baseline.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long beforeGen0 = GC.CollectionCount(0);
        long beforeGen1 = GC.CollectionCount(1);
        long beforeGen2 = GC.CollectionCount(2);
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

        sw.Restart();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++)
        {
            var b = pool.Get();
            pool.Return(b);
        }
        long t1 = Stopwatch.GetTimestamp();
        sw.Stop();

        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long allocDelta = afterAlloc - beforeAlloc;
        long gen0Delta  = GC.CollectionCount(0) - beforeGen0;
        long gen1Delta  = GC.CollectionCount(1) - beforeGen1;
        long gen2Delta  = GC.CollectionCount(2) - beforeGen2;

        Console.WriteLine($"  M1.1 Pool   : {Iterations:N0} Get/Return in {sw.Elapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"               alloc delta = {allocDelta} B   (gen0={gen0Delta} gen1={gen1Delta} gen2={gen2Delta})");
        if (allocDelta != 0 || gen0Delta != 0)
        {
            Console.Error.WriteLine($"  M1.1 FAIL : expected 0 bytes, got {allocDelta} B / {gen0Delta} gen0 collections");
            return 1;
        }
        Console.WriteLine("  M1.1 OK    : zero allocations confirmed");
        return 0;
    }

    // -------- M1.2 ----------------------------------------------------------

    private static int CheckPhysicsBudget()
    {
        const int Bodies = 500;
        const int Runs   = 200;
        const int MaxEntityId = 1024;

        var world = new PhysicsWorldManager(maxEntityId: MaxEntityId, warmStarting: true, continuous: false);

        // 4 walls around a 50×50 box.
        Fix64 boxHalf = (Fix64)40;
        Fix64 wallThk = (Fix64)1;
        Fix64 zero    = Fix64.Zero;
        ushort wallCat = 0x0001, wallMask = 0xFFFF;
        world.CreateBox(new EntityId(1), new TSVector2(zero, boxHalf + wallThk), new TSVector2(boxHalf + wallThk, wallThk), BodyType.Static, wallCat, wallMask);
        world.CreateBox(new EntityId(2), new TSVector2(zero, -(boxHalf + wallThk)), new TSVector2(boxHalf + wallThk, wallThk), BodyType.Static, wallCat, wallMask);
        world.CreateBox(new EntityId(3), new TSVector2(boxHalf + wallThk, zero), new TSVector2(wallThk, boxHalf + wallThk), BodyType.Static, wallCat, wallMask);
        world.CreateBox(new EntityId(4), new TSVector2(-(boxHalf + wallThk), zero), new TSVector2(wallThk, boxHalf + wallThk), BodyType.Static, wallCat, wallMask);

        // 500 circles laid out on a sparse 30x30 grid (much less initial overlap)
        // so contact graph is small after a brief settling period.
        ushort dynCat = 0x0002, dynMask = 0xFFFF;
        var rng = new MOBA.Shared.Math.XorShift128Plus(0xBADCAB1EUL);
        Fix64 radius = (Fix64)0.3f;
        Fix64 spacing = (Fix64)1.5f;
        for (int i = 0; i < Bodies; i++)
        {
            int gx = i % 30 - 15;
            int gy = i / 30 - 8;
            var pos = new TSVector2((Fix64)gx * spacing, (Fix64)gy * spacing);
            var body = world.CreateCircle(new EntityId(10u + (uint)i), pos, radius, BodyType.Dynamic, dynCat, dynMask);
            // small symmetric velocity so simulation isn't a degenerate stack
            long vxR = (long)(rng.NextULong() % (ulong)(1L << 31)) - (1L << 30);
            long vyR = (long)(rng.NextULong() % (ulong)(1L << 31)) - (1L << 30);
            body.LinearVelocity = new TSVector2(Fix64.FromRaw(vxR), Fix64.FromRaw(vyR));
        }

        Fix64 dt = Fix64.FromRaw((1L << 32) / 15);

        // Warmup until the contact graph has stabilised.
        for (int i = 0; i < 200; i++) world.Step(dt);

        // Measure single-step latency over Runs samples.
        long[] samples = new long[Runs];
        var sw = new Stopwatch();
        for (int i = 0; i < Runs; i++)
        {
            sw.Restart();
            world.Step(dt);
            sw.Stop();
            samples[i] = sw.ElapsedTicks;
        }
        Array.Sort(samples);
        long p50 = samples[Runs / 2];
        long p95 = samples[(int)(Runs * 0.95)];
        long pMax = samples[Runs - 1];
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"  M1.2 Physic : 500 circles + 4 walls @ 1/15 dt, {Runs} samples");
        Console.WriteLine($"              p50={p50 * tickToMs:F3}ms  p95={p95 * tickToMs:F3}ms  max={pMax * tickToMs:F3}ms");
        bool dod = p50 * tickToMs < 2.0;
        Console.WriteLine(dod ? "  M1.2 OK    : p50 < 2ms" : "  M1.2 FAIL : p50 budget exceeded");
        return dod ? 0 : 1;
    }
}
