// SPDX-License-Identifier: MIT
// M5.5 verification — crystals, respawn formula, GameOver freeze.
//
// Asserts:
//   M5.5.1  Crystals start alive at fountain positions with full HP.
//   M5.5.2  Killing the blue crystal (via direct ApplyDamage) ends the game with Red winner;
//           subsequent Tick is a no-op (Frame still increments but state frozen).
//   M5.5.3  Respawn timing scales: level 1 @ frame 0 → 7s = 105 frames; cap at 60s.
//   M5.5.4  Killed hero respawns at fountain after timer elapses, with full Hp/Mp.
//   M5.5.5  Two-world determinism with crystal damage interleaved.
//   M5.5.6  GC=0 tight loop with crystal AI active.

using System;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M5_5_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.5 Verify");
        int rc = 0;

        var w = new DeterministicWorld(seed: 1009) { EnableGameplay = true };
        // 5.5.1
        if (!w.Crystals[0].Alive || !w.Crystals[1].Alive) { Console.WriteLine("  FAIL  crystals not alive at start"); rc = 1; }
        if (w.Crystals[0].Hp != CrystalSystem.CrystalMaxHp) { Console.WriteLine("  FAIL  blue crystal hp mismatch"); rc = 1; }
        if (w.Crystals[0].Pos.X != Lanes.BlueTowers[1].X || w.Crystals[0].Pos.Y != Lanes.BlueTowers[1].Y)
        { Console.WriteLine("  FAIL  blue crystal pos mismatch"); rc = 1; }

        // 5.5.2
        // Smash the blue crystal directly with overwhelming damage.
        for (int i = 0; i < 100; i++) CrystalSystem.ApplyDamage(w.Crystals, (int)Team.Blue, (Fix64)1000);
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        // First Tick after death detects winner.
        w.Tick(inputs);
        if (!w.GameOver || w.Winner != Team.Red) { Console.WriteLine($"  FAIL  expected Red win; gameOver={w.GameOver} winner={w.Winner}"); rc = 1; }
        // Second Tick: state should be frozen (frame still increments, but no other state change).
        uint hpBefore = (uint)w.Heroes[0].Hp.RawValue;
        uint frameBefore = w.Frame;
        w.Tick(inputs);
        if (w.Frame != frameBefore + 1) { Console.WriteLine("  FAIL  frame did not increment after game-over"); rc = 1; }
        if ((uint)w.Heroes[0].Hp.RawValue != hpBefore) { Console.WriteLine("  FAIL  hero hp changed after freeze"); rc = 1; }

        // 5.5.3 — respawn formula.
        int f0 = Respawn.FramesFor(level: 1, frame: 0);
        if (f0 != 7 * 15) { Console.WriteLine($"  FAIL  L1@frame0 expected 105, got {f0}"); rc = 1; }
        int fHigh = Respawn.FramesFor(level: 18, frame: 15u * 60u * 30u); // 30 minutes in
        if (fHigh != 60 * 15) { Console.WriteLine($"  FAIL  expected 60s cap, got {fHigh / 15}s"); rc = 1; }

        // 5.5.4 — actual respawn happens at fountain.
        var w2 = new DeterministicWorld(seed: 7) { EnableGameplay = true };
        ref var hero3 = ref w2.Heroes[3];
        hero3.Hp = (Fix64)1;
        // Kill via direct damage event path.
        var q = new GameSystems.DamageEvent[1];
        int qc = 0;
        GameSystems.EnqueueDamage(q, ref qc, new UnitRef { Kind = UnitKind.Hero, Index = 3, BornFrame = 0 }, (Fix64)10000, frame: 0);
        GameSystems.ResolveDamage(q, qc, w2.Minions, w2.Towers, w2.Heroes, frame: 0, out _);
        if (hero3.Alive) { Console.WriteLine("  FAIL  hero did not die"); rc = 1; }
        uint expectRespawn = (uint)Respawn.FramesFor(hero3.Level, 0);
        if (hero3.RespawnFrame != expectRespawn) { Console.WriteLine($"  FAIL  respawn frame got {hero3.RespawnFrame}, want {expectRespawn}"); rc = 1; }
        // Tick forward until past respawn.
        var inputs2 = new InputFrame[DeterministicWorld.PlayerCount];
        for (int t = 0; t <= expectRespawn + 1; t++) w2.Tick(inputs2);
        if (!hero3.Alive) { Console.WriteLine("  FAIL  hero did not respawn"); rc = 1; }
        var fountain = Lanes.BlueTowers[1];
        if (hero3.Pos.X != fountain.X || hero3.Pos.Y != fountain.Y)
        { Console.WriteLine($"  FAIL  hero respawn pos {hero3.Pos.X},{hero3.Pos.Y} != fountain {fountain.X},{fountain.Y}"); rc = 1; }
        if (hero3.Hp != hero3.MaxHp) { Console.WriteLine("  FAIL  respawn hp not full"); rc = 1; }

        // 5.5.5 — two-world determinism.
        ulong hashA = RunSeeded(seed: 7777);
        ulong hashB = RunSeeded(seed: 7777);
        if (hashA != hashB) { Console.WriteLine($"  FAIL  determinism A=0x{hashA:X16} B=0x{hashB:X16}"); rc = 1; }
        else                  Console.WriteLine($"  determinism hash = 0x{hashA:X16}");

        // 5.5.6 — GC=0.
        var w3 = new DeterministicWorld(seed: 31) { EnableGameplay = true };
        var inputs3 = new InputFrame[DeterministicWorld.PlayerCount];
        for (int t = 0; t < 60; t++) w3.Tick(inputs3); // warmup
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 500; t++) w3.Tick(inputs3);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        if (delta != 0) { Console.WriteLine($"  FAIL  GC alloc in crystal/respawn loop = {delta} bytes"); rc = 1; }
        else              Console.WriteLine("  GC=0 over 500 ticks with crystal AI");

        Console.WriteLine(rc == 0 ? "M5.5 PASS" : "M5.5 FAIL");
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
            // At frame 80, smash blue crystal once for good measure.
            if (f == 80) CrystalSystem.ApplyDamage(w.Crystals, (int)Team.Blue, (Fix64)123);
            w.Tick(inputs);
        }
        return w.Hash();
    }
}
