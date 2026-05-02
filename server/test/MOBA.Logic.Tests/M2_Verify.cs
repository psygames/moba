// SPDX-License-Identifier: MIT
// M2 verification — invoked via `dotnet run --project test/MOBA.Logic.Tests -- m2`
//
// Three checks (PRD §3.2 + Milestone 2 DoD):
//   M2.1  Corner cases : 4 hand-crafted scenarios produce expected outcomes.
//   M2.2  Pathfinder   : 200×200 random map, 1000 random pairs:
//                        avg < 2 ms, max < 8 ms.
//   M2.3  Combined tick: 100 entities pathfind+move + 1 physics Step on a
//                        single logic frame must stay under 8 ms.

using System;
using System.Diagnostics;
using MOBA.Logic;
using MOBA.Logic.Movement;
using MOBA.Logic.Pathfinding;
using MOBA.Logic.Physics;
using Fix64    = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M2_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M2 Verify");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        int rc = 0;
        rc |= CornerCases();
        rc |= PerfRandom();
        rc |= CombinedTick();
        return rc;
    }

    // -------- M2.1 ----------------------------------------------------------

    private static int CornerCases()
    {
        var ok = true;
        int[] buf = new int[4096];

        // Case A: same start/goal cell ⇒ length 1
        {
            var map = MakeOpen(10, 10);
            var pf = new GridPathfinder(map);
            int n = pf.FindPath(3, 3, 3, 3, buf);
            ok &= Expect("A same-cell", n == 1 && buf[0] == map.Index(3, 3));
        }
        // Case B: start blocked ⇒ length 0
        {
            var map = MakeOpen(10, 10);
            map.RawCells[map.Index(2, 2)] = 1;
            var pf = new GridPathfinder(map);
            int n = pf.FindPath(2, 2, 5, 5, buf);
            ok &= Expect("B start blocked", n == 0);
        }
        // Case C: goal unreachable (walled-off) ⇒ length 0
        {
            var map = MakeOpen(10, 10);
            for (int y = 0; y < 10; y++) map.RawCells[map.Index(5, y)] = 1; // vertical wall x=5
            var pf = new GridPathfinder(map);
            int n = pf.FindPath(2, 2, 8, 8, buf);
            ok &= Expect("C unreachable", n == 0);
        }
        // Case D: no diagonal corner-cutting through walls
        {
            // open 5x5, but block (1,0) and (0,1). Diagonal (0,0)->(1,1) must be denied.
            var map = MakeOpen(5, 5);
            map.RawCells[map.Index(1, 0)] = 1;
            map.RawCells[map.Index(0, 1)] = 1;
            var pf = new GridPathfinder(map);
            int n = pf.FindPath(0, 0, 1, 1, buf);
            // (1,1) is unreachable from (0,0): must wall-route, but every adjacent diagonal/straight
            // out of (0,0) is blocked. So no path.
            ok &= Expect("D corner-cut blocked", n == 0);
        }
        Console.WriteLine(ok ? "  M2.1 OK    : 4/4 corner cases pass" : "  M2.1 FAIL");
        return ok ? 0 : 1;
    }

    private static GridMap MakeOpen(int w, int h)
    {
        var cells = new byte[w * h];
        return new GridMap(w, h, (Fix64)0.5f, TSVector2.Zero, cells);
    }

    private static bool Expect(string label, bool cond)
    {
        if (!cond) Console.Error.WriteLine($"  M2.1 case {label}: FAIL");
        return cond;
    }

    // -------- M2.2 ----------------------------------------------------------

    private static int PerfRandom()
    {
        const int W = 200, H = 200, Pairs = 1000;
        var map = BuildRandomMap(W, H, blockProb: 0.18, seed: 0xC0FFEE);
        var pf = new GridPathfinder(map);
        int[] buf = new int[W * H];

        // Generate deterministic walkable start/goal pairs.
        var rng = new MOBA.Shared.Math.XorShift128Plus(0xDEADBEEFUL);
        var pairs = new (int sx, int sy, int ex, int ey)[Pairs];
        int got = 0;
        while (got < Pairs)
        {
            int sx = (int)(rng.NextULong() % (ulong)W);
            int sy = (int)(rng.NextULong() % (ulong)H);
            int ex = (int)(rng.NextULong() % (ulong)W);
            int ey = (int)(rng.NextULong() % (ulong)H);
            if (!map.IsWalkable(sx, sy) || !map.IsWalkable(ex, ey)) continue;
            pairs[got++] = (sx, sy, ex, ey);
        }

        // Warmup
        for (int i = 0; i < 30; i++) pf.FindPath(pairs[i].sx, pairs[i].sy, pairs[i].ex, pairs[i].ey, buf);

        long[] samples = new long[Pairs];
        long totalLen = 0;
        int found = 0;
        var sw = new Stopwatch();
        for (int i = 0; i < Pairs; i++)
        {
            var p = pairs[i];
            sw.Restart();
            int len = pf.FindPath(p.sx, p.sy, p.ex, p.ey, buf);
            sw.Stop();
            samples[i] = sw.ElapsedTicks;
            if (len > 0) { totalLen += len; found++; }
        }
        Array.Sort(samples);
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        double avg = 0;
        for (int i = 0; i < Pairs; i++) avg += samples[i] * tickToMs;
        avg /= Pairs;
        double p50 = samples[Pairs / 2] * tickToMs;
        double p95 = samples[(int)(Pairs * 0.95)] * tickToMs;
        double max = samples[Pairs - 1] * tickToMs;

        Console.WriteLine($"  M2.2 PathPerf: {Pairs} A* on {W}x{H} (block prob 0.18, found {found})");
        Console.WriteLine($"              avg={avg:F3}ms p50={p50:F3}ms p95={p95:F3}ms max={max:F3}ms avgLen={(found == 0 ? 0 : totalLen / found)}");
        bool dod = avg < 2.0 && max < 8.0;
        Console.WriteLine(dod ? "  M2.2 OK    : avg<2ms max<8ms" : "  M2.2 FAIL : DoD breach");
        return dod ? 0 : 1;
    }

    private static GridMap BuildRandomMap(int w, int h, double blockProb, ulong seed)
    {
        // Deterministic block fill via xorshift, but we only need it once at setup
        // so the floating-point comparison is acceptable here.
        var rng = new MOBA.Shared.Math.XorShift128Plus(seed);
        var cells = new byte[w * h];
        ulong threshold = (ulong)(blockProb * ulong.MaxValue);
        for (int i = 0; i < cells.Length; i++)
            if (rng.NextULong() < threshold) cells[i] = 1;
        // Ensure border is walkable so most goals are reachable.
        for (int x = 0; x < w; x++) { cells[x] = 0; cells[(h - 1) * w + x] = 0; }
        for (int y = 0; y < h; y++) { cells[y * w] = 0; cells[y * w + (w - 1)] = 0; }
        return new GridMap(w, h, (Fix64)0.5f, TSVector2.Zero, cells);
    }

    // -------- M2.3 ----------------------------------------------------------

    private static int CombinedTick()
    {
        const int W = 200, H = 200, Entities = 100, Frames = 200;

        var map = BuildRandomMap(W, H, blockProb: 0.12, seed: 0xBAD0DA7A);
        var pf = new GridPathfinder(map);
        var world = new PhysicsWorldManager(maxEntityId: 1024, warmStarting: false, continuous: false);
        var movement = new MovementSystem(map, world);

        // Spawn 100 dynamic circles at deterministic spawn cells, give each a goal.
        var agents = new PathAgent[Entities];
        ushort cat = 0x0002, mask = 0xFFFF;
        var rng = new MOBA.Shared.Math.XorShift128Plus(0xA11ACAFEUL);
        Fix64 radius = (Fix64)0.2f;
        Fix64 speed  = (Fix64)4;
        int[] pathBuf = new int[W * H];

        for (int i = 0; i < Entities; i++)
        {
            int sx, sy, ex, ey;
            while (true)
            {
                sx = (int)(rng.NextULong() % (ulong)W);
                sy = (int)(rng.NextULong() % (ulong)H);
                ex = (int)(rng.NextULong() % (ulong)W);
                ey = (int)(rng.NextULong() % (ulong)H);
                if (!map.IsWalkable(sx, sy) || !map.IsWalkable(ex, ey)) continue;
                int len = pf.FindPath(sx, sy, ex, ey, pathBuf);
                if (len <= 1) continue;
                var ag = new PathAgent { Entity = new EntityId(100u + (uint)i), Speed = speed, PathLen = len, Cursor = 0 };
                Array.Copy(pathBuf, 0, ag.Path, 0, len);
                agents[i] = ag;
                break;
            }
            world.CreateCircle(agents[i].Entity, map.CellCenter(sx, sy), radius, BodyType.Dynamic, cat, mask);
        }

        Fix64 dt = Fix64.FromRaw((1L << 32) / 15);

        // Warmup
        for (int i = 0; i < 20; i++) { movement.Tick(agents, Entities); world.Step(dt); }

        // Per measured frame: re-path 1 entity (round-robin), tick movement, step physics.
        long[] samples = new long[Frames];
        var sw = new Stopwatch();
        for (int f = 0; f < Frames; f++)
        {
            sw.Restart();
            int repathIdx = f % Entities;
            var ag = agents[repathIdx];
            int sx, sy;
            map.WorldToCell(world.TryGet(ag.Entity)!.Position, out sx, out sy);
            if (map.IsWalkable(sx, sy))
            {
                int ex = (int)(rng.NextULong() % (ulong)W);
                int ey = (int)(rng.NextULong() % (ulong)H);
                if (map.IsWalkable(ex, ey))
                {
                    int len = pf.FindPath(sx, sy, ex, ey, pathBuf);
                    if (len > 1) { Array.Copy(pathBuf, 0, ag.Path, 0, len); ag.PathLen = len; ag.Cursor = 0; }
                }
            }
            movement.Tick(agents, Entities);
            world.Step(dt);
            sw.Stop();
            samples[f] = sw.ElapsedTicks;
        }
        Array.Sort(samples);
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        double p50 = samples[Frames / 2] * tickToMs;
        double p95 = samples[(int)(Frames * 0.95)] * tickToMs;
        double max = samples[Frames - 1] * tickToMs;

        Console.WriteLine($"  M2.3 Tick   : {Entities} entities, repath 1/frame, {Frames} samples");
        Console.WriteLine($"              p50={p50:F3}ms p95={p95:F3}ms max={max:F3}ms");
        bool dod = p95 < 8.0;
        Console.WriteLine(dod ? "  M2.3 OK    : p95 < 8ms" : "  M2.3 FAIL : tick budget exceeded");
        return dod ? 0 : 1;
    }
}
