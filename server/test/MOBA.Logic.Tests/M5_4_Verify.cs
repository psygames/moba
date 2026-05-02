// SPDX-License-Identifier: MIT
// M5.4 verification — vision / fog of war.
//
// Asserts:
//   M5.4.1  VisionGrid bit get/set/clear correctness on edge cells (0,0), (199,199).
//   M5.4.2  Stamping a circle at the origin lights up the centre cell + the 8-neighbourhood.
//   M5.4.3  Recompute(team, ...) yields a non-empty mask after world initialization.
//   M5.4.4  Two worlds with identical inputs produce identical vision hashes.
//   M5.4.5  GC=0: 500-tick tight loop with vision recompute every frame.
//   M5.4.6  WriteDiff XOR sanity: diff(curr, prev) ⊕ prev == curr.

using System;
using System.IO.Hashing;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M5_4_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.4 Verify");
        int rc = 0;

        // 5.4.1 — bit get/set basics.
        var g = new VisionGrid();
        if (g.Get(0, 0) || g.Get(199, 199)) { Console.WriteLine("  FAIL  fresh grid not empty"); rc = 1; }
        g.Set(0, 0); g.Set(199, 199);
        if (!g.Get(0, 0) || !g.Get(199, 199)) { Console.WriteLine("  FAIL  set not visible via Get"); rc = 1; }
        g.Clear();
        if (g.Get(0, 0) || g.Get(199, 199)) { Console.WriteLine("  FAIL  clear didn't wipe"); rc = 1; }

        // 5.4.2 — circle stamp at origin (world (0,0)) with r=1 → cell (100,100) plus its 8-neighbourhood.
        g.Clear();
        g.StampCircle(Fix64.Zero, Fix64.Zero, (Fix64)1);
        int lit = VisionSystem.VisibleCellCount(g);
        if (lit < 9) { Console.WriteLine($"  FAIL  small circle lit only {lit} cells"); rc = 1; }
        if (!g.Get(100, 100)) { Console.WriteLine("  FAIL  origin cell not lit"); rc = 1; }

        // 5.4.3 — full recompute on a real world.
        var w = new DeterministicWorld(seed: 33) { EnableGameplay = true };
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        for (int t = 0; t < 30; t++) w.Tick(inputs);
        int litB = VisionSystem.VisibleCellCount(w.VisionBlue);
        int litR = VisionSystem.VisibleCellCount(w.VisionRed);
        if (litB == 0 || litR == 0) { Console.WriteLine($"  FAIL  empty vision lit B={litB} R={litR}"); rc = 1; }
        Console.WriteLine($"  vision cells lit: blue={litB} red={litR}");

        // 5.4.4 — two-world determinism (vision is part of the state hash).
        ulong hashA = RunSeeded(seed: 91);
        ulong hashB = RunSeeded(seed: 91);
        if (hashA != hashB) { Console.WriteLine($"  FAIL  vision determinism A=0x{hashA:X16} B=0x{hashB:X16}"); rc = 1; }
        else                  Console.WriteLine($"  determinism hash = 0x{hashA:X16}");

        // 5.4.5 — GC=0 tight loop.
        var w2 = new DeterministicWorld(seed: 17) { EnableGameplay = true };
        for (int t = 0; t < 60; t++) w2.Tick(inputs); // warmup
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 500; t++) w2.Tick(inputs);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        if (delta != 0) { Console.WriteLine($"  FAIL  GC alloc in vision loop = {delta} bytes"); rc = 1; }
        else              Console.WriteLine("  GC=0 over 500 ticks with vision recompute");

        // 5.4.6 — diff XOR roundtrip.
        var prev = new VisionGrid();
        var curr = new VisionGrid();
        // populate two different patterns
        prev.StampCircle((Fix64)5, (Fix64)5, (Fix64)3);
        curr.StampCircle((Fix64)5, (Fix64)5, (Fix64)3);
        curr.StampCircle((Fix64)(-5), (Fix64)(-5), (Fix64)2);
        Span<byte> diff = stackalloc byte[Vision.MaskBytes];
        VisionSystem.WriteDiff(curr, prev, diff);
        // Reconstruct: diff ^ prev == curr.
        bool match = true;
        for (int i = 0; i < Vision.MaskBytes; i++)
            if ((byte)(diff[i] ^ prev.Mask[i]) != curr.Mask[i]) { match = false; break; }
        if (!match) { Console.WriteLine("  FAIL  XOR diff roundtrip"); rc = 1; }

        Console.WriteLine(rc == 0 ? "M5.4 PASS" : "M5.4 FAIL");
        return rc;
    }

    private static ulong RunSeeded(ulong seed)
    {
        var w = new DeterministicWorld(seed) { EnableGameplay = true };
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        for (uint f = 0; f < 200; f++)
        {
            for (int i = 0; i < DeterministicWorld.PlayerCount; i++)
            {
                uint mix = (uint)(seed ^ (f * 2654435761u) ^ ((uint)i * 374761393u));
                inputs[i] = default;
                inputs[i].JoyX = (sbyte)(((mix >> 8) & 0xFF) - 128);
                inputs[i].JoyY = (sbyte)(((mix >> 16) & 0xFF) - 128);
            }
            w.Tick(inputs);
        }
        return w.Hash();
    }
}
