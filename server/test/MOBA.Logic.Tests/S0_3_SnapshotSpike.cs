// SPDX-License-Identifier: MIT
// Milestone 0 — Spike S0.3
// Snapshot round-trip for Box2DSharp-deterministic.
//
// PRD §3.3 explicitly accepts "first-frame contact warm-rebuild jitter" after
// a snapshot restore — so we do NOT compare a restored world against the
// original continuous run. The production invariants are:
//   T1. Read(Write(world)) reproduces body kinematic state bit-exactly.
//   T2. Two independent restores from the same snapshot, stepped forward
//       identically, produce identical hashes (deterministic re-simulation).
// Bonus: we report position drift between the original continuous world and
// the restored world after N further steps, so we can monitor that drift in
// future regressions (it is allowed but should stay bounded).

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Common;
using Box2DSharp.Dynamics;
using MOBA.Shared.Math;

namespace MOBA.Logic.Tests;

internal static class S0_3_SnapshotSpike
{
    private const int CircleCount   = 1000;
    private const int Steps         = 1000;
    private const int SnapAt        = 500;
    private const int VelIterations = 8;
    private const int PosIterations = 3;
    private const ulong Seed        = 0xC0FFEE_F00D_CAFEUL;
    private const long HalfBoxRaw    = 25L << 32;
    private const long SpawnRangeRaw = 24L << 32;

    /// <summary>Builds world with walls + circles. Returns dynamic body array in deterministic order.</summary>
    private static (World world, Body[] bodies) BuildWorld()
    {
        var gravity = new FVector2(FP.FromRaw(0), FP.FromRaw(0));
        var world = new World(gravity)
        {
            // Disable warm-starting and TOI to make snapshot+restore bit-exact.
            WarmStarting = false,
            ContinuousPhysics = false,
        };

        FP wallHalfThick = FP.FromRaw(1L << 32);
        FP boxHalf       = FP.FromRaw(HalfBoxRaw);
        FP zero          = FP.FromRaw(0);
        CreateWall(world, new FVector2(zero, boxHalf + wallHalfThick),    boxHalf + wallHalfThick, wallHalfThick);
        CreateWall(world, new FVector2(zero, -(boxHalf + wallHalfThick)), boxHalf + wallHalfThick, wallHalfThick);
        CreateWall(world, new FVector2(boxHalf + wallHalfThick, zero),    wallHalfThick, boxHalf + wallHalfThick);
        CreateWall(world, new FVector2(-(boxHalf + wallHalfThick), zero), wallHalfThick, boxHalf + wallHalfThick);

        var rng = new XorShift128Plus(Seed);
        var bodies = new Body[CircleCount];
        for (int i = 0; i < CircleCount; i++)
        {
            FP px = SampleSymmetric(ref rng, SpawnRangeRaw);
            FP py = SampleSymmetric(ref rng, SpawnRangeRaw);
            long radiusRaw = 858993459L + (long)(rng.NextULong() % (long)(0.3 * (1L << 32)));
            FP radius = FP.FromRaw(radiusRaw);
            FP vx = SampleSymmetric(ref rng, 3L << 32);
            FP vy = SampleSymmetric(ref rng, 3L << 32);

            var bd = new BodyDef
            {
                BodyType = BodyType.DynamicBody,
                Position = new FVector2(px, py),
                LinearVelocity = new FVector2(vx, vy),
                AllowSleep = false,
            };
            var body = world.CreateBody(bd);
            body.CreateFixture(new FixtureDef
            {
                Shape = new CircleShape { Radius = radius },
                Density     = FP.FromRaw(1L << 32),
                Friction    = FP.FromRaw(1L << 30),
                Restitution = FP.FromRaw(1L << 31),
            });
            bodies[i] = body;
        }
        return (world, bodies);
    }

    private static void StepN(World w, int n)
    {
        FP dt = FP.FromRaw((1L << 32) / 60);
        for (int i = 0; i < n; i++) w.Step(dt, VelIterations, PosIterations);
    }

    /// <summary>Snapshot layout: per body { posX, posY, angle, velX, velY, angVel : long; awake : byte }.</summary>
    private const int BytesPerBody = 8 * 6 + 1;

    private static byte[] WriteSnapshot(Body[] bodies)
    {
        var buf = new byte[bodies.Length * BytesPerBody];
        for (int i = 0; i < bodies.Length; i++)
        {
            var b = bodies[i];
            int off = i * BytesPerBody;
            FVector2 p = b.GetPosition();
            FVector2 v = b.LinearVelocity;
            FP angle  = b.GetAngle();
            FP angVel = b.AngularVelocity;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 0,  8), p.X.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 8,  8), p.Y.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 16, 8), angle.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 24, 8), v.X.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 32, 8), v.Y.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 40, 8), angVel.RawValue);
            buf[off + 48] = (byte)(b.IsAwake ? 1 : 0);
        }
        return buf;
    }

    private static void ReadSnapshot(Body[] bodies, ReadOnlySpan<byte> buf)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            int off = i * BytesPerBody;
            long pxR  = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 0,  8));
            long pyR  = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 8,  8));
            long angR = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 16, 8));
            long vxR  = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 24, 8));
            long vyR  = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 32, 8));
            long avR  = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 40, 8));
            bool awake = buf[off + 48] != 0;
            var b = bodies[i];
            b.SetTransform(new FVector2(FP.FromRaw(pxR), FP.FromRaw(pyR)), FP.FromRaw(angR));
            b.SetLinearVelocity(new FVector2(FP.FromRaw(vxR), FP.FromRaw(vyR)));
            b.SetAngularVelocity(FP.FromRaw(avR));
            b.IsAwake = awake;
        }
    }

    private static ulong HashWorld(Body[] bodies)
    {
        var hasher = new XxHash64(0);
        Span<byte> scratch = stackalloc byte[8];
        for (int i = 0; i < bodies.Length; i++)
        {
            var b = bodies[i];
            FVector2 p = b.GetPosition();
            FVector2 v = b.LinearVelocity;
            BinaryPrimitives.WriteInt64LittleEndian(scratch, p.X.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, p.Y.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, v.X.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, v.Y.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, b.GetAngle().RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, b.AngularVelocity.RawValue); hasher.Append(scratch);
        }
        Span<byte> outBytes = stackalloc byte[8];
        hasher.GetCurrentHash(outBytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(outBytes);
    }

    private static FP SampleSymmetric(ref XorShift128Plus rng, long halfRangeRaw)
    {
        long modulus = halfRangeRaw * 2L;
        long r = (long)(rng.NextULong() % (ulong)modulus) - halfRangeRaw;
        return FP.FromRaw(r);
    }

    private static void CreateWall(World world, FVector2 center, FP halfX, FP halfY)
    {
        var bd = new BodyDef { BodyType = BodyType.StaticBody, Position = center };
        var body = world.CreateBody(bd);
        var box = new PolygonShape();
        box.SetAsBox(halfX, halfY);
        body.CreateFixture(box, FP.FromRaw(0));
    }

    public static int Execute()
    {
        Console.WriteLine("S0.3 Snapshot Spike: Box2D world snapshot round-trip");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  OS / Arch  : {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        // --- Reference path: build & step 1000 in one go ---
        var (refWorld, refBodies) = BuildWorld();
        StepN(refWorld, Steps);
        ulong refHash = HashWorld(refBodies);
        Console.WriteLine($"  REF hash      (1000 steps continuous)        : 0x{refHash:X16}");

        // --- Snapshot path A: build, step 500, snapshot bytes ---
        var (worldA, bodiesA) = BuildWorld();
        StepN(worldA, SnapAt);
        ulong midHashA = HashWorld(bodiesA);
        byte[] snap = WriteSnapshot(bodiesA);
        Console.WriteLine($"  Mid hash A    ({SnapAt} steps in worldA)       : 0x{midHashA:X16}");
        Console.WriteLine($"  Snapshot size : {snap.Length:N0} bytes");

        // T1: Read into a fresh world reproduces state bit-exactly.
        var (worldB, bodiesB) = BuildWorld();
        ReadSnapshot(bodiesB, snap);
        ulong midHashB = HashWorld(bodiesB);
        Console.WriteLine($"  Mid hash B    (fresh world after Read)         : 0x{midHashB:X16}");
        if (midHashA != midHashB)
        {
            Console.Error.WriteLine("FAIL T1: snapshot read did not reproduce body state.");
            return 1;
        }

        // T2: Two independent restores from the same snapshot, stepped equally,
        //     produce identical hashes (deterministic re-simulation).
        var (worldC, bodiesC) = BuildWorld();
        ReadSnapshot(bodiesC, snap);
        StepN(worldB, Steps - SnapAt);
        StepN(worldC, Steps - SnapAt);
        ulong restoredB = HashWorld(bodiesB);
        ulong restoredC = HashWorld(bodiesC);
        Console.WriteLine($"  Restored B    ({Steps - SnapAt} more steps)            : 0x{restoredB:X16}");
        Console.WriteLine($"  Restored C    ({Steps - SnapAt} more steps)            : 0x{restoredC:X16}");
        if (restoredB != restoredC)
        {
            Console.Error.WriteLine("FAIL T2: two restored worlds diverged from each other.");
            return 1;
        }

        // Diagnostic: drift vs reference (PRD-permitted, no DoD threshold).
        FP maxDelta = MaxPositionDelta(refBodies, bodiesB);
        Console.WriteLine($"  Drift vs REF  (max |pos|, allowed)             : {(double)maxDelta:F4} m");

        Console.WriteLine("  RESULT     : OK (T1 instant-restore bit-exact, T2 re-sim deterministic)");

        // Lock REF hash baseline so even drift-causing regressions get caught.
        string outFile = Path.Combine(AppContext.BaseDirectory, "S0_3_hash.txt");
        File.WriteAllText(outFile, $"REF=0x{refHash:X16}\nRESTORED=0x{restoredB:X16}\n");
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "S0_3_baseline.txt");
        if (File.Exists(baselinePath))
        {
            string expected = File.ReadAllText(baselinePath).Trim();
            string actual   = $"REF=0x{refHash:X16};RESTORED=0x{restoredB:X16}";
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"FAIL: baseline mismatch. expected={expected} actual={actual}");
                return 1;
            }
            Console.WriteLine($"  Baseline   : OK ({expected})");
        }
        else
        {
            Console.WriteLine("  Baseline   : (not set — copy this run's REF/RESTORED line into S0_3_baseline.txt to lock it in)");
        }
        return 0;
    }

    private static FP MaxPositionDelta(Body[] a, Body[] b)
    {
        FP max = FP.FromRaw(0);
        for (int i = 0; i < a.Length; i++)
        {
            FVector2 pa = a[i].GetPosition();
            FVector2 pb = b[i].GetPosition();
            FP dx = FP.Abs(pa.X - pb.X);
            FP dy = FP.Abs(pa.Y - pb.Y);
            FP d  = dx > dy ? dx : dy;
            if (d > max) max = d;
        }
        return max;
    }
}
