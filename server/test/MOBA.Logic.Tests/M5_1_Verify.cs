// SPDX-License-Identifier: MIT
// M5.1 verification — Tags / Buff / Projectile / SkillEngine core wiring.
//
// Asserts:
//   M5.1.1  Engine smoke: register one damage skill + one projectile skill +
//           one DoT buff; a hero can cast each; CD blocks repeat cast within
//           the cooldown window; mana is consumed.
//   M5.1.2  Buff lifecycle: applied buff sets tag bits; expires on schedule;
//           tag bits are cleared after expiry.
//   M5.1.3  Projectile lifecycle: spawned projectile flies, expires on hit
//           or on lifetime; alive count is bounded.
//   M5.1.4  GC=0: 1000-tick tight loop with the engines wired in must allocate
//           zero bytes (per-thread counter).

using System;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class M5_1_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.1 Verify");

        // Construct world FIRST so BuiltinContent.Register() runs once and
        // populates HeroStats. Then we reset the skill/buff tables and
        // register a tiny set of test-only defs to isolate engine behavior.
        var w = new DeterministicWorld(seed: 1) { EnableGameplay = true };

        // --- Register tiny content ----------------------------------------------------
        SkillEngine.Reset();
        BuffEngine.Reset();

        ushort dotBuff = BuffEngine.Register(new BuffDef
        {
            Id = 1,
            Stack = BuffStackPolicy.Refresh,
            MaxStack = 1,
            DurationFrames = 30,           // 2s @15Hz
            TickIntervalFrames = 15,       // 1s
            Modifier = BuffModifierKind.DamageOverTime,
            ModifierValue = (Fix64)10,
            TagBits = GameplayTags.Slowed,
        });

        Span<EffectStep> dmgSteps = stackalloc EffectStep[1];
        dmgSteps[0] = new EffectStep { Kind = EffectKind.Damage, Param = (Fix64)50, Param2 = 500, Param3 = 0 };
        ushort dmgSkill = SkillEngine.Register(new SkillDef
        {
            Id = 100, OwnerHeroDefId = 0,
            CdFrames = 30, ManaCost = (Fix64)20, CastRange = (Fix64)6,
            Cast = CastType.Position, HitShape = HitShape.Circle, HitParamA = (Fix64)2,
        }, dmgSteps);

        Span<EffectStep> projSteps = stackalloc EffectStep[1];
        projSteps[0] = new EffectStep { Kind = EffectKind.SpawnProjectile, Param2 = 120 /* 12 m/s*10 */, Param3 = 30 /* 3.0s*10 */ };
        ushort projSkill = SkillEngine.Register(new SkillDef
        {
            Id = 101, OwnerHeroDefId = 0,
            CdFrames = 15, ManaCost = (Fix64)15, CastRange = (Fix64)8,
            Cast = CastType.Direction, HitShape = HitShape.None,
        }, projSteps);

        // --- Build a world ------------------------------------------------------------
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];

        // Move heroes apart so the AoE actually hits when we aim at slot 5 (red side).
        for (int f = 0; f < 200; f++) w.Tick(inputs);

        int rc = 0;

        // --- 5.1.1 Damage skill -------------------------------------------------------
        var redHpBefore = w.Heroes[5].Hp;
        var blueMpBefore = w.Heroes[0].Mp;
        var aim = w.Heroes[5].Pos;
        var q = new GameSystems.DamageEvent[64];
        int qc = 0;
        bool cast = SkillSystem.TryCast(w.Heroes, w.Buffs, 0, 0, dmgSkill, aim, w.Projectiles, w.Frame, q, ref qc);
        if (!cast) { Console.WriteLine("  FAIL  damage skill cast refused"); rc = 1; }
        if (qc == 0) { Console.WriteLine("  FAIL  damage skill produced no DamageEvent"); rc = 1; }
        // Apply queue to world.
        GameSystems.ResolveDamage(q, qc, w.Minions, w.Towers, w.Heroes, w.Frame, out _);
        if (w.Heroes[5].Hp >= redHpBefore) { Console.WriteLine("  FAIL  red hero hp not reduced"); rc = 1; }
        if (w.Heroes[0].Mp >= blueMpBefore) { Console.WriteLine("  FAIL  blue hero mp not reduced"); rc = 1; }

        // CD must block immediate recast.
        qc = 0;
        bool cast2 = SkillSystem.TryCast(w.Heroes, w.Buffs, 0, 0, dmgSkill, aim, w.Projectiles, w.Frame, q, ref qc);
        if (cast2) { Console.WriteLine("  FAIL  CD did not block recast"); rc = 1; }

        // --- 5.1.2 Buff lifecycle -----------------------------------------------------
        bool applied = BuffEngine.Apply(w.Heroes, w.Buffs, 5, dotBuff, sourceSlot: 0, frame: w.Frame);
        if (!applied) { Console.WriteLine("  FAIL  buff apply"); rc = 1; }
        if ((w.Heroes[5].Tags & GameplayTags.Slowed) == 0) { Console.WriteLine("  FAIL  tag not applied"); rc = 1; }
        var hpBeforeDot = w.Heroes[5].Hp;
        for (int f = 0; f < 60; f++) w.Tick(inputs);
        if (w.Heroes[5].Hp >= hpBeforeDot) { Console.WriteLine("  FAIL  DoT did not damage"); rc = 1; }
        if ((w.Heroes[5].Tags & GameplayTags.Slowed) != 0) { Console.WriteLine("  FAIL  tag not cleared after expiry"); rc = 1; }

        // --- 5.1.3 Projectile ---------------------------------------------------------
        var aimDir = w.Heroes[5].Pos;
        qc = 0;
        SkillSystem.TryCast(w.Heroes, w.Buffs, 0, 1, projSkill, aimDir, w.Projectiles, w.Frame, q, ref qc);
        int aliveProj = 0;
        for (int i = 0; i < w.Projectiles.Length; i++) if (w.Projectiles[i].Alive) aliveProj++;
        if (aliveProj == 0) { Console.WriteLine("  FAIL  projectile not spawned"); rc = 1; }
        // Tick until projectile dies.
        for (int f = 0; f < 60 && aliveProj > 0; f++)
        {
            w.Tick(inputs);
            aliveProj = 0;
            for (int i = 0; i < w.Projectiles.Length; i++) if (w.Projectiles[i].Alive) aliveProj++;
        }
        if (aliveProj > 0) { Console.WriteLine("  FAIL  projectile leaked"); rc = 1; }

        // --- 5.1.4 GC=0 tight loop ----------------------------------------------------
        // Re-cast continuously to exercise the skill path inside the loop.
        // Warm up LevelSystem JIT paths (TryLevelUp / ApplyLevelGrowth) before measuring.
        LevelSystem.WarmUp();
        for (int f = 0; f < 60; f++) w.Tick(inputs); // settle CDs
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        const int tight = 1000;
        for (int f = 0; f < tight; f++)
        {
            if ((f % 5) == 0)
            {
                int qc2 = 0;
                SkillSystem.TryCast(w.Heroes, w.Buffs, 0, 1, projSkill, w.Heroes[5].Pos, w.Projectiles, w.Frame, q, ref qc2);
            }
            w.Tick(inputs);
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - a0;
        Console.WriteLine($"  Tight loop : alloc Δ={delta} bytes over {tight} ticks");
        if (delta != 0) { Console.WriteLine($"  FAIL  M5.1.4 alloc Δ {delta} bytes (expected 0)"); rc = 1; }

        if (rc == 0) Console.WriteLine("  PASS  M5.1 engine core OK");
        return rc;
    }
}
