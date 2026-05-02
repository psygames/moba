// SPDX-License-Identifier: MIT
// M8 verification — PRD §7.2/§7.7: attack speed, CDR, passive skill effects.
//
// Asserts:
//   M8.1  Hero attack-CD derived from AttackSpeed (15/AS frames):
//           Swordsman AS=0.7  → CD=21f; Mage AS=0.6 → CD=25f; Marksman AS=0.85 → CD=17f.
//   M8.2  Marksman AttackSpeed grows +0.03 per level-up (PRD §7.7 "AS 0.85(+0.03)").
//   M8.3  破甲 passive (ArmorShred): Swordsman basic attack applies ArmorShred5 buff to target hero.
//   M8.4  狩猎之眼 passive (ConditionalDamageMul): Marksman deals ×1.2 damage to targets <50% HP,
//           normal damage to targets ≥50% HP.
//   M8.5  CDR reduces skill cooldown: hero with CDR=20 casts 90f-CD skill → CD ends at frame 72.

using System;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M8_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M8 Verify");
        BuiltinContent.Register();
        int rc = 0;
        rc |= M8_1_AttackSpeed();
        rc |= M8_2_MarksmanAsGrowth();
        rc |= M8_3_ArmorShredPassive();
        rc |= M8_4_HunterPassive();
        rc |= M8_5_CdrFormula();
        if (rc == 0) Console.WriteLine("  PASS  M8 all assertions");
        return rc;
    }

    // ── M8.1  Hero attack CD = TicksPerSecond / AttackSpeed ──────────────────────
    static int M8_1_AttackSpeed()
    {
        const string name = "M8.1 AttackSpeed→CD";
        int rc = 0;
        // Archetypes: Swordsman (0) AS=0.7, Mage (1) AS=0.6, Marksman (2) AS=0.85.
        for (byte defId = 0; defId < 3; defId++)
        {
            var heroes = new Hero[10];
            heroes[0].HeroDefId = defId;
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[0].Alive = true;
            heroes[0].AttackCdEndFrame = 0;
            // Place enemy Red hero (slot 5) within attack range.
            heroes[5].Alive = true;
            BuiltinContent.ApplyBaseStats(ref heroes[5]);
            heroes[5].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[5].Pos = new TSVector2((Fix64)1, Fix64.Zero); // well within range
            heroes[0].Target = new UnitRef { Kind = UnitKind.Hero, Index = 5, BornFrame = 0 };

            var minions = Array.Empty<Minion>();
            var towers  = Array.Empty<Tower>();
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
            int qc = 0;
            GameSystems.TickHeroes(heroes, minions, towers, frame: 1, q, ref qc);

            // Check attack enqueued.
            if (Check($"{name} defId={defId} attack enqueued", qc >= 1) != 0) { rc = 1; continue; }

            // Expected CD: (uint)(15 / AS).
            uint expectedCd = heroes[0].AttackSpeed > Fix64.Zero
                ? (uint)((Fix64)GameSystems.TicksPerSecond / heroes[0].AttackSpeed)
                : (uint)GameSystems.AttackCdHero;
            uint gotCd = heroes[0].AttackCdEndFrame - 1u; // frame was 1
            rc |= Check($"{name} defId={defId} CdEnd got={heroes[0].AttackCdEndFrame} expected={1u + expectedCd}",
                        heroes[0].AttackCdEndFrame == 1u + expectedCd);
        }
        if (rc == 0) Console.WriteLine("  OK    M8.1 attack CD = 15 / AttackSpeed");
        return rc;
    }

    // ── M8.2  Marksman AS grows +0.03 per level ──────────────────────────────────
    static int M8_2_MarksmanAsGrowth()
    {
        const string name = "M8.2 Marksman AS growth";
        var heroes = new Hero[1];
        heroes[0].HeroDefId = 2; // Marksman
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Level = 1;

        Fix64 asAtLv1 = heroes[0].AttackSpeed; // should be 0.85

        // Award XP to reach level 2 (280 XP required per LevelSystem).
        LevelSystem.AwardXp(heroes, 0, 280u);

        int rc = 0;
        rc |= Check($"{name}: level reached 2", heroes[0].Level == 2);
        Fix64 expectedAs = asAtLv1 + (Fix64)0.03f;
        rc |= Check($"{name}: AS after lv2 = {heroes[0].AttackSpeed} expected ~{expectedAs}",
                    heroes[0].AttackSpeed == expectedAs);

        // Swordsman should have no AS growth.
        var sw = new Hero[1];
        sw[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref sw[0]);
        sw[0].Alive = true;
        sw[0].Level = 1;
        Fix64 swAsLv1 = sw[0].AttackSpeed;
        LevelSystem.AwardXp(sw, 0, 280u);
        rc |= Check($"{name}: Swordsman AS unchanged after lv2", sw[0].AttackSpeed == swAsLv1);

        if (rc == 0) Console.WriteLine("  OK    M8.2 Marksman AS +0.03/lv");
        return rc;
    }

    // ── M8.3  破甲 (ArmorShred) passive: buff applied on basic-attack hit ─────────
    static int M8_3_ArmorShredPassive()
    {
        const string name = "M8.3 ArmorShred passive";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        // Blue Swordsman at slot 0.
        heroes[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
        heroes[0].AttackCdEndFrame = 0;
        // Red target at slot 5 (index ≥ 5 = Red team).
        heroes[5].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[5]);
        heroes[5].Alive = true;
        heroes[5].Pos = new TSVector2((Fix64)1, Fix64.Zero); // within 1.5m melee range
        heroes[0].Target = new UnitRef { Kind = UnitKind.Hero, Index = 5, BornFrame = 0 };

        var buffs = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var minions = Array.Empty<Minion>();
        var towers  = Array.Empty<Tower>();
        Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
        int qc = 0;
        GameSystems.TickHeroes(heroes, minions, towers, frame: 1, q, ref qc, buffs);

        int rc = 0;
        rc |= Check($"{name}: attack enqueued", qc >= 1);

        // Check that ArmorShred buff was applied to hero[5].
        // BuffEngine stores DefId as (buffIndex+1); ArmorShred5 has index=BuffArmorShred5.
        ushort expectedDefId = (ushort)(BuiltinContent.BuffArmorShred5 + 1);
        bool buffFound = false;
        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
        {
            if (buffs[5, s].DefId == expectedDefId) { buffFound = true; break; }
        }
        rc |= Check($"{name}: ArmorShred buff on target (expectedDefId={expectedDefId})", buffFound);

        // Hit 4 more times (total 5) — stack should reach 5 (max).
        for (int hit = 2; hit <= 5; hit++)
        {
            heroes[0].AttackCdEndFrame = 0; // reset CD to allow re-attack
            qc = 0;
            GameSystems.TickHeroes(heroes, minions, towers, (uint)hit, q, ref qc, buffs);
            // Expire-tick buffs so stacks are correct.
            Span<GameSystems.DamageEvent> bdmq = stackalloc GameSystems.DamageEvent[8];
            int bdmqc = 0;
            BuffEngine.Tick(heroes, buffs, (uint)hit, bdmq, ref bdmqc);
        }
        byte stackVal = 0;
        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
            if (buffs[5, s].DefId == expectedDefId) { stackVal = buffs[5, s].Stack; break; }
        rc |= Check($"{name}: 5 hits → stack=5 (got {stackVal})", stackVal == 5);

        if (rc == 0) Console.WriteLine("  OK    M8.3 破甲 ArmorShred passive");
        return rc;
    }

    // ── M8.4  狩猎之眼 (ConditionalDamageMul): ×1.2 damage at <50% HP target ───────
    static int M8_4_HunterPassive()
    {
        const string name = "M8.4 HunterPassive";
        BuiltinContent.Register();
        int rc = 0;

        // Helper: compute damage from Marksman basic attack.
        Fix64 ComputeHit(Fix64 targetHpPct)
        {
            var heroes = new Hero[10];
            heroes[0].HeroDefId = 2; // Marksman, Blue (slot 0)
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[0].Alive = true;
            heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[0].AttackCdEndFrame = 0;
            heroes[0].Target = new UnitRef { Kind = UnitKind.Hero, Index = 5, BornFrame = 0 };

            heroes[5].HeroDefId = 0;
            BuiltinContent.ApplyBaseStats(ref heroes[5]);
            heroes[5].Alive = true;
            heroes[5].Pos  = new TSVector2((Fix64)3, Fix64.Zero); // within 6m
            heroes[5].MaxHp = (Fix64)100;
            heroes[5].Hp   = (Fix64)100 * targetHpPct;
            heroes[5].Armor = Fix64.Zero; // no armor
            heroes[5].MagicResist = Fix64.Zero;

            var buffs = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
            var minions = Array.Empty<Minion>();
            var towers  = Array.Empty<Tower>();
            var q = new GameSystems.DamageEvent[8];
            int qc = 0;
            GameSystems.TickHeroes(heroes, minions, towers, frame: 1, q.AsSpan(), ref qc, buffs);
            return qc > 0 ? q[0].Damage : Fix64.Zero;
        }

        // Base AD damage with no conditional bonus.
        // Marksman: BasicAttackBase=0, AdScale=1000 (1.0×AD). At AD=55.
        // With <50% HP → ×1.2 → 55 × 1.2 = 66.
        Fix64 dmgLow  = ComputeHit((Fix64)0.4f); // 40% HP → conditional triggers
        Fix64 dmgHigh = ComputeHit((Fix64)0.6f); // 60% HP → no bonus

        // dmgHigh should be plain AD of Marksman (55 at level 1).
        // dmgLow should be 55 * 1.2f.
        // Use the engine multiplication to avoid float precision issues.
        Fix64 expectedBase  = (Fix64)55; // Marksman AD at lv1
        Fix64 expectedBoosted = expectedBase * (Fix64)1.2f;

        rc |= Check($"{name}: normal hit dmg={dmgHigh} (expected={expectedBase})",   dmgHigh == expectedBase);
        rc |= Check($"{name}: boosted hit dmg={dmgLow} (expected={expectedBoosted})", dmgLow  == expectedBoosted);

        if (rc == 0) Console.WriteLine("  OK    M8.4 狩猎之眼 ConditionalDamageMul");
        return rc;
    }

    // ── M8.5  CDR reduces skill CD ────────────────────────────────────────────────
    static int M8_5_CdrFormula()
    {
        const string name = "M8.5 CDR formula";
        BuiltinContent.Register();

        var heroes = new Hero[10];
        heroes[0].HeroDefId = 0; // Swordsman
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        heroes[0].Alive = true;
        heroes[0].Cdr = (Fix64)20; // 20% CDR → 80% of base CD
        heroes[0].Mp  = (Fix64)9999;

        var buffs = new BuffInstance[10, BuffEngine.MaxBuffsPerHero];
        var projectiles = new Projectile[SkillSystem.MaxProjectiles];
        Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
        int qc = 0;

        // Swordsman Q (冲锋): CdFrames=90.
        ushort chargeId = BuiltinContent.HeroSkills[0, 0];
        TSVector2 aim = new TSVector2((Fix64)2, Fix64.Zero);
        bool cast = SkillSystem.TryCast(heroes, buffs, 0, 0, chargeId, aim, projectiles, frame: 0, q, ref qc);

        int rc = 0;
        rc |= Check($"{name}: cast succeeded", cast);

        // CDR=20 → cdrPct=80 → cdEnd = 0 + 90*80/100 = 72.
        uint expectedCdEnd = 90u * 80u / 100u; // = 72
        rc |= Check($"{name}: SkillCd0={heroes[0].SkillCd0} expected={expectedCdEnd}",
                    heroes[0].SkillCd0 == expectedCdEnd);

        // Verify max CDR cap: CDR=50 (over cap) → cdrPct clamped to 60 → cdEnd = 90*60/100=54.
        heroes[0].SkillCd0 = 0; // reset
        heroes[0].Cdr = (Fix64)50; // over cap
        qc = 0;
        cast = SkillSystem.TryCast(heroes, buffs, 0, 0, chargeId, aim, projectiles, frame: 0, q, ref qc);
        rc |= Check($"{name}: cast with over-cap CDR succeeded", cast);
        uint cappedCdEnd = 90u * 60u / 100u; // = 54
        rc |= Check($"{name}: CDR capped: SkillCd0={heroes[0].SkillCd0} expected={cappedCdEnd}",
                    heroes[0].SkillCd0 == cappedCdEnd);

        if (rc == 0) Console.WriteLine("  OK    M8.5 CDR formula (20→72f, cap 40→54f)");
        return rc;
    }

    private static int Check(string label, bool ok)
    {
        if (!ok) { Console.WriteLine($"  FAIL  {label}"); return 1; }
        return 0;
    }
}
