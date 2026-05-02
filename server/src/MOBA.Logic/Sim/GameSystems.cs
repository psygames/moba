// SPDX-License-Identifier: MIT
// M4 game systems — deterministic, GC-free in Tick.
// All methods operate on arrays owned by DeterministicWorld; no instance allocations.

using System;

namespace MOBA.Logic.Sim;

public static class GameSystems
{
    // ---- Constants -----------------------------------------------------------------
    public const int MaxMinions = 256;
    public const int TowerCount = Lanes.TowersPerSide * 2;     // 12
    public const int WaveIntervalFrames = 450;                  // 30s @15Hz
    public const int MinionsPerLanePerSide = 6;                 // 3 melee + 3 caster
    public const int RespawnFrames = 300;                       // 20s @15Hz
    public const int TicksPerSecond = 15;                       // 15 Hz
    public const int AttackCdMelee = 15;                         // 1 attack/sec
    public const int AttackCdCaster = 18;
    public const int AttackCdHero = 12;                          // fallback only
    public const int AttackCdTower = 15;

    // ---- Spawning ------------------------------------------------------------------
    [NoGC]
    public static int SpawnWave(Minion[] minions, int aliveCount, uint frame, int waveIdx)
    {
        // Stronger waves over time: hp +5%/wave.
        Fix64 hpMul = Fix64.One + (Fix64)waveIdx * (Fix64)0.05f;
        for (byte lane = 0; lane < Lanes.LaneCount; lane++)
        {
            for (int side = 0; side < 2; side++)
            {
                Team team = (Team)side;
                byte wpStart = team == Team.Blue ? (byte)0 : (byte)(Lanes.WaypointsPerLane - 1);
                TSVector2 spawn = Lanes.Waypoints[lane, wpStart];
                for (int i = 0; i < MinionsPerLanePerSide; i++)
                {
                    if (aliveCount >= MaxMinions) return aliveCount;
                    int slot = FindFreeSlot(minions);
                    if (slot < 0) return aliveCount;
                    bool melee = i < 3;
                    ref var m = ref minions[slot];
                    m.Pos = spawn + new TSVector2(((Fix64)i - (Fix64)2.5f) * (Fix64)0.6f, Fix64.Zero);
                    m.Type = melee ? MinionType.Melee : MinionType.Caster;
                    m.Team = team;
                    m.Lane = lane;
                    m.WaypointIdx = team == Team.Blue ? (byte)1 : (byte)(Lanes.WaypointsPerLane - 2);
                    m.MaxHp = (melee ? (Fix64)450 : (Fix64)300) * hpMul;
                    m.Hp = m.MaxHp;
                    m.Ad = melee ? (Fix64)18 : (Fix64)24;
                    m.Armor = (Fix64)2;
                    m.AttackRange = melee ? (Fix64)1.5f : (Fix64)4;
                    m.MoveSpeed = (Fix64)3;
                    m.Alive = true;
                    m.BornFrame = frame;
                    m.AttackCdEndFrame = 0;
                    m.Target = UnitRef.None;
                    aliveCount++;
                }
                // PRD §4.3: after 30 waves, add 1 siege minion per lane per side.
                if (waveIdx >= 30)
                {
                    int ss = FindFreeSlot(minions);
                    if (ss >= 0 && aliveCount < MaxMinions)
                    {
                        ref var sm = ref minions[ss];
                        sm.Pos = spawn + new TSVector2((Fix64)0.5f, Fix64.Zero);
                        sm.Type = MinionType.Siege;
                        sm.Team = team;
                        sm.Lane = lane;
                        sm.WaypointIdx = team == Team.Blue ? (byte)1 : (byte)(Lanes.WaypointsPerLane - 2);
                        sm.MaxHp = (Fix64)750 * hpMul;
                        sm.Hp = sm.MaxHp;
                        sm.Ad = (Fix64)35;
                        sm.Armor = (Fix64)10;
                        sm.AttackRange = (Fix64)2;
                        sm.MoveSpeed = (Fix64)2.5f;
                        sm.Alive = true;
                        sm.BornFrame = frame;
                        sm.AttackCdEndFrame = 0;
                        sm.Target = UnitRef.None;
                        aliveCount++;
                    }
                }
            }
        }
        return aliveCount;
    }

    [NoGC]
    private static int FindFreeSlot(Minion[] minions)
    {
        for (int i = 0; i < minions.Length; i++) if (!minions[i].Alive) return i;
        return -1;
    }

    // ---- Minion AI -----------------------------------------------------------------
    [NoGC]
    public static void TickMinions(Minion[] minions, Tower[] towers, Hero[] heroes,
                                   Fix64 dt, uint frame,
                                   Span<DamageEvent> damageQueue, ref int damageCount)
    {
        for (int i = 0; i < minions.Length; i++)
        {
            ref var m = ref minions[i];
            if (!m.Alive) continue;
            // Validate / refresh target.
            if (!IsTargetAlive(m.Target, minions, towers, heroes)) m.Target = UnitRef.None;
            if (!m.Target.IsValid) AcquireMinionTarget(ref m, i, minions, towers, heroes);

            if (m.Target.IsValid)
            {
                TSVector2 tp = TargetPos(m.Target, minions, towers, heroes);
                Fix64 dx = tp.X - m.Pos.X, dy = tp.Y - m.Pos.Y;
                Fix64 d2 = dx * dx + dy * dy;
                Fix64 r2 = m.AttackRange * m.AttackRange;
                if (d2 <= r2)
                {
                    if (frame >= m.AttackCdEndFrame)
                    {
                        EnqueueDamage(damageQueue, ref damageCount, m.Target, m.Ad, frame);
                        m.AttackCdEndFrame = frame + (m.Type == MinionType.Melee ? (uint)AttackCdMelee : (uint)AttackCdCaster);
                    }
                    continue;
                }
                StepToward(ref m.Pos, tp, m.MoveSpeed, dt);
                continue;
            }
            // No target — march toward next waypoint.
            byte targetWp = m.WaypointIdx;
            TSVector2 wp = Lanes.Waypoints[m.Lane, targetWp];
            Fix64 wdx = wp.X - m.Pos.X, wdy = wp.Y - m.Pos.Y;
            Fix64 wd2 = wdx * wdx + wdy * wdy;
            if (wd2 < (Fix64)1) // 1m² => ~1m radius
            {
                if (m.Team == Team.Blue && targetWp + 1 < Lanes.WaypointsPerLane) m.WaypointIdx = (byte)(targetWp + 1);
                else if (m.Team == Team.Red && targetWp > 0)                       m.WaypointIdx = (byte)(targetWp - 1);
            }
            StepToward(ref m.Pos, wp, m.MoveSpeed, dt);
        }
    }

    [NoGC]
    private static void AcquireMinionTarget(ref Minion m, int selfIdx, Minion[] minions, Tower[] towers, Hero[] heroes)
    {
        // PRD §4.3 priority: nearest enemy minion > nearest enemy hero > nearest enemy tower.
        // ("attacker first" is handled in ResolveDamage by directly setting m.Target when hit.)
        Fix64 r2 = m.AttackRange * m.AttackRange;
        Fix64 best = r2;
        UnitRef pick = UnitRef.None;
        // Priority 1: nearest enemy minion.
        for (int i = 0; i < minions.Length; i++)
        {
            if (i == selfIdx) continue;
            ref var o = ref minions[i];
            if (!o.Alive || o.Team == m.Team) continue;
            Fix64 dx = o.Pos.X - m.Pos.X, dy = o.Pos.Y - m.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Minion, Index = (ushort)i, BornFrame = o.BornFrame }; }
        }
        if (pick.IsValid) { m.Target = pick; return; }
        // Priority 2: nearest enemy hero.
        for (int i = 0; i < heroes.Length; i++)
        {
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam == m.Team) continue;
            Fix64 dx = h.Pos.X - m.Pos.X, dy = h.Pos.Y - m.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)i, BornFrame = 0 }; }
        }
        if (pick.IsValid) { m.Target = pick; return; }
        // Priority 3: nearest enemy tower.
        for (int i = 0; i < towers.Length; i++)
        {
            ref var t = ref towers[i];
            if (!t.Alive || t.Team == m.Team) continue;
            Fix64 dx = t.Pos.X - m.Pos.X, dy = t.Pos.Y - m.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Tower, Index = (ushort)i, BornFrame = t.BornFrame }; }
        }
        m.Target = pick;
    }

    // ---- Tower AI ------------------------------------------------------------------
    [NoGC]
    public static void TickTowers(Tower[] towers, Minion[] minions, Hero[] heroes,
                                  uint frame,
                                  Span<DamageEvent> damageQueue, ref int damageCount)
    {
        for (int i = 0; i < towers.Length; i++)
        {
            ref var t = ref towers[i];
            if (!t.Alive) continue;
            if (!IsTargetAlive(t.Target, minions, towers, heroes)) t.Target = UnitRef.None;
            if (!t.Target.IsValid) AcquireTowerTarget(ref t, minions, heroes);
            if (!t.Target.IsValid) continue;
            TSVector2 tp = TargetPos(t.Target, minions, towers, heroes);
            Fix64 dx = tp.X - t.Pos.X, dy = tp.Y - t.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            Fix64 r2 = t.AttackRange * t.AttackRange;
            if (d2 > r2) { t.Target = UnitRef.None; continue; }
            if (frame >= t.AttackCdEndFrame)
            {
                EnqueueDamage(damageQueue, ref damageCount, t.Target, t.Ad, frame);
                t.AttackCdEndFrame = frame + (uint)AttackCdTower;
            }
        }
    }

    [NoGC]
    private static void AcquireTowerTarget(ref Tower t, Minion[] minions, Hero[] heroes)
    {
        // PRD §4.3 priority: enemy hero attacking allied unit > nearest enemy minion > nearest enemy hero.
        Fix64 r2 = t.AttackRange * t.AttackRange;
        Fix64 best = r2;
        UnitRef pick = UnitRef.None;
        // Priority 1: enemy hero (in range) currently targeting an allied unit.
        for (int i = 0; i < heroes.Length; i++)
        {
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam == t.Team) continue;
            Fix64 dx = h.Pos.X - t.Pos.X, dy = h.Pos.Y - t.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 >= r2) continue;
            if (IsAttackingAlly(h.Target, t.Team, minions, heroes))
            {
                if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)i, BornFrame = 0 }; }
            }
        }
        if (pick.IsValid) { t.Target = pick; return; }
        // Priority 2: nearest enemy minion.
        for (int i = 0; i < minions.Length; i++)
        {
            ref var m = ref minions[i];
            if (!m.Alive || m.Team == t.Team) continue;
            Fix64 dx = m.Pos.X - t.Pos.X, dy = m.Pos.Y - t.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Minion, Index = (ushort)i, BornFrame = m.BornFrame }; }
        }
        if (pick.IsValid) { t.Target = pick; return; }
        // Priority 3: nearest enemy hero.
        for (int i = 0; i < heroes.Length; i++)
        {
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam == t.Team) continue;
            Fix64 dx = h.Pos.X - t.Pos.X, dy = h.Pos.Y - t.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)i, BornFrame = 0 }; }
        }
        t.Target = pick;
    }

    /// <summary>Returns true if <paramref name="target"/> belongs to <paramref name="allyTeam"/>.</summary>
    [NoGC]
    private static bool IsAttackingAlly(UnitRef target, Team allyTeam, Minion[] minions, Hero[] heroes)
    {
        if (!target.IsValid) return false;
        switch (target.Kind)
        {
            case UnitKind.Minion:
                if (target.Index < minions.Length)
                    return minions[target.Index].Team == allyTeam && minions[target.Index].Alive;
                break;
            case UnitKind.Hero:
                if (target.Index < heroes.Length)
                {
                    Team tT = target.Index < 5 ? Team.Blue : Team.Red;
                    return tT == allyTeam && heroes[target.Index].Alive;
                }
                break;
        }
        return false;
    }

    // ---- Hero auto-attack (M4-lite: heroes auto-fire at first enemy in range) ------
    [NoGC]
    public static void TickHeroes(Hero[] heroes, Minion[] minions, Tower[] towers,
                                  uint frame,
                                  Span<DamageEvent> damageQueue, ref int damageCount,
                                  BuffInstance[,]? buffs = null,
                                  JungleMonster[]? jungle = null)
    {
        for (int i = 0; i < heroes.Length; i++)
        {
            ref var h = ref heroes[i];
            if (!h.Alive)
            {
                if (frame >= h.RespawnFrame)
                {
                    Respawn.RespawnAtFountain(ref h, i);
                }
                continue;
            }
            if (!IsTargetAlive(h.Target, minions, towers, heroes, jungle)) h.Target = UnitRef.None;
            if (!h.Target.IsValid) AcquireHeroTarget(ref h, i, minions, towers, jungle);
            if (!h.Target.IsValid) continue;
            TSVector2 tp = TargetPos(h.Target, minions, towers, heroes, jungle);
            Fix64 dx = tp.X - h.Pos.X, dy = tp.Y - h.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            Fix64 r2 = h.AttackRange * h.AttackRange;
            if (d2 > r2) continue;
            if (frame >= h.AttackCdEndFrame)
            {
                // Per-hero basic attack formula (PRD §7.7): damage = Base + AD*adScale + AP*apScale.
                ref var hDef = ref BuiltinContent.HeroStats[h.HeroDefId % BuiltinContent.HeroCount];
                Fix64 rawDmg = (Fix64)hDef.BasicAttackBase
                             + h.Ad * ((Fix64)hDef.BasicAttackAdScale1k / (Fix64)1000)
                             + h.Ap * ((Fix64)hDef.BasicAttackApScale1k / (Fix64)1000);
                if (buffs != null)
                    rawDmg = SkillSystem.ApplyPassivesOnHit(heroes, buffs, i, h.Target, rawDmg, frame);
                EnqueueDamage(damageQueue, ref damageCount, h.Target, rawDmg, frame, hDef.BasicAttackDmgType, (byte)i);
                // PRD §7.7: cooldown = 1 / AttackSpeed seconds = TicksPerSecond / AttackSpeed frames.
                uint atkCd = h.AttackSpeed > Fix64.Zero
                    ? (uint)((Fix64)TicksPerSecond / h.AttackSpeed)
                    : (uint)AttackCdHero;
                h.AttackCdEndFrame = frame + atkCd;
            }
        }
    }

    [NoGC]
    private static void AcquireHeroTarget(ref Hero h, int selfIdx, Minion[] minions, Tower[] towers,
                                          JungleMonster[]? jungle = null)
    {
        Team hTeam = selfIdx < 5 ? Team.Blue : Team.Red;
        Fix64 r2 = h.AttackRange * h.AttackRange;
        Fix64 best = r2;
        UnitRef pick = UnitRef.None;
        for (int i = 0; i < minions.Length; i++)
        {
            ref var m = ref minions[i];
            if (!m.Alive || m.Team == hTeam) continue;
            Fix64 dx = m.Pos.X - h.Pos.X, dy = m.Pos.Y - h.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Minion, Index = (ushort)i, BornFrame = m.BornFrame }; }
        }
        if (pick.IsValid) { h.Target = pick; return; }
        for (int i = 0; i < towers.Length; i++)
        {
            ref var t = ref towers[i];
            if (!t.Alive || t.Team == hTeam) continue;
            Fix64 dx = t.Pos.X - h.Pos.X, dy = t.Pos.Y - h.Pos.Y;
            Fix64 d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.Tower, Index = (ushort)i, BornFrame = t.BornFrame }; }
        }
        h.Target = pick;
        // Jungle monsters (neutral; any hero may attack them).
        if (jungle != null)
        {
            for (int i = 0; i < jungle.Length; i++)
            {
                ref var jm = ref jungle[i];
                if (!jm.Alive) continue;
                Fix64 dx = jm.Pos.X - h.Pos.X, dy = jm.Pos.Y - h.Pos.Y;
                Fix64 d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; pick = new UnitRef { Kind = UnitKind.JungleMonster, Index = (ushort)i, BornFrame = jm.BornFrame }; }
            }
        }
        h.Target = pick;
    }

    // ---- Combat resolve ------------------------------------------------------------
    public struct DamageEvent { public UnitRef Target; public Fix64 Damage; public uint Frame; public DamageType DmgType; public byte SourceSlot; }

    [NoGC]
    public static void EnqueueDamage(Span<DamageEvent> q, ref int count, UnitRef tgt, Fix64 amount, uint frame,
                                     DamageType dmgType = DamageType.Physical, byte sourceSlot = 0xFF)
    {
        if (count >= q.Length) return; // overflow: drop (deterministic)
        q[count++] = new DamageEvent { Target = tgt, Damage = amount, Frame = frame, DmgType = dmgType, SourceSlot = sourceSlot };
    }

    [NoGC]
    public static int ResolveDamage(Span<DamageEvent> q, int count, Minion[] minions, Tower[] towers, Hero[] heroes,
                                    uint frame, out int aliveMinionCount,
                                    BuffInstance[,]? buffs = null,
                                    JungleMonster[]? jungle = null)
    {
        aliveMinionCount = 0;
        for (int i = 0; i < count; i++)
        {
            ref var ev = ref q[i];
            switch (ev.Target.Kind)
            {
                case UnitKind.Minion:
                {
                    ref var m = ref minions[ev.Target.Index];
                    if (!m.Alive || m.BornFrame != ev.Target.BornFrame) break;
                    Fix64 dmg = ev.DmgType == DamageType.True ? ev.Damage : ApplyArmor(ev.Damage, m.Armor);
                    m.Hp -= dmg;
                    // PRD §4.3 attacker priority: if attacked by a hero, immediately retarget that hero.
                    if (ev.SourceSlot < heroes.Length)
                    {
                        Team attackerTeam = ev.SourceSlot < 5 ? Team.Blue : Team.Red;
                        if (attackerTeam != m.Team && heroes[ev.SourceSlot].Alive)
                            m.Target = new UnitRef { Kind = UnitKind.Hero, Index = ev.SourceSlot, BornFrame = 0 };
                    }
                    if (m.Hp <= Fix64.Zero)
                    {
                        m.Alive = false;
                        if (ev.SourceSlot < heroes.Length)
                            LevelSystem.AwardMinionKill(heroes, ev.SourceSlot, m.Type);
                    }
                    break;
                }
                case UnitKind.Tower:
                {
                    ref var t = ref towers[ev.Target.Index];
                    if (!t.Alive || t.BornFrame != ev.Target.BornFrame) break;
                    Fix64 dmg = ev.DmgType == DamageType.True ? ev.Damage : ApplyArmor(ev.Damage, t.Armor);
                    t.Hp -= dmg;
                    if (t.Hp <= Fix64.Zero)
                    {
                        t.Alive = false;
                        if (ev.SourceSlot < heroes.Length)
                            LevelSystem.AwardTowerKill(heroes, ev.SourceSlot);
                    }
                    break;
                }
                case UnitKind.Hero:
                {
                    ref var h = ref heroes[ev.Target.Index];
                    if (!h.Alive) break;
                    Fix64 effectiveArmor = h.Armor;
                    // Apply ArmorReduction buff stacks (PRD §7.7 破甲).
                    if (buffs != null && ev.DmgType == DamageType.Physical)
                    {
                        int idx = ev.Target.Index;
                        for (int s = 0; s < BuffEngine.MaxBuffsPerHero; s++)
                        {
                            ref var b = ref buffs[idx, s];
                            if (b.DefId == 0) continue;
                            ushort di = (ushort)(b.DefId - 1);
                            if (di < BuffEngine.DefCount && BuffEngine.Defs[di].Modifier == BuffModifierKind.ArmorReduction)
                                effectiveArmor -= BuffEngine.Defs[di].ModifierValue * (Fix64)b.Stack;
                        }
                        if (effectiveArmor < Fix64.Zero) effectiveArmor = Fix64.Zero;
                    }
                    Fix64 dmg;
                    if (ev.DmgType == DamageType.True) dmg = ev.Damage;
                    else if (ev.DmgType == DamageType.Magic) dmg = ApplyArmor(ev.Damage, h.MagicResist);
                    else dmg = ApplyArmor(ev.Damage, effectiveArmor);
                    h.Hp -= dmg;
                    if (h.Hp <= Fix64.Zero)
                    {
                        h.Alive = false;
                        h.Deaths++;
                        h.KillStreak = 0;   // PRD §7.3: reset streak on death
                        h.RespawnFrame = frame + (uint)Respawn.FramesFor(h.Level, frame);
                        if (ev.SourceSlot < heroes.Length && ev.SourceSlot != ev.Target.Index)
                        {
                            byte newStreak = ++heroes[ev.SourceSlot].KillStreak;
                            heroes[ev.SourceSlot].Gold += Items.HeroKillGold + KillStreakBonus(newStreak);
                            heroes[ev.SourceSlot].Kills++;
                        }
                    }
                    break;
                }
                case UnitKind.JungleMonster:
                {
                    if (jungle == null) break;
                    int ji = ev.Target.Index;
                    if (ji >= jungle.Length) break;
                    ref var jm = ref jungle[ji];
                    if (!jm.Alive || jm.BornFrame != ev.Target.BornFrame) break;
                    Fix64 jdmg = ev.DmgType == DamageType.True ? ev.Damage : ApplyArmor(ev.Damage, jm.Armor);
                    jm.Hp -= jdmg;
                    if (jm.Hp <= Fix64.Zero)
                    {
                        jm.Alive = false;
                        jm.RespawnFrame = frame + JungleSystem.RespawnFrames;
                        if (ev.SourceSlot < heroes.Length)
                            LevelSystem.AwardJungleKill(heroes, ev.SourceSlot);
                    }
                    break;
                }
            }
        }
        for (int i = 0; i < minions.Length; i++) if (minions[i].Alive) aliveMinionCount++;
        return count;
    }

    [NoGC]
    private static uint KillStreakBonus(byte streak)
    {
        // PRD §7.3: 连杀奖励. +50g per consecutive kill beyond first, cap at +300g.
        if (streak <= 1) return 0u;
        uint bonus = (uint)(streak - 1) * 50u;
        return bonus > 300u ? 300u : bonus;
    }

    [NoGC]
    private static Fix64 ApplyArmor(Fix64 raw, Fix64 armor)
    {
        // PRD §7.2: final = raw * 100 / (100 + armor), clamped to >=1.
        Fix64 mul = (Fix64)100 / ((Fix64)100 + armor);
        Fix64 d = raw * mul;
        return d < Fix64.One ? Fix64.One : d;
    }

    // ---- Helpers -------------------------------------------------------------------
    [NoGC]
    private static bool IsTargetAlive(UnitRef r, Minion[] minions, Tower[] towers, Hero[] heroes,
                                      JungleMonster[]? jungle = null)
    {
        switch (r.Kind)
        {
            case UnitKind.Minion: return r.Index < minions.Length && minions[r.Index].Alive && minions[r.Index].BornFrame == r.BornFrame;
            case UnitKind.Tower:  return r.Index < towers.Length  && towers[r.Index].Alive  && towers[r.Index].BornFrame  == r.BornFrame;
            case UnitKind.Hero:   return r.Index < heroes.Length  && heroes[r.Index].Alive;
            case UnitKind.JungleMonster:
                return jungle != null && r.Index < jungle.Length && jungle[r.Index].Alive && jungle[r.Index].BornFrame == r.BornFrame;
            default: return false;
        }
    }

    [NoGC]
    private static TSVector2 TargetPos(UnitRef r, Minion[] minions, Tower[] towers, Hero[] heroes,
                                       JungleMonster[]? jungle = null) => r.Kind switch
    {
        UnitKind.Minion        => minions[r.Index].Pos,
        UnitKind.Tower         => towers[r.Index].Pos,
        UnitKind.Hero          => heroes[r.Index].Pos,
        UnitKind.JungleMonster => jungle != null && r.Index < jungle.Length ? jungle[r.Index].Pos : default,
        _                      => default,
    };

    /// <summary>Move <paramref name="pos"/> linearly toward <paramref name="dest"/> by speed*dt
    /// using a Manhattan-projected step (sqrt-free, deterministic).</summary>
    [NoGC]
    private static void StepToward(ref TSVector2 pos, TSVector2 dest, Fix64 speed, Fix64 dt)
    {
        Fix64 dx = dest.X - pos.X, dy = dest.Y - pos.Y;
        Fix64 absDx = dx < Fix64.Zero ? -dx : dx;
        Fix64 absDy = dy < Fix64.Zero ? -dy : dy;
        Fix64 sum = absDx + absDy;
        if (sum <= Fix64.Zero) return;
        Fix64 step = speed * dt;
        if (sum <= step) { pos = dest; return; }
        // Normalised step: dx/sum * step (Manhattan-norm, slightly non-Euclidean — cheap & deterministic).
        pos = new TSVector2(pos.X + (dx / sum) * step, pos.Y + (dy / sum) * step);
    }
}
