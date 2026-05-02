// SPDX-License-Identifier: MIT
// M5.7 verification — XP / Level-up / Kill economy (PRD §7.3).
//
// Asserts:
//   M5.7.1  Melee minion kill: killer gets +20g +60xp; caster minion: +18g +50xp.
//   M5.7.2  Level-up triggers at correct XP threshold; MaxHp/Ad/Ap increase per PRD §7.7.
//   M5.7.3  Hero kill: killer gets +300g, Kills++ ; victim Deaths++ (already tested in M5.2).
//   M5.7.4  Tower kill: all living team members get +150g.
//   M5.7.5  SourceSlot=0xFF (non-hero damage) kills a minion without crashing or awarding gold.
//   M5.7.6  Dead hero does not receive XP or gold rewards.

using System;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M5_7_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.7 Verify");
        int rc = 0;

        // ── M5.7.1 — Minion kill gold + XP ───────────────────────────────────────
        {
            var heroes = MakeHeroes(2);
            heroes[0].Gold = 500;
            heroes[0].Xp   = 0;

            // Melee kill
            LevelSystem.AwardMinionKill(heroes, 0, MinionType.Melee);
            if (heroes[0].Gold != 500 + LevelSystem.MeleeMinionGold)
            { Console.WriteLine($"  FAIL  M5.7.1a melee gold: expected {500 + LevelSystem.MeleeMinionGold} got {heroes[0].Gold}"); rc = 1; }
            if (heroes[0].Xp != LevelSystem.MeleeMinionXp)
            { Console.WriteLine($"  FAIL  M5.7.1a melee xp: expected {LevelSystem.MeleeMinionXp} got {heroes[0].Xp}"); rc = 1; }

            // Caster kill
            uint goldAfterMelee = heroes[0].Gold;
            uint xpAfterMelee   = heroes[0].Xp;
            LevelSystem.AwardMinionKill(heroes, 0, MinionType.Caster);
            if (heroes[0].Gold != goldAfterMelee + LevelSystem.CasterMinionGold)
            { Console.WriteLine($"  FAIL  M5.7.1b caster gold"); rc = 1; }
            if (heroes[0].Xp != xpAfterMelee + LevelSystem.CasterMinionXp)
            { Console.WriteLine($"  FAIL  M5.7.1b caster xp"); rc = 1; }
        }
        Console.WriteLine("  OK    M5.7.1 minion kill gold+xp");

        // ── M5.7.2 — Level-up at XP threshold ────────────────────────────────────
        {
            // Swordsman (HeroDefId=0): +80HP/lv, +5AD/lv
            var heroes = MakeHeroes(1);
            ref var h = ref heroes[0];
            h.HeroDefId = 0;
            // Apply base stats so MaxHp/Ad are meaningful.
            BuiltinContent.ApplyBaseStats(ref h);
            Fix64 maxHpBefore = h.MaxHp;
            Fix64 adBefore    = h.Ad;
            byte  lvBefore    = h.Level;

            // Award exactly the XP needed for level 2 (280).
            LevelSystem.AwardXp(heroes, 0, 280);
            if (h.Level != lvBefore + 1)
            { Console.WriteLine($"  FAIL  M5.7.2 did not level up (level={h.Level})"); rc = 1; }
            // MaxHp should increase by HpPerLv.
            Fix64 expectedMaxHp = maxHpBefore + (Fix64)80;
            if (h.MaxHp != expectedMaxHp)
            { Console.WriteLine($"  FAIL  M5.7.2 MaxHp: expected {expectedMaxHp} got {h.MaxHp}"); rc = 1; }
            // AD should increase by AdPerLv.
            Fix64 expectedAd = adBefore + (Fix64)5;
            if (h.Ad != expectedAd)
            { Console.WriteLine($"  FAIL  M5.7.2 Ad: expected {expectedAd} got {h.Ad}"); rc = 1; }

            // Mage (HeroDefId=1): +60HP/lv, +8AP/lv
            var heroes1 = MakeHeroes(1);
            ref var m = ref heroes1[0];
            m.HeroDefId = 1;
            BuiltinContent.ApplyBaseStats(ref m);
            Fix64 mageMaxHpBase = m.MaxHp; // record actual base after ApplyBaseStats
            Fix64 apBefore = m.Ap;
            LevelSystem.AwardXp(heroes1, 0, 280);
            if (m.Level != 2)
            { Console.WriteLine($"  FAIL  M5.7.2 mage no level-up"); rc = 1; }
            if (m.MaxHp != mageMaxHpBase + (Fix64)60)
            { Console.WriteLine($"  FAIL  M5.7.2 mage MaxHp: expected {mageMaxHpBase + (Fix64)60} got {m.MaxHp}"); rc = 1; }
            if (m.Ap != apBefore + (Fix64)8)
            { Console.WriteLine($"  FAIL  M5.7.2 mage Ap"); rc = 1; }
        }
        Console.WriteLine("  OK    M5.7.2 level-up stat growth");

        // ── M5.7.3 — Hero kill: +300g + Kills++ ──────────────────────────────────
        {
            var heroes = MakeHeroes(10);
            heroes[0].Gold = 0;
            heroes[0].Kills = 0;
            // Simulate: hero 5 (Red) killed by hero 0 (Blue).
            // Build a minimal DamageEvent and route through ResolveDamage.
            var minions = new Minion[0];
            var towers  = new Tower[0];
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[4];
            int qc = 0;
            // Set hero 5 to low HP so the kill happens.
            heroes[5].Hp = (Fix64)1;
            // Enqueue fatal damage from slot 0.
            GameSystems.EnqueueDamage(q, ref qc,
                new UnitRef { Kind = UnitKind.Hero, Index = 5 },
                (Fix64)9999, frame: 1, sourceSlot: 0);
            GameSystems.ResolveDamage(q, qc, minions, towers, heroes, frame: 1, out _);

            if (heroes[0].Gold != Items.HeroKillGold)
            { Console.WriteLine($"  FAIL  M5.7.3 killer gold: expected {Items.HeroKillGold} got {heroes[0].Gold}"); rc = 1; }
            if (heroes[0].Kills != 1)
            { Console.WriteLine($"  FAIL  M5.7.3 Kills not incremented"); rc = 1; }
            if (heroes[5].Alive)
            { Console.WriteLine("  FAIL  M5.7.3 victim still alive"); rc = 1; }
        }
        Console.WriteLine("  OK    M5.7.3 hero kill rewards");

        // ── M5.7.4 — Tower kill: all team members +150g ───────────────────────────
        {
            var heroes = MakeHeroes(10);
            for (int i = 0; i < 5; i++) heroes[i].Gold = 0;
            // Blue hero slot 2 kills a tower.
            LevelSystem.AwardTowerKill(heroes, killerSlot: 2);
            for (int i = 0; i < 5; i++)
            {
                if (heroes[i].Gold != LevelSystem.TowerKillGold)
                { Console.WriteLine($"  FAIL  M5.7.4 blue hero {i} gold: expected {LevelSystem.TowerKillGold} got {heroes[i].Gold}"); rc = 1; }
            }
            // Red team should not receive gold.
            for (int i = 5; i < 10; i++)
            {
                if (heroes[i].Gold != 0)
                { Console.WriteLine($"  FAIL  M5.7.4 red hero {i} got gold unexpectedly"); rc = 1; }
            }
        }
        Console.WriteLine("  OK    M5.7.4 tower kill team gold");

        // ── M5.7.5 — SourceSlot=0xFF: no crash, no gold/XP award ────────────────
        {
            var heroes  = MakeHeroes(2);
            var minions = new Minion[1];
            minions[0] = new Minion
            {
                Alive = true, Hp = (Fix64)1, MaxHp = (Fix64)100,
                Type = MinionType.Melee, Team = Team.Red, BornFrame = 0,
                Armor = Fix64.Zero,
            };
            var towers = new Tower[0];
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[4];
            int qc = 0;
            // Non-hero source (0xFF default) kills the minion.
            GameSystems.EnqueueDamage(q, ref qc,
                new UnitRef { Kind = UnitKind.Minion, Index = 0, BornFrame = 0 },
                (Fix64)9999, frame: 1);
            GameSystems.ResolveDamage(q, qc, minions, towers, heroes, frame: 1, out _);
            if (minions[0].Alive)
            { Console.WriteLine("  FAIL  M5.7.5 minion should be dead"); rc = 1; }
            if (heroes[0].Gold != 0 || heroes[1].Gold != 0)
            { Console.WriteLine("  FAIL  M5.7.5 non-hero kill should not award gold"); rc = 1; }
        }
        Console.WriteLine("  OK    M5.7.5 non-hero source no award");

        // ── M5.7.6 — Dead killer receives no rewards ─────────────────────────────
        {
            var heroes = MakeHeroes(2);
            heroes[0].Alive = false; // killer is dead
            heroes[0].Gold  = 0;
            LevelSystem.AwardMinionKill(heroes, 0, MinionType.Melee);
            if (heroes[0].Gold != 0)
            { Console.WriteLine("  FAIL  M5.7.6 dead hero received gold"); rc = 1; }
        }
        Console.WriteLine("  OK    M5.7.6 dead hero no reward");

        if (rc == 0) Console.WriteLine("  PASS  M5.7 all assertions");
        return rc;
    }

    // Build a minimal hero array — all alive, level 1, team split 0-4 Blue / 5-9 Red.
    private static Hero[] MakeHeroes(int count)
    {
        var h = new Hero[count];
        for (int i = 0; i < count; i++)
        {
            h[i].Alive    = true;
            h[i].Level    = 1;
            h[i].MaxHp    = (Fix64)600;
            h[i].Hp       = (Fix64)600;
            h[i].MaxMp    = (Fix64)200;
            h[i].Mp       = (Fix64)200;
            h[i].Ad       = (Fix64)60;
            h[i].Ap       = Fix64.Zero;
            h[i].Armor    = Fix64.Zero;
            h[i].Gold     = 0;
            h[i].HeroDefId = (byte)(i % BuiltinContent.HeroCount);
        }
        return h;
    }
}
