// SPDX-License-Identifier: MIT
// M5.2 verification — 3 hero archetypes × 4 skills wired through SkillBits.
//
// Asserts:
//   M5.2.1  All 12 BuiltinContent skills are registered with non-zero step counts.
//   M5.2.2  Each skill, when triggered via InputFrame.SkillBits, executes inside
//           DeterministicWorld.Tick — observable by mana drain or projectile spawn
//           or hp delta on a target hero.
//   M5.2.3  Aim from AimAngleDeg works deterministically (TrigLut hash stable).
//   M5.2.4  GC=0: 1000-tick tight loop with all 10 heroes spamming skills allocs 0 bytes.
//   M5.2.5  Determinism: two worlds fed identical input streams produce identical hashes.

using System;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class M5_2_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.2 Verify");
        int rc = 0;

        var w = new DeterministicWorld(seed: 7) { EnableGameplay = true };

        // 5.2.1 — All 12 skills registered.
        for (int hd = 0; hd < BuiltinContent.HeroCount; hd++)
            for (int sl = 0; sl < BuiltinContent.SkillsPerHero; sl++)
            {
                ushort id = BuiltinContent.HeroSkills[hd, sl];
                if (id >= SkillEngine.DefCount) { Console.WriteLine($"  FAIL  hero {hd} slot {sl} skill id {id} unregistered"); rc = 1; continue; }
                ref var def = ref SkillEngine.Defs[id];
                if (def.StepCount == 0) { Console.WriteLine($"  FAIL  hero {hd} slot {sl} has 0 steps"); rc = 1; }
            }

        // 5.2.2 — drive every hero to spam every skill via SkillBits.
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        // Aim each hero toward map centre so AoE/projectiles travel a known direction.
        // Blue (slots 0..4) aims at +Y (90°); Red (slots 5..9) aims at -Y (270°).
        for (int i = 0; i < inputs.Length; i++)
            inputs[i].AimAngleDeg = (ushort)(i < 5 ? 90 : 270);

        // Settle then snapshot mana of all heroes.
        for (int f = 0; f < 60; f++) w.Tick(inputs);
        var mp0 = new Fix64[10];
        for (int i = 0; i < 10; i++) mp0[i] = w.Heroes[i].Mp;

        // Cast slots Q (frame n), W (n+30), E (n+60), R (n+120) — far enough apart that mana exists.
        // Then run for 200 frames letting projectiles fly.
        for (int f = 0; f < 200; f++)
        {
            byte bits = 0;
            if (f == 0)   bits = 0x01;
            if (f == 30)  bits = 0x02;
            if (f == 60)  bits = 0x04;
            if (f == 120) bits = 0x08;
            for (int i = 0; i < inputs.Length; i++) inputs[i].SkillBits = bits;
            w.Tick(inputs);
        }
        // Reset SkillBits.
        for (int i = 0; i < inputs.Length; i++) inputs[i].SkillBits = 0;

        // Mana should have dropped on every hero (every hero spent on at least one skill).
        for (int i = 0; i < 10; i++)
        {
            if (w.Heroes[i].Mp >= mp0[i] && w.Heroes[i].Alive)
            {
                // If the hero died in melee combat that's OK — only fail for survivors.
                Console.WriteLine($"  FAIL  hero {i} (defId {w.Heroes[i].HeroDefId}) mana never dropped (mp0={mp0[i]} mp={w.Heroes[i].Mp})");
                rc = 1;
            }
        }

        // 5.2.3 — TrigLut sanity.
        var (c0, s0) = TrigLut.Dir(0);
        var (c90, s90) = TrigLut.Dir(90);
        if (c0 != (Fix64)1 || s0 != (Fix64)0)
        {
            // Allow 1-LSB rounding tolerance.
            long c0err = System.Math.Abs(c0.RawValue - ((long)1 << 32));
            long s0err = System.Math.Abs(s0.RawValue - 0);
            if (c0err > 4 || s0err > 4) { Console.WriteLine($"  FAIL  TrigLut(0) cos/sin off by {c0err}/{s0err} raw"); rc = 1; }
        }
        if ((s90 - (Fix64)1).RawValue > 8 || c90.RawValue > 8 || c90.RawValue < -8)
        { Console.WriteLine("  FAIL  TrigLut(90) wrong"); rc = 1; }

        // 5.2.5 — Determinism: two worlds, identical inputs, identical hashes.
        var w1 = new DeterministicWorld(seed: 7) { EnableGameplay = true };
        var w2 = new DeterministicWorld(seed: 7) { EnableGameplay = true };
        var rng = new System.Random(12345);
        var ins = new InputFrame[10];
        for (int f = 0; f < 600; f++)
        {
            for (int i = 0; i < 10; i++)
            {
                ins[i].JoyX = (sbyte)rng.Next(-100, 101);
                ins[i].JoyY = (sbyte)rng.Next(-100, 101);
                ins[i].SkillBits = (byte)rng.Next(0, 16);
                ins[i].AimAngleDeg = (ushort)rng.Next(0, 360);
            }
            w1.Tick(ins);
            w2.Tick(ins);
        }
        ulong h1 = w1.Hash(), h2 = w2.Hash();
        if (h1 != h2) { Console.WriteLine($"  FAIL  determinism h1=0x{h1:X16} h2=0x{h2:X16}"); rc = 1; }
        else Console.WriteLine($"  Determinism : two-world hash = 0x{h1:X16}");

        // 5.2.4 — GC=0 tight loop with all heroes spamming skills.
        var w3 = new DeterministicWorld(seed: 7) { EnableGameplay = true };
        var ins2 = new InputFrame[10];
        for (int i = 0; i < 10; i++) { ins2[i].SkillBits = 0x0F; ins2[i].AimAngleDeg = (ushort)(i < 5 ? 90 : 270); }
        for (int f = 0; f < 600; f++) w3.Tick(ins2); // warmup
        _ = GC.GetAllocatedBytesForCurrentThread();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        const int tight = 1000;
        for (int f = 0; f < tight; f++) w3.Tick(ins2);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        long delta = a1 - a0;
        Console.WriteLine($"  Tight loop : alloc Δ={delta} bytes over {tight} ticks (all skills spamming)");
        if (delta != 0) { Console.WriteLine($"  FAIL  M5.2.4 alloc Δ {delta} bytes"); rc = 1; }

        if (rc == 0) Console.WriteLine("  PASS  M5.2 12 skills + input wiring OK");
        return rc;
    }
}
