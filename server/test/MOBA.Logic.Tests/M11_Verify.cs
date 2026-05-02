// SPDX-License-Identifier: MIT
// M11 verification — 投射物系统、瞬移技能与复活计时器 (PRD §4.2.4 / §7.6 / §7.7).
//
// Asserts:
//   M11.1  火球投射物生成 — TryCast(Mage Q) 后 Projectiles[0].Alive=true，
//           Velocity.X>0（朝 +X aim 方向），ExpireFrame=frame+10。
//   M11.2  旋风斩 AOE 伤害 — Swordsman W (Instant 2m 圆)：2m 内敌方英雄产生
//           DamageEvent；Damage = 60 + 0.5×AD(60) = 90；圆外英雄不受影响。
//   M11.3  闪现瞬移 — TryCast(Mage E，aim=(2,0)) 后施法者 Pos 精确等于 (2,0)。
//   M11.4  陨石延迟 AoE — TryCast(Mage R) 生成 AoeOnExpiry 投射物，ExpireFrame=12；
//           TickProjectiles 第 11 帧无伤害；第 12 帧触发，DamageEvent.Damage=310
//           (250 + 1.2×AP(50))。
//   M11.5  复活计时器公式 — Respawn.FramesFor(lv=1,f=0)=105；
//           Respawn.FramesFor(lv=5,f=900)=255（gameMinute=1）；
//           lv=18@f=9000 钳制 60s→900 帧。

using System;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M11_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M11 Verify");
        BuiltinContent.Register();
        int rc = 0;
        rc |= M11_1_ProjectileSpawn();
        rc |= M11_2_WhirlwindAoeDamage();
        rc |= M11_3_BlinkTeleport();
        rc |= M11_4_MeteorDelayedAoe();
        rc |= M11_5_RespawnTimer();
        if (rc == 0) Console.WriteLine("  PASS  M11 all assertions");
        return rc;
    }

    // ── M11.1  火球投射物生成 ─────────────────────────────────────────────────
    static int M11_1_ProjectileSpawn()
    {
        const string name = "M11.1 Fireball";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        // hero[0] = Mage (Blue team, index 0..4)
        heroes[0].HeroDefId = 1;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Pos   = new TSVector2(Fix64.Zero, Fix64.Zero);
        heroes[0].Mp    = heroes[0].MaxMp;

        var buffs      = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projectiles = new Projectile[SkillSystem.MaxProjectiles];
        Span<GameSystems.DamageEvent> dmgQ = stackalloc GameSystems.DamageEvent[16];
        int dmgCount = 0;

        // Mage Q = slot 0 → skillDefId = HeroSkills[1, 0]
        ushort skillId = BuiltinContent.HeroSkills[1, 0];
        // Aim far in +X direction; will be clamped to CastRange=8m
        TSVector2 aim = new TSVector2((Fix64)20, Fix64.Zero);

        bool cast = SkillSystem.TryCast(heroes, buffs, 0, 0, skillId, aim, projectiles, 0, dmgQ, ref dmgCount);
        int rc = Check($"{name}: TryCast returns true", cast);
        rc |= Check($"{name}: projectile[0].Alive", projectiles[0].Alive);
        rc |= Check($"{name}: velocity toward +X (got {projectiles[0].Velocity.X})", projectiles[0].Velocity.X > Fix64.Zero);
        rc |= Check($"{name}: ExpireFrame=10 (got {projectiles[0].ExpireFrame})", projectiles[0].ExpireFrame == 10);

        if (rc == 0) Console.WriteLine($"  PASS  {name}");
        return rc;
    }

    // ── M11.2  旋风斩 AOE 伤害 ───────────────────────────────────────────────
    static int M11_2_WhirlwindAoeDamage()
    {
        const string name = "M11.2 Whirlwind";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        // hero[0] = Swordsman (Blue), at origin
        heroes[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Pos   = new TSVector2(Fix64.Zero, Fix64.Zero);
        heroes[0].Mp    = heroes[0].MaxMp;

        // hero[5] = Red enemy at (1,0): within 2m AOE radius → should be hit
        heroes[5].HeroDefId = 1;
        BuiltinContent.ApplyBaseStats(ref heroes[5]);
        heroes[5].Alive = true;
        heroes[5].Pos   = new TSVector2((Fix64)1, Fix64.Zero);

        // hero[6] = Red enemy at (3,0): outside 2m → should NOT be hit
        heroes[6].HeroDefId = 1;
        BuiltinContent.ApplyBaseStats(ref heroes[6]);
        heroes[6].Alive = true;
        heroes[6].Pos   = new TSVector2((Fix64)3, Fix64.Zero);

        var buffs      = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projectiles = new Projectile[SkillSystem.MaxProjectiles];
        Span<GameSystems.DamageEvent> dmgQ = stackalloc GameSystems.DamageEvent[16];
        int dmgCount = 0;

        // Swordsman W = slot 1 → skillDefId = HeroSkills[0, 1]; CastType=Instant
        ushort skillId = BuiltinContent.HeroSkills[0, 1];
        // For Instant skills, aim = caster pos centres the AOE on self
        TSVector2 aim = heroes[0].Pos;

        bool cast = SkillSystem.TryCast(heroes, buffs, 0, 1, skillId, aim, projectiles, 0, dmgQ, ref dmgCount);
        int rc = Check($"{name}: TryCast returns true", cast);

        // Damage = Param(60) + Ad(60) * Param2(500)/1000 = 60 + 30 = 90
        Fix64 expected90 = (Fix64)90;

        int hitIn = 0, hitOut = 0;
        for (int i = 0; i < dmgCount; i++)
        {
            if (dmgQ[i].Target.Kind == UnitKind.Hero && dmgQ[i].Target.Index == 5) hitIn++;
            if (dmgQ[i].Target.Kind == UnitKind.Hero && dmgQ[i].Target.Index == 6) hitOut++;
        }
        rc |= Check($"{name}: hero[5] hit (1m, inside 2m)", hitIn == 1);
        rc |= Check($"{name}: hero[6] not hit (3m, outside 2m)", hitOut == 0);

        for (int i = 0; i < dmgCount; i++)
        {
            if (dmgQ[i].Target.Kind == UnitKind.Hero && dmgQ[i].Target.Index == 5)
            {
                rc |= Check($"{name}: damage=90 (got {dmgQ[i].Damage})", dmgQ[i].Damage == expected90);
                break;
            }
        }

        if (rc == 0) Console.WriteLine($"  PASS  {name}");
        return rc;
    }

    // ── M11.3  闪现瞬移 ──────────────────────────────────────────────────────
    static int M11_3_BlinkTeleport()
    {
        const string name = "M11.3 Blink";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        heroes[0].HeroDefId = 1; // Mage
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Pos   = new TSVector2(Fix64.Zero, Fix64.Zero);
        heroes[0].Mp    = heroes[0].MaxMp;

        var buffs      = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projectiles = new Projectile[SkillSystem.MaxProjectiles];
        Span<GameSystems.DamageEvent> dmgQ = stackalloc GameSystems.DamageEvent[16];
        int dmgCount = 0;

        // Mage E = slot 2 → skillDefId = HeroSkills[1, 2]; CastType=Direction, CastRange=3m
        ushort skillId = BuiltinContent.HeroSkills[1, 2];
        // aim (2,0): Manhattan distance = 2 ≤ CastRange=3 → no clamping
        TSVector2 aim = new TSVector2((Fix64)2, Fix64.Zero);

        bool cast = SkillSystem.TryCast(heroes, buffs, 0, 2, skillId, aim, projectiles, 0, dmgQ, ref dmgCount);
        int rc = Check($"{name}: TryCast returns true", cast);
        // Teleport (non-reverse): caster.Pos = aim = (2,0)
        rc |= Check($"{name}: Pos.X=2 (got {heroes[0].Pos.X})", heroes[0].Pos.X == (Fix64)2);
        rc |= Check($"{name}: Pos.Y=0 (got {heroes[0].Pos.Y})", heroes[0].Pos.Y == Fix64.Zero);

        if (rc == 0) Console.WriteLine($"  PASS  {name}");
        return rc;
    }

    // ── M11.4  陨石延迟 AoE ──────────────────────────────────────────────────
    static int M11_4_MeteorDelayedAoe()
    {
        const string name = "M11.4 Meteor";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        // hero[0] = Mage (Blue), AP=50
        heroes[0].HeroDefId = 1;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Pos   = new TSVector2(Fix64.Zero, Fix64.Zero);
        heroes[0].Mp    = heroes[0].MaxMp;

        // hero[5] = Red enemy at (1,0), within 3m AOE radius of aim=(0,0)
        heroes[5].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[5]);
        heroes[5].Alive = true;
        heroes[5].Pos   = new TSVector2((Fix64)1, Fix64.Zero);

        var buffs      = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projectiles = new Projectile[SkillSystem.MaxProjectiles];
        Span<GameSystems.DamageEvent> dmgQ = stackalloc GameSystems.DamageEvent[16];
        int dmgCount = 0;

        // Mage R = slot 3 → skillDefId = HeroSkills[1, 3]; CastType=Position
        ushort skillId = BuiltinContent.HeroSkills[1, 3];
        // aim at caster pos (0,0) — definitely within CastRange=10m
        TSVector2 aim = new TSVector2(Fix64.Zero, Fix64.Zero);

        bool cast = SkillSystem.TryCast(heroes, buffs, 0, 3, skillId, aim, projectiles, 0, dmgQ, ref dmgCount);
        int rc = Check($"{name}: TryCast returns true", cast);
        rc |= Check($"{name}: AoeOnExpiry projectile spawned", projectiles[0].Alive && projectiles[0].AoeOnExpiry);
        rc |= Check($"{name}: ExpireFrame=12 (got {projectiles[0].ExpireFrame})", projectiles[0].ExpireFrame == 12);
        rc |= Check($"{name}: no immediate damage (got {dmgCount})", dmgCount == 0);

        var minions = new Minion[0];
        var towers  = new Tower[0];
        Fix64 dt = (Fix64)1 / (Fix64)15;

        // Frame 11: projectile not yet expired (ExpireFrame=12)
        dmgCount = 0;
        SkillSystem.TickProjectiles(projectiles, heroes, minions, towers, dt, 11, dmgQ, ref dmgCount, buffs);
        rc |= Check($"{name}: no damage at f=11 (got {dmgCount})", dmgCount == 0);
        rc |= Check($"{name}: projectile alive at f=11", projectiles[0].Alive);

        // Frame 12: fires (frame >= ExpireFrame)
        dmgCount = 0;
        SkillSystem.TickProjectiles(projectiles, heroes, minions, towers, dt, 12, dmgQ, ref dmgCount, buffs);
        rc |= Check($"{name}: projectile expired at f=12", !projectiles[0].Alive);
        rc |= Check($"{name}: ≥1 DamageEvent at f=12 (got {dmgCount})", dmgCount >= 1);

        // Replicate ComputeDamage arithmetic exactly to avoid Fix64 rounding mismatch:
        // ComputeDamage = Param(250) + Ad*(Param2(0)/1000) + Ap*(Param3(1200)/1000)
        //               = 250 + 0 + AP*(1200/1000)  → 1200/1000 is not exactly 1.2 in binary
        Fix64 expectedDmg = (Fix64)250 + heroes[0].Ap * ((Fix64)1200 / (Fix64)1000);
        if (dmgCount >= 1)
            rc |= Check($"{name}: damage≈310 (got {dmgQ[0].Damage})", dmgQ[0].Damage == expectedDmg);

        if (rc == 0) Console.WriteLine($"  PASS  {name}");
        return rc;
    }

    // ── M11.5  复活计时器公式 ────────────────────────────────────────────────
    static int M11_5_RespawnTimer()
    {
        const string name = "M11.5 RespawnTimer";
        // PRD §7.6: baseSec = level*2+5; total = baseSec + gameMinute*2; cap 60s → frames = totalSec*15.

        // lv=1, f=0: gameMinute=0/(15*60)=0, baseSec=7, total=7s, frames=105
        int frames_lv1_f0 = Respawn.FramesFor(1, 0u);
        int rc = Check($"{name}: lv=1 @f=0 → 105 (got {frames_lv1_f0})", frames_lv1_f0 == 105);

        // lv=5, f=900: gameMinute=900/900=1, baseSec=15, total=17s, frames=255
        int frames_lv5_f900 = Respawn.FramesFor(5, 900u);
        rc |= Check($"{name}: lv=5 @f=900 → 255 (got {frames_lv5_f900})", frames_lv5_f900 == 255);

        // lv=18, f=9000: gameMinute=10, baseSec=41, total=61>60 → cap 60s=900 frames
        int frames_cap = Respawn.FramesFor(18, 9000u);
        rc |= Check($"{name}: lv=18 @f=9000 → cap 900 (got {frames_cap})", frames_cap == 900);

        if (rc == 0) Console.WriteLine($"  PASS  {name}");
        return rc;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    static int Check(string label, bool cond)
    {
        if (!cond) { Console.WriteLine($"  FAIL  {label}"); return 1; }
        return 0;
    }
}
