// SPDX-License-Identifier: MIT
// PRD §4.3 / §7.1  野怪 jungle-camp system — deterministic, GC-free in Tick.

using System;
using System.Runtime.CompilerServices;

namespace MOBA.Logic.Sim;

/// <summary>
/// Manages 4 jungle camps (2 monsters each = 8 total) placed between the three lanes.
/// Monsters spawn at frame 900 (60 s @15 Hz) and respawn 90 s after death.
/// They attack the nearest hero within <see cref="AggroRange"/> on each Tick.
/// </summary>
public static class JungleSystem
{
    // ---- Configuration -------------------------------------------------------------
    public const int   CampCount       = 4;
    public const int   MonstersPerCamp = 2;
    public const int   MaxMonsters     = CampCount * MonstersPerCamp; // 8
    public const uint  InitialSpawnFrame = 900;   // 60 s @15 Hz
    public const uint  RespawnFrames     = 1350;  // 90 s @15 Hz
    public const int   AttackCdFrames    = 20;    // ~1.33 s

    // Monster base stats
    private static readonly Fix64 MonsterMaxHp    = (Fix64)300;
    private static readonly Fix64 MonsterAd       = (Fix64)20;
    private static readonly Fix64 MonsterArmor    = (Fix64)5;
    private static readonly Fix64 AggroRange      = (Fix64)5;

    // Camp centre positions (between the three lanes, symmetric around map centre).
    // Blue-side jungle: quadrants (-30,-15) and (-15,-30).
    // Red-side  jungle: quadrants ( 30, 15) and ( 15, 30).
    private static readonly TSVector2[] CampCentres = new TSVector2[CampCount]
    {
        new TSVector2((Fix64)(-30), (Fix64)(-15)),
        new TSVector2((Fix64)(-15), (Fix64)(-30)),
        new TSVector2((Fix64)( 30), (Fix64)( 15)),
        new TSVector2((Fix64)( 15), (Fix64)( 30)),
    };

    // ---- Init ----------------------------------------------------------------------
    /// <summary>Populate <paramref name="monsters"/> with initial (dead) entries for all camps.</summary>
    [NoGC]
    public static void Init(JungleMonster[] monsters)
    {
        int idx = 0;
        for (byte c = 0; c < CampCount; c++)
        {
            TSVector2 centre = CampCentres[c];
            for (byte m = 0; m < MonstersPerCamp; m++)
            {
                // Offset the two monsters within a camp ±1.5 on the Y axis.
                Fix64 offsetY = m == 0 ? (Fix64)1.5f : (Fix64)(-1.5f);
                ref var jm = ref monsters[idx];
                jm.Pos         = new TSVector2(centre.X, centre.Y + offsetY);
                jm.MaxHp       = MonsterMaxHp;
                jm.Hp          = MonsterMaxHp;
                jm.Ad          = MonsterAd;
                jm.Armor       = MonsterArmor;
                jm.AggroRange  = AggroRange;
                jm.CampId      = c;
                jm.MonsterId   = m;
                jm.Alive       = false;  // not alive until frame >= InitialSpawnFrame
                jm.BornFrame   = 0;
                jm.AttackCdEndFrame = 0;
                jm.RespawnFrame     = 0;
                idx++;
            }
        }
    }

    // ---- Tick ----------------------------------------------------------------------
    [NoGC]
    public static void Tick(JungleMonster[] monsters, Hero[] heroes, uint frame,
                            Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount)
    {
        for (int i = 0; i < monsters.Length; i++)
        {
            ref var jm = ref monsters[i];

            // ---- Spawn / Respawn ----
            if (!jm.Alive)
            {
                bool firstSpawn  = jm.BornFrame == 0 && frame >= InitialSpawnFrame;
                bool respawnReady = jm.RespawnFrame > 0 && frame >= jm.RespawnFrame;
                if (firstSpawn || respawnReady)
                {
                    jm.Hp           = jm.MaxHp;
                    jm.Alive        = true;
                    jm.BornFrame    = frame;
                    jm.RespawnFrame = 0;
                    jm.AttackCdEndFrame = 0;
                }
                continue;
            }

            // ---- Attack nearest hero in aggro range ----
            if (frame < jm.AttackCdEndFrame) continue;

            int    bestIdx = -1;
            Fix64  bestD2  = jm.AggroRange * jm.AggroRange + Fix64.One; // anything >= range is out

            for (int h = 0; h < heroes.Length; h++)
            {
                ref var hero = ref heroes[h];
                if (!hero.Alive) continue;
                Fix64 dx = hero.Pos.X - jm.Pos.X;
                Fix64 dy = hero.Pos.Y - jm.Pos.Y;
                Fix64 d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; bestIdx = h; }
            }

            if (bestIdx < 0) continue; // no hero in range

            // Enqueue auto-attack damage (physical, source = 0xFF = environment).
            UnitRef target = new UnitRef
            {
                Kind      = UnitKind.Hero,
                Index     = (ushort)bestIdx,
                BornFrame = 0,   // heroes don't use BornFrame for alive-check in IsTargetAlive
            };
            GameSystems.EnqueueDamage(dmgQ, ref dmgCount, target, jm.Ad, frame,
                                      DamageType.Physical, sourceSlot: 0xFF);
            jm.AttackCdEndFrame = frame + AttackCdFrames;
        }
    }
}
