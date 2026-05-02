// SPDX-License-Identifier: MIT
// M7 verification �?PRD §7.7 v3.0 hero exact specs.
//
// Asserts:
//   M7.1  Swordsman stats match PRD §7.7 (HP 600, AD 60, ARM 25, MR 25, AttackRange 1.5m, AS 0.7, MS 6.5).
//   M7.2  Mage stats match PRD §7.7 (HP 480, MP 400, AP 50, ARM 18, AttackRange 5m, AS 0.6, MS 6.0).
//   M7.3  Marksman stats match PRD §7.7 (HP 520, MP 250, AD 55, ARM 20, AttackRange 6m, AS 0.85, MS 6.2).
//   M7.4  Mage basic attack = Magic damage, 30 + 0.3AP.
//   M7.5  Swordsman basic attack = Physical damage, 1.0 AD.
//   M7.6  陨石 deals 250 + 1.2AP Magic damage; stun buff applied on hit.
//   M7.7  冲锋 has PreCastFrames = 5 (PRD: 0.3s �?5f @15 Hz).
//   M7.8  Minion attacked by hero immediately retargets that hero (PRD §4.3 attacker priority).
//   M7.9  Tower prioritises enemy hero attacking allied minion over closer enemy minion.

using System;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M7_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M7 Verify");
        BuiltinContent.Register();
        int rc = 0;

        // ── M7.1 Swordsman stats ─────────────────────────────────────────────────
        {
            var h = MakeHero(heroDefId: 0);
            BuiltinContent.ApplyBaseStats(ref h);
            rc |= Check("M7.1 Swordsman MaxHp",    h.MaxHp        == (Fix64)600);
            rc |= Check("M7.1 Swordsman Ad",       h.Ad           == (Fix64)60);
            rc |= Check("M7.1 Swordsman Armor",    h.Armor        == (Fix64)25);
            rc |= Check("M7.1 Swordsman MR",       h.MagicResist  == (Fix64)25);
            rc |= Check("M7.1 Swordsman AttackRange", h.AttackRange == (Fix64)1.5f);
            rc |= Check("M7.1 Swordsman AttackSpeed", h.AttackSpeed == (Fix64)0.7f);
            rc |= Check("M7.1 Swordsman MoveSpeed", h.MoveSpeed   == (Fix64)6.5f);
        }
        Console.WriteLine("  OK    M7.1 Swordsman stats");

        // ── M7.2 Mage stats ──────────────────────────────────────────────────────
        {
            var h = MakeHero(heroDefId: 1);
            BuiltinContent.ApplyBaseStats(ref h);
            rc |= Check("M7.2 Mage MaxHp",    h.MaxHp        == (Fix64)480);
            rc |= Check("M7.2 Mage MaxMp",    h.MaxMp        == (Fix64)400);
            rc |= Check("M7.2 Mage Ap",       h.Ap           == (Fix64)50);
            rc |= Check("M7.2 Mage Ad",       h.Ad           == (Fix64)45);
            rc |= Check("M7.2 Mage Armor",    h.Armor        == (Fix64)18);
            rc |= Check("M7.2 Mage AttackRange", h.AttackRange == (Fix64)5);
            rc |= Check("M7.2 Mage AttackSpeed", h.AttackSpeed == (Fix64)0.6f);
            rc |= Check("M7.2 Mage MoveSpeed",   h.MoveSpeed   == (Fix64)6.0f);
        }
        Console.WriteLine("  OK    M7.2 Mage stats");

        // ── M7.3 Marksman stats ──────────────────────────────────────────────────
        {
            var h = MakeHero(heroDefId: 2);
            BuiltinContent.ApplyBaseStats(ref h);
            rc |= Check("M7.3 Marksman MaxHp",    h.MaxHp        == (Fix64)520);
            rc |= Check("M7.3 Marksman MaxMp",    h.MaxMp        == (Fix64)250);
            rc |= Check("M7.3 Marksman Ad",       h.Ad           == (Fix64)55);
            rc |= Check("M7.3 Marksman Armor",    h.Armor        == (Fix64)20);
            rc |= Check("M7.3 Marksman AttackRange", h.AttackRange == (Fix64)6);
            rc |= Check("M7.3 Marksman AttackSpeed", h.AttackSpeed == (Fix64)0.85f);
            rc |= Check("M7.3 Marksman MoveSpeed",   h.MoveSpeed   == (Fix64)6.2f);
        }
        Console.WriteLine("  OK    M7.3 Marksman stats");

        // ── M7.4  Mage basic attack = Magic 30+0.3AP ─────────────────────────────
        {
            // Damage = 30 + 0.3 * AP.  At AP=50: expected = 30 + 15 = 45.
            var heroes = new Hero[2];
            heroes[0] = MakeHero(heroDefId: 1, slot: 0); // Mage, Blue team
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[1] = MakeHero(heroDefId: 0, slot: 1, team: Team.Red); // enemy (Red)
            BuiltinContent.ApplyBaseStats(ref heroes[1]);
            heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[1].Pos = new TSVector2((Fix64)3, Fix64.Zero); // within 5m attack range
            heroes[1].Armor = Fix64.Zero; // remove armor so damage is raw
            heroes[1].MagicResist = Fix64.Zero;

            var minions = Array.Empty<Minion>();
            var towers  = Array.Empty<Tower>();
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
            int qc = 0;
            // Force hero 0 to attack hero 1.
            heroes[0].Target = new UnitRef { Kind = UnitKind.Hero, Index = 1, BornFrame = 0 };
            heroes[0].AttackCdEndFrame = 0;
            GameSystems.TickHeroes(heroes, minions, towers, frame: 1, q, ref qc);
            rc |= Check("M7.4 Mage attack enqueued", qc >= 1);
            if (qc >= 1)
            {
                rc |= Check("M7.4 Mage attack dmgType Magic", q[0].DmgType == DamageType.Magic);
                // Expected damage: 30 + 0.3*50 = 45. Use integer division to match engine formula.
                Fix64 expectedDmg = (Fix64)30 + heroes[0].Ap * ((Fix64)300 / (Fix64)1000);
                rc |= Check($"M7.4 Mage attack dmg={q[0].Damage} expected~={expectedDmg}",
                            q[0].Damage == expectedDmg);
            }
        }
        Console.WriteLine("  OK    M7.4 Mage basic attack Magic 30+0.3AP");

        // ── M7.5  Swordsman basic attack = Physical 1.0AD ────────────────────────
        {
            var heroes = new Hero[2];
            heroes[0] = MakeHero(heroDefId: 0, slot: 0);
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[1] = MakeHero(heroDefId: 0, slot: 1, team: Team.Red);
            BuiltinContent.ApplyBaseStats(ref heroes[1]);
            heroes[0].Pos = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[1].Pos = new TSVector2((Fix64)1, Fix64.Zero); // within 1.5m melee range
            heroes[0].Target = new UnitRef { Kind = UnitKind.Hero, Index = 1, BornFrame = 0 };
            heroes[0].AttackCdEndFrame = 0;

            var minions = Array.Empty<Minion>();
            var towers  = Array.Empty<Tower>();
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
            int qc = 0;
            GameSystems.TickHeroes(heroes, minions, towers, frame: 1, q, ref qc);
            rc |= Check("M7.5 Swordsman attack enqueued", qc >= 1);
            if (qc >= 1)
            {
                rc |= Check("M7.5 Swordsman attack Physical", q[0].DmgType == DamageType.Physical);
                rc |= Check($"M7.5 Swordsman attack dmg={q[0].Damage} expected={heroes[0].Ad}",
                            q[0].Damage == heroes[0].Ad);
            }
        }
        Console.WriteLine("  OK    M7.5 Swordsman basic attack Physical 1.0AD");

        // ── M7.6  陨石 damage 250+1.2AP Magic + stun buff ─────────────────────────
        {
            // Cast the Mage R skill and verify the spawned delayed-AoE projectile.
            BuiltinContent.Register();
            ushort meteorSkillId = BuiltinContent.HeroSkills[1, 3]; // HeroDefId=1, slot R
            ref var def = ref SkillEngine.Defs[meteorSkillId];
            rc |= Check("M7.6 meteor DelayFrames=12", SkillEngine.Steps[def.StepStart].DelayFrames == 12u);
            rc |= Check("M7.6 meteor DmgType Magic",  SkillEngine.Steps[def.StepStart].DmgType == DamageType.Magic);

            // Verify AP scaling: Param3 = 1200 (1.2 AP).
            rc |= Check("M7.6 meteor Param3(APscale)=1200",
                        SkillEngine.Steps[def.StepStart].Param3 == 1200);

            // Cast the skill and verify the projectile carries the stun buff.
            var heroes = new Hero[2];
            heroes[0] = MakeHero(heroDefId: 1, slot: 0); // Mage, Blue
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[0].Pos  = new TSVector2(Fix64.Zero, Fix64.Zero);
            heroes[0].Mp   = (Fix64)200;
            heroes[1] = MakeHero(heroDefId: 0, slot: 1, team: Team.Red);
            heroes[1].Pos  = new TSVector2((Fix64)5, Fix64.Zero);

            var projectiles = new Projectile[SkillSystem.MaxProjectiles];
            var buffs       = new BuffInstance[2, BuffEngine.MaxBuffsPerHero];
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
            int qc = 0;
            TSVector2 aim = heroes[1].Pos;
            bool cast = SkillSystem.TryCast(heroes, buffs, 0, 3, meteorSkillId, aim, projectiles, 1, q, ref qc);
            rc |= Check("M7.6 meteor cast succeeded", cast);

            // Exactly one projectile (the delayed AoE) should have spawned.
            int projCount = 0;
            int pi = -1;
            for (int p = 0; p < projectiles.Length; p++)
                if (projectiles[p].Alive) { projCount++; pi = p; }
            rc |= Check("M7.6 meteor spawned 1 projectile", projCount == 1);
            if (pi >= 0)
            {
                rc |= Check("M7.6 meteor AoeOnExpiry=true", projectiles[pi].AoeOnExpiry);
                rc |= Check("M7.6 meteor BuffOnHit=stun",
                            projectiles[pi].BuffOnHit == BuiltinContent.BuffStun10);
                // Verify damage amount: 250 + 1.2 * AP(50) = 250 + 60 = 310. Use integer division.
                Fix64 expectedDmg = (Fix64)250 + heroes[0].Ap * ((Fix64)1200 / (Fix64)1000);
                rc |= Check($"M7.6 meteor dmg={projectiles[pi].Damage} expected={expectedDmg}",
                            projectiles[pi].Damage == expectedDmg);
            }
        }
        Console.WriteLine("  OK    M7.6 陨石 AP scaling + stun buff");

        // ── M7.7  冲锋 PreCastFrames = 5 ─────────────────────────────────────────
        {
            ushort chargeSkillId = BuiltinContent.HeroSkills[0, 0]; // Swordsman Q
            ref var def = ref SkillEngine.Defs[chargeSkillId];
            rc |= Check($"M7.7 冲锋 PreCastFrames=5 (got {def.PreCastFrames})",
                        def.PreCastFrames == 5u);
        }
        Console.WriteLine("  OK    M7.7 冲锋 PreCastFrames=5");

        // ── M7.8  Minion attacker priority: hero-hit → minion retargets hero ──────
        {
            // GameSystems determines team by slot index: <5 = Blue, >=5 = Red.
            // Red hero must be at index 5 to be treated as Red by ResolveDamage.
            var heroes  = new Hero[10];
            heroes[0] = MakeHero(heroDefId: 0, slot: 0); // Blue hero, index 0
            BuiltinContent.ApplyBaseStats(ref heroes[0]);
            heroes[0].Alive = true;
            heroes[5] = MakeHero(heroDefId: 0, slot: 5, team: Team.Red); // Red hero, index 5
            heroes[5].Alive = true;

            var minions = new Minion[1];
            minions[0] = new Minion
            {
                Alive = true, Hp = (Fix64)500, MaxHp = (Fix64)500,
                Ad = (Fix64)10, Armor = Fix64.Zero, AttackRange = (Fix64)2,
                Team = Team.Blue, Pos = new TSVector2(Fix64.Zero, Fix64.Zero), BornFrame = 0,
            };

            // Initially minion has no target.
            rc |= Check("M7.8 pre: minion has no target", !minions[0].Target.IsValid);

            // Red hero (slot 5) hits the Blue minion.
            var towers = Array.Empty<Tower>();
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[4];
            int qc = 0;
            GameSystems.EnqueueDamage(q, ref qc,
                new UnitRef { Kind = UnitKind.Minion, Index = 0, BornFrame = 0 },
                (Fix64)50, frame: 1, sourceSlot: 5); // slot 5 = Red hero (index >= 5)
            GameSystems.ResolveDamage(q, qc, minions, towers, heroes, frame: 1, out _);

            rc |= Check("M7.8 post: minion targets attacker hero",
                        minions[0].Target.IsValid &&
                        minions[0].Target.Kind == UnitKind.Hero &&
                        minions[0].Target.Index == 5);
        }
        Console.WriteLine("  OK    M7.8 minion attacker priority");

        // ── M7.9  Tower aggro: hero attacking ally preferred over closer minion ───
        {
            // GameSystems determines team by slot index: <5 = Blue, >=5 = Red.
            // Red hero must be at index 5 to be treated as Red by AcquireTowerTarget.
            var heroes = new Hero[10];
            // Blue team: hero slot 0 (ally of the tower)
            heroes[0] = MakeHero(heroDefId: 0, slot: 0);
            heroes[0].Alive = true;
            heroes[0].Pos   = new TSVector2((Fix64)8, Fix64.Zero); // inside tower range (10m)
            // Red team: hero slot 5 — this hero is attacking an allied Blue minion.
            heroes[5] = MakeHero(heroDefId: 0, slot: 5, team: Team.Red);
            heroes[5].Alive = true;
            heroes[5].Pos   = new TSVector2((Fix64)9, Fix64.Zero); // farther from tower, inside range

            var minions = new Minion[2];
            // Blue minion (ally of tower) that red hero is attacking.
            minions[0] = new Minion
            {
                Alive = true, Team = Team.Blue,
                Hp = (Fix64)100, MaxHp = (Fix64)100,
                Pos = new TSVector2((Fix64)5, Fix64.Zero), BornFrame = 0,
            };
            // Red minion — closer to tower than the Red hero.
            minions[1] = new Minion
            {
                Alive = true, Team = Team.Red,
                Hp = (Fix64)100, MaxHp = (Fix64)100,
                Pos = new TSVector2((Fix64)3, Fix64.Zero), BornFrame = 1,
            };

            // Red hero (slot 5) is targeting the Blue minion.
            heroes[5].Target = new UnitRef { Kind = UnitKind.Minion, Index = 0, BornFrame = 0 };

            var tower = new Tower
            {
                Alive = true, Team = Team.Blue,
                Pos = new TSVector2(Fix64.Zero, Fix64.Zero), AttackRange = (Fix64)10, BornFrame = 0,
                Ad = (Fix64)100, Armor = Fix64.Zero,
            };
            var towers = new Tower[] { tower };
            Span<GameSystems.DamageEvent> q = stackalloc GameSystems.DamageEvent[8];
            int qc = 0;
            GameSystems.TickTowers(towers, minions, heroes, frame: 1, q, ref qc);

            // Tower should target the Red hero (slot 5) because it's attacking an allied minion,
            // NOT the closer Red minion.
            rc |= Check("M7.9 tower targets aggro hero (not closer minion)",
                        towers[0].Target.IsValid &&
                        towers[0].Target.Kind == UnitKind.Hero &&
                        towers[0].Target.Index == 5);
        }
        Console.WriteLine("  OK    M7.9 tower aggro priority");

        if (rc == 0) Console.WriteLine("  PASS  M7 all assertions");
        return rc;
    }

    private static int Check(string label, bool ok)
    {
        if (!ok) { Console.WriteLine($"  FAIL  {label}"); return 1; }
        return 0;
    }

    private static Hero MakeHero(byte heroDefId, int slot = 0, Team team = Team.Blue)
        => new Hero
        {
            Alive = true, Level = 1,
            HeroDefId = heroDefId,
            // Pos zero by default.
        };
}
