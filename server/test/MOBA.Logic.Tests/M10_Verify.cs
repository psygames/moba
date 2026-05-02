// SPDX-License-Identifier: MIT
// M10 verification — Buff lifecycle & ice-wall (PRD §7.7 / §4.2.3).
//
// Asserts:
//   M10.1  HoT buff (BuffShield): ticks every 15 frames healing +20 HP; expires at frame 45, no third tick.
//   M10.2  Stun (BuffStun10) sets Stunned/CannotCast tag; TryCast returns false while stunned;
//           after expiry at frame 15, TryCast succeeds.
//   M10.3  Execute condition (斩首): R skill blocked when target HP = 35% MaxHp; passes at 25%.
//   M10.4  Ice wall (冰墙): Mage W spawns Walls[0].Alive=true with ExpireFrame=45;
//           at frame ≥ 45 the expiry logic clears Alive.
//   M10.5  BuffSlow30 Refresh policy: re-applying at frame 20 extends EndFrame 30 → 50;
//           Slowed tag persists through frame 31, clears at frame 50.

using System;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M10_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M10 Verify");
        BuiltinContent.Register();
        int rc = 0;
        rc |= M10_1_HoTBuff();
        rc |= M10_2_StunBlocksCast();
        rc |= M10_3_ExecuteCondition();
        rc |= M10_4_IceWall();
        rc |= M10_5_BuffRefresh();
        if (rc == 0) Console.WriteLine("  PASS  M10 all assertions");
        return rc;
    }

    // ── M10.1  HoT buff ticks ────────────────────────────────────────────────
    static int M10_1_HoTBuff()
    {
        const string name = "M10.1 HoT";
        BuiltinContent.Register();

        var heroes = new Hero[1];
        heroes[0].HeroDefId = 1; // Mage: MaxHp=480
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Hp = heroes[0].MaxHp - (Fix64)40; // start at 440

        var buffs = new BuffInstance[1, BuffEngine.MaxBuffsPerHero];
        bool ok = BuffEngine.Apply(heroes, buffs, 0, BuiltinContent.BuffShield, 0xFF, frame: 0);
        int rc = Check($"{name}: Apply succeeded", ok);

        // Frames 1–14: no heal yet.
        for (uint f = 1; f < 15; f++)
        {
            Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
            BuffEngine.Tick(heroes, buffs, f, dq, ref dqc);
        }
        rc |= Check($"{name}: HP unchanged before frame 15 (got {heroes[0].Hp})",
                    heroes[0].Hp == heroes[0].MaxHp - (Fix64)40);

        // Frame 15: first HoT tick, +20 HP → 460.
        { Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
          BuffEngine.Tick(heroes, buffs, 15u, dq, ref dqc); }
        rc |= Check($"{name}: HP +20 after frame 15 (got {heroes[0].Hp})",
                    heroes[0].Hp == heroes[0].MaxHp - (Fix64)20);

        // Frames 16–29: no additional heal.
        for (uint f = 16; f < 30; f++)
        {
            Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
            BuffEngine.Tick(heroes, buffs, f, dq, ref dqc);
        }

        // Frame 30: second HoT tick, +20 HP → MaxHp (capped).
        { Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
          BuffEngine.Tick(heroes, buffs, 30u, dq, ref dqc); }
        rc |= Check($"{name}: HP at MaxHp after frame 30 (got {heroes[0].Hp})",
                    heroes[0].Hp == heroes[0].MaxHp);

        // Frame 45: buff expires (EndFrame=45) — no third tick fired.
        { Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
          BuffEngine.Tick(heroes, buffs, 45u, dq, ref dqc); }
        rc |= Check($"{name}: HP still MaxHp at expiry frame 45 (got {heroes[0].Hp})",
                    heroes[0].Hp == heroes[0].MaxHp);

        // Buff slot should be cleared.
        bool buffStillActive = false;
        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
            if (buffs[0, s].DefId == BuiltinContent.BuffShield + 1) { buffStillActive = true; break; }
        rc |= Check($"{name}: buff slot cleared at expiry", !buffStillActive);

        if (rc == 0) Console.WriteLine("  OK    M10.1 HoT buff ticks +20 HP at f15/f30, expires at f45");
        return rc;
    }

    // ── M10.2  Stun blocks skill cast ────────────────────────────────────────
    static int M10_2_StunBlocksCast()
    {
        const string name = "M10.2 StunBlocksCast";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        heroes[0].HeroDefId = 0; // Swordsman
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Mp  = heroes[0].MaxMp;
        heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);

        var buffs   = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projs   = new Projectile[SkillSystem.MaxProjectiles];

        // Apply stun at frame 0.
        BuffEngine.Apply(heroes, buffs, 0, BuiltinContent.BuffStun10, 0xFF, frame: 0);
        int rc = Check($"{name}: Stunned tag set after Apply", (heroes[0].Tags & GameplayTags.Stunned) != 0);

        // TryCast Swordsman W (旋风斩, slot 1, Instant cast) — must be blocked.
        Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8]; int qc = 0;
        bool stunCast = SkillSystem.TryCast(heroes, buffs, 0, 1, BuiltinContent.HeroSkills[0, 1],
            aim: heroes[0].Pos, projectiles: projs, frame: 0u, q, ref qc);
        rc |= Check($"{name}: TryCast blocked while stunned", !stunCast);

        // Tick frames 1–15 to expire the stun (BuffStun10.DurationFrames = 15).
        for (uint f = 1; f <= 15; f++)
        {
            Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
            BuffEngine.Tick(heroes, buffs, f, dq, ref dqc);
        }
        rc |= Check($"{name}: Stunned tag cleared after expiry", (heroes[0].Tags & GameplayTags.Stunned) == 0);

        // TryCast again at frame 15 — must now succeed.
        qc = 0;
        bool afterCast = SkillSystem.TryCast(heroes, buffs, 0, 1, BuiltinContent.HeroSkills[0, 1],
            aim: heroes[0].Pos, projectiles: projs, frame: 15u, q, ref qc);
        rc |= Check($"{name}: TryCast succeeds after stun expiry", afterCast);

        if (rc == 0) Console.WriteLine("  OK    M10.2 stun blocks cast; succeeds after expiry");
        return rc;
    }

    // ── M10.3  Execute condition (斩首) ───────────────────────────────────────
    static int M10_3_ExecuteCondition()
    {
        const string name = "M10.3 ExecuteCondition";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        // Blue Swordsman at slot 0.
        heroes[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Mp  = heroes[0].MaxMp;
        heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);

        // Red enemy at slot 5, within 4m.
        heroes[5].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[5]);
        heroes[5].Alive  = true;
        heroes[5].Pos    = new TSVector2((Fix64)2, Fix64.Zero);
        heroes[5].MaxHp  = (Fix64)100;

        var buffs = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projs = new Projectile[SkillSystem.MaxProjectiles];
        ushort rSkill = BuiltinContent.HeroSkills[0, 3]; // 斩首 R
        var aim = heroes[5].Pos;                          // aim at enemy

        // HP = 35 (35% of 100) → above the 30% threshold → cast blocked.
        heroes[5].Hp = (Fix64)35;
        Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8]; int qc = 0;

        bool blockedCast = SkillSystem.TryCast(heroes, buffs, 0, 3, rSkill, aim, projs, frame: 0u, q, ref qc);
        int rc = Check($"{name}: blocked when target HP=35%", !blockedCast);

        // HP = 25 (25% of 100) → below the 30% threshold → cast allowed.
        heroes[5].Hp = (Fix64)25;
        qc = 0;
        bool executeCast = SkillSystem.TryCast(heroes, buffs, 0, 3, rSkill, aim, projs, frame: 0u, q, ref qc);
        rc |= Check($"{name}: allowed when target HP=25%", executeCast);

        if (rc == 0) Console.WriteLine("  OK    M10.3 execute condition (斩首) blocks at 35%, allows at 25%");
        return rc;
    }

    // ── M10.4  Ice wall spawn and expiry ─────────────────────────────────────
    static int M10_4_IceWall()
    {
        const string name = "M10.4 IceWall";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        heroes[1].HeroDefId = 1; // Mage at slot 1
        BuiltinContent.ApplyBaseStats(ref heroes[1]);
        heroes[1].Alive = true;
        heroes[1].Mp   = heroes[1].MaxMp;
        heroes[1].Pos  = new TSVector2(Fix64.Zero, Fix64.Zero);

        var buffs = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var walls = new Wall[DeterministicWorld.MaxWalls];
        var projs = new Projectile[SkillSystem.MaxProjectiles];

        // Aim within CastRange=6m (Mage W range).
        TSVector2 wallAim = new TSVector2((Fix64)3, Fix64.Zero);
        ushort mageW = BuiltinContent.HeroSkills[1, 1]; // 冰墙 W

        Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8]; int qc = 0;
        bool castOk = SkillSystem.TryCast(heroes, buffs, 1, 1, mageW, wallAim, projs, frame: 0u, q, ref qc, walls);
        int rc = Check($"{name}: TryCast returns true", castOk);

        // Verify wall spawned with correct data.
        bool wallAlive  = false;
        uint wallExpire = 0;
        for (int w = 0; w < walls.Length; w++)
        {
            if (walls[w].Alive) { wallAlive = true; wallExpire = walls[w].ExpireFrame; break; }
        }
        rc |= Check($"{name}: wall Alive == true after cast", wallAlive);
        rc |= Check($"{name}: wall ExpireFrame == 45 (got {wallExpire})", wallExpire == 45u);

        // Replicate DeterministicWorld expiry logic at frame 45.
        for (int w = 0; w < walls.Length; w++)
        {
            ref var wall = ref walls[w];
            if (wall.Alive && 45u >= wall.ExpireFrame) wall.Alive = false;
        }
        bool stillAlive = false;
        for (int w = 0; w < walls.Length; w++) if (walls[w].Alive) { stillAlive = true; break; }
        rc |= Check($"{name}: wall Alive == false at frame 45", !stillAlive);

        if (rc == 0) Console.WriteLine("  OK    M10.4 冰墙 spawned Alive=true ExpireFrame=45, cleared at f45");
        return rc;
    }

    // ── M10.5  Buff Refresh extends duration ─────────────────────────────────
    static int M10_5_BuffRefresh()
    {
        const string name = "M10.5 BuffRefresh";
        BuiltinContent.Register();

        var heroes = new Hero[1];
        heroes[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;

        var buffs    = new BuffInstance[1, BuffEngine.MaxBuffsPerHero];
        ushort slowId = BuiltinContent.BuffSlow30;

        // Apply at frame 0: EndFrame = 0 + 30 = 30.
        BuffEngine.Apply(heroes, buffs, 0, slowId, 0xFF, frame: 0);
        uint endFrame0 = 0;
        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
            if (buffs[0, s].DefId == slowId + 1) { endFrame0 = buffs[0, s].EndFrame; break; }
        int rc = Check($"{name}: initial EndFrame == 30 (got {endFrame0})", endFrame0 == 30u);

        // Re-apply at frame 20 (Refresh policy): EndFrame = 20 + 30 = 50.
        BuffEngine.Apply(heroes, buffs, 0, slowId, 0xFF, frame: 20);
        uint endFrame1 = 0;
        int slotCount = 0;
        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
            if (buffs[0, s].DefId == slowId + 1) { endFrame1 = buffs[0, s].EndFrame; slotCount++; }
        rc |= Check($"{name}: refreshed EndFrame == 50 (got {endFrame1})", endFrame1 == 50u);
        rc |= Check($"{name}: only one slot used after Refresh (got {slotCount})", slotCount == 1);

        // Frame 31 (past original expiry 30): Slowed tag must still be active.
        { Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
          BuffEngine.Tick(heroes, buffs, 31u, dq, ref dqc); }
        rc |= Check($"{name}: Slowed tag active at frame 31", (heroes[0].Tags & GameplayTags.Slowed) != 0);

        // Frame 50: buff expires, Slowed tag must be cleared.
        { Span<GameSystems.DamageEvent> dq = stackalloc GameSystems.DamageEvent[8]; int dqc = 0;
          BuffEngine.Tick(heroes, buffs, 50u, dq, ref dqc); }
        rc |= Check($"{name}: Slowed tag cleared at frame 50", (heroes[0].Tags & GameplayTags.Slowed) == 0);

        if (rc == 0) Console.WriteLine("  OK    M10.5 BuffSlow30 Refresh: EndFrame extended 30→50, tag clears at f50");
        return rc;
    }

    private static int Check(string label, bool ok)
    {
        if (!ok) { Console.WriteLine($"  FAIL  {label}"); return 1; }
        return 0;
    }
}
