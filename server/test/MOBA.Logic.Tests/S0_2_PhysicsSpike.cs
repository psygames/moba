// SPDX-License-Identifier: MIT
// Milestone 0 — Spike S0.2
// Determinism check for Zonciu/Box2DSharp-deterministic.
// 1000 dynamic circles in a 50x50 enclosed box, zero gravity (MOBA top-down),
// initial random velocities from XorShift128Plus, 1000 steps.
// Final hash = xxHash64 over all body (pos.x, pos.y, vel.x, vel.y) raws.
// Identical hash across Windows x64 / Linux x64 / Android arm64 = pass.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Common;
using Box2DSharp.Dynamics;
using MOBA.Shared.Math;

namespace MOBA.Logic.Tests;

internal static class S0_2_PhysicsSpike
{
    private const int CircleCount      = 1000;
    private const int Steps            = 1000;
    private const int VelIterations    = 8;
    private const int PosIterations    = 3;
    private const ulong Seed           = 0xC0FFEE_F00D_CAFEUL;
    // Box: [-25, 25] on both axes. Walls inset slightly so spawns don't overlap.
    private const long HalfBoxRaw      = 25L << 32;     // FP raw for 25
    private const long SpawnRangeRaw   = 24L << 32;     // FP raw for 24

    public static ulong Run()
    {
        // Zero gravity (top-down MOBA).
        var gravity = new FVector2(FP.FromRaw(0), FP.FromRaw(0));
        var world = new World(gravity);

        // --- Static enclosing box made of 4 thin polygon walls ---
        FP wallHalfThick = FP.FromRaw(1L << 32);   // 1.0
        FP boxHalf       = FP.FromRaw(HalfBoxRaw); // 25.0
        FP zero          = FP.FromRaw(0);
        CreateWall(world, new FVector2(zero, boxHalf + wallHalfThick),  boxHalf + wallHalfThick, wallHalfThick); // top
        CreateWall(world, new FVector2(zero, -(boxHalf + wallHalfThick)), boxHalf + wallHalfThick, wallHalfThick); // bottom
        CreateWall(world, new FVector2(boxHalf + wallHalfThick, zero),  wallHalfThick, boxHalf + wallHalfThick); // right
        CreateWall(world, new FVector2(-(boxHalf + wallHalfThick), zero), wallHalfThick, boxHalf + wallHalfThick); // left

        // --- Dynamic circles ---
        var rng = new XorShift128Plus(Seed);
        var bodies = new Body[CircleCount];
        for (int i = 0; i < CircleCount; i++)
        {
            // Position uniform in [-24, 24]
            FP px = SampleSymmetric(ref rng, SpawnRangeRaw);
            FP py = SampleSymmetric(ref rng, SpawnRangeRaw);
            // Radius in [0.2, 0.5]: raw 0.2 = 0.2 * 2^32 ≈ 858993459
            long radiusRaw = 858993459L + (long)(rng.NextULong() % (long)(0.3 * (1L << 32)));
            FP radius = FP.FromRaw(radiusRaw);
            // Velocity in [-3, 3]
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
            var shape = new CircleShape { Radius = radius };
            var fd = new FixtureDef
            {
                Shape = shape,
                Density = FP.FromRaw(1L << 32),       // 1.0
                Friction = FP.FromRaw(1L << 30),       // 0.25
                Restitution = FP.FromRaw(1L << 31),    // 0.5
            };
            body.CreateFixture(fd);
            bodies[i] = body;
        }

        // --- Step ---
        FP dt = FP.FromRaw((1L << 32) / 60); // 1/60 second
        for (int s = 0; s < Steps; s++)
        {
            world.Step(dt, VelIterations, PosIterations);
        }

        // --- Hash ---
        var hasher = new XxHash64(seed: 0);
        Span<byte> scratch = stackalloc byte[8];
        for (int i = 0; i < CircleCount; i++)
        {
            var b = bodies[i];
            FVector2 p = b.GetPosition();
            FVector2 v = b.LinearVelocity;
            BinaryPrimitives.WriteInt64LittleEndian(scratch, p.X.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, p.Y.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, v.X.RawValue); hasher.Append(scratch);
            BinaryPrimitives.WriteInt64LittleEndian(scratch, v.Y.RawValue); hasher.Append(scratch);
        }
        Span<byte> hashBytes = stackalloc byte[8];
        hasher.GetCurrentHash(hashBytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(hashBytes);
    }

    private static FP SampleSymmetric(ref XorShift128Plus rng, long halfRangeRaw)
    {
        // Uniform in [-halfRange, +halfRange] using rng raw bits.
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
        Console.WriteLine("S0.2 Physics Spike: Box2DSharp-deterministic 1000 circles x 1000 steps");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  OS / Arch  : {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong hash = Run();
        sw.Stop();
        Console.WriteLine($"  Elapsed    : {sw.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  HASH       : 0x{hash:X16}");

        string platformTag = $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}-" +
                             $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        string outFile = Path.Combine(AppContext.BaseDirectory, $"S0_2_hash.{platformTag}.txt");
        File.WriteAllText(outFile, $"0x{hash:X16}\n");

        string baselinePath = Path.Combine(AppContext.BaseDirectory, "S0_2_baseline.txt");
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
            Console.WriteLine("  Baseline   : (not set — copy this run's hash into S0_2_baseline.txt to lock it in)");
        }
        return 0;
    }
}
