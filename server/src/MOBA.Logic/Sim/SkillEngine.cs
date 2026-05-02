// SPDX-License-Identifier: MIT
// M5.1 — engines for buffs / projectiles / skill execution.
// All Tick paths are [NoGC]; tables (SkillDefs/EffectSteps/BuffDefs) are populated
// by BuiltinContent.Register() at world construction.

using System;

namespace MOBA.Logic.Sim;

public static class SkillEngine
{
    public const int MaxSkillDefs = 64;
    public const int MaxEffectSteps = 256;
    public static readonly SkillDef[] Defs = new SkillDef[MaxSkillDefs];
    public static readonly EffectStep[] Steps = new EffectStep[MaxEffectSteps];
    public static int DefCount;
    public static int StepCount;

    /// <summary>Reset tables to allow tests to re-register.</summary>
    public static void Reset() { DefCount = 0; StepCount = 0; Array.Clear(Defs, 0, Defs.Length); Array.Clear(Steps, 0, Steps.Length); }

    /// <summary>Register a SkillDef + its steps. Returns the SkillDef index (== Id by convention).</summary>
    public static ushort Register(SkillDef def, ReadOnlySpan<EffectStep> steps)
    {
        if (DefCount >= MaxSkillDefs) throw new InvalidOperationException("SkillEngine.Defs overflow");
        if (StepCount + steps.Length > MaxEffectSteps) throw new InvalidOperationException("SkillEngine.Steps overflow");
        def.StepStart = (ushort)StepCount;
        def.StepCount = (byte)steps.Length;
        for (int i = 0; i < steps.Length; i++) Steps[StepCount + i] = steps[i];
        StepCount += steps.Length;
        Defs[DefCount] = def;
        return (ushort)DefCount++;
    }
}

public static class BuffEngine
{
    public const int MaxBuffDefs = 64;
    public const int MaxBuffsPerHero = 16;
    public static readonly BuffDef[] Defs = new BuffDef[MaxBuffDefs];
    public static int DefCount;

    public static void Reset() { DefCount = 0; Array.Clear(Defs, 0, Defs.Length); }

    public static ushort Register(BuffDef def)
    {
        if (DefCount >= MaxBuffDefs) throw new InvalidOperationException("BuffEngine.Defs overflow");
        Defs[DefCount] = def;
        return (ushort)DefCount++;
    }

    /// <summary>Apply or stack a buff onto a hero. Returns true if added/refreshed.</summary>
    [NoGC]
    public static bool Apply(Hero[] heroes, BuffInstance[,] buffs, int heroIdx, ushort defId, byte sourceSlot, uint frame)
    {
        if (defId >= DefCount) return false;
        ref var def = ref Defs[defId];
        // Look for existing slot with same DefId.
        int slot = -1, free = -1;
        for (int s = 0; s < MaxBuffsPerHero; s++)
        {
            if (buffs[heroIdx, s].DefId == defId + 1) { slot = s; break; }
            if (free < 0 && buffs[heroIdx, s].DefId == 0) free = s;
        }
        if (slot >= 0)
        {
            ref var b = ref buffs[heroIdx, slot];
            switch (def.Stack)
            {
                case BuffStackPolicy.Independent: /* leave existing, add new if free */
                    if (free < 0) return false;
                    slot = free; break;
                case BuffStackPolicy.Refresh:
                    b.EndFrame = frame + def.DurationFrames;
                    return true;
                case BuffStackPolicy.Stack:
                    if (b.Stack < def.MaxStack) b.Stack++;
                    b.EndFrame = frame + def.DurationFrames;
                    return true;
            }
        }
        else slot = free;
        if (slot < 0) return false;
        ref var nb = ref buffs[heroIdx, slot];
        nb.DefId = (ushort)(defId + 1); // store +1 so 0 = empty
        nb.Stack = 1;
        nb.SourceSlot = sourceSlot;
        nb.EndFrame = frame + def.DurationFrames;
        nb.NextTickFrame = def.TickIntervalFrames > 0 ? frame + def.TickIntervalFrames : uint.MaxValue;
        nb.TagBits = def.TagBits;
        heroes[heroIdx].Tags |= def.TagBits;
        heroes[heroIdx].BuffCount++;
        return true;
    }

    /// <summary>Per-frame tick: expire buffs, run DoT/HoT, recompute aggregate tag mask.</summary>
    [NoGC]
    public static void Tick(Hero[] heroes, BuffInstance[,] buffs, uint frame, Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount)
    {
        for (int h = 0; h < heroes.Length; h++)
        {
            ref var hero = ref heroes[h];
            if (hero.BuffCount == 0) continue;
            ulong newTags = 0;
            byte alive = 0;
            for (int s = 0; s < MaxBuffsPerHero; s++)
            {
                ref var b = ref buffs[h, s];
                if (b.DefId == 0) continue;
                if (frame >= b.EndFrame) { b.DefId = 0; continue; }
                ref var def = ref Defs[b.DefId - 1];
                if (b.NextTickFrame != uint.MaxValue && frame >= b.NextTickFrame)
                {
                    b.NextTickFrame = frame + def.TickIntervalFrames;
                    if (def.Modifier == BuffModifierKind.DamageOverTime)
                    {
                        var ev = new GameSystems.DamageEvent
                        {
                            Target = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)h, BornFrame = 0 },
                            Damage = def.ModifierValue * (Fix64)b.Stack,
                            Frame = frame,
                            SourceSlot = b.SourceSlot,
                        };
                        if (dmgCount < dmgQ.Length) dmgQ[dmgCount++] = ev;
                    }
                    else if (def.Modifier == BuffModifierKind.HealOverTime)
                    {
                        hero.Hp += def.ModifierValue * (Fix64)b.Stack;
                        if (hero.Hp > hero.MaxHp) hero.Hp = hero.MaxHp;
                    }
                }
                newTags |= b.TagBits;
                alive++;
            }
            hero.Tags = newTags;
            hero.BuffCount = alive;
        }
    }
}

/// <summary>Skill cast + projectile lifecycle. PRD §4.2.3 / §7.7.</summary>
public static class SkillSystem
{
    public const int MaxProjectiles = 80;

    /// <summary>Try to cast skill <paramref name="skillSlot"/> (0..3) for hero <paramref name="heroIdx"/>.
    /// Returns true if cast succeeded (CD started, mana paid, instant steps queued).
    /// v3.0: honours CastTargetHpMaxPct (execute condition) and IsPassive check.</summary>
    [NoGC]
    public static bool TryCast(Hero[] heroes, BuffInstance[,] buffs, int heroIdx, byte skillSlot, ushort skillDefId,
                               TSVector2 aim, Projectile[] projectiles, uint frame,
                               Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount,
                               Wall[]? walls = null)
    {
        ref var h = ref heroes[heroIdx];
        if (!h.Alive) return false;
        if ((h.Tags & GameplayTags.CannotCast) != 0) return false;
        if (skillDefId >= SkillEngine.DefCount) return false;
        ref var def = ref SkillEngine.Defs[skillDefId];
        // Passive skills are never cast directly.
        if ((def.SkillFlags & 1) != 0) return false;
        uint cd = skillSlot switch { 0 => h.SkillCd0, 1 => h.SkillCd1, 2 => h.SkillCd2, 3 => h.SkillCd3, _ => uint.MaxValue };
        if (frame < cd) return false;
        if (h.Mp < def.ManaCost) return false;
        // Clamp aim to CastRange (Direction/Position only).
        if (def.Cast == CastType.Direction || def.Cast == CastType.Position)
        {
            TSVector2 d = aim - h.Pos;
            Fix64 absX = d.X < Fix64.Zero ? -d.X : d.X;
            Fix64 absY = d.Y < Fix64.Zero ? -d.Y : d.Y;
            Fix64 mag = absX + absY; // Manhattan; deterministic, conservative
            if (mag > def.CastRange && mag > Fix64.Zero)
            {
                Fix64 k = def.CastRange / mag;
                aim = new TSVector2(h.Pos.X + d.X * k, h.Pos.Y + d.Y * k);
            }
        }
        // PRD §7.7 execute condition (斩首): CastTargetHpMaxPct > 0 → only cast if target in range is below threshold.
        if (def.CastTargetHpMaxPct > Fix64.Zero)
        {
            if (!HasTargetBelowHpPct(heroes, heroIdx, aim, def.HitParamA, def.CastTargetHpMaxPct))
                return false; // CD not consumed — condition not met
        }
        h.Mp -= def.ManaCost;
        // CD with CDR. cdrPct ∈ [60..100].
        int cdrPct = 100 - (int)h.Cdr;
        if (cdrPct < 60) cdrPct = 60;
        uint cdEnd = frame + (uint)((long)def.CdFrames * cdrPct / 100L);
        switch (skillSlot)
        {
            case 0: h.SkillCd0 = cdEnd; break;
            case 1: h.SkillCd1 = cdEnd; break;
            case 2: h.SkillCd2 = cdEnd; break;
            case 3: h.SkillCd3 = cdEnd; break;
        }
        ExecuteSteps(heroes, buffs, heroIdx, skillDefId, aim, projectiles, frame, dmgQ, ref dmgCount, walls);
        return true;
    }

    /// <summary>Returns true if any enemy hero within <paramref name="radius"/> of <paramref name="centre"/>
    /// has HP/MaxHp ≤ <paramref name="hpPct"/>. Used for execute-condition check (PRD §7.7 斩首).</summary>
    [NoGC]
    private static bool HasTargetBelowHpPct(Hero[] heroes, int casterIdx, TSVector2 centre, Fix64 radius, Fix64 hpPct)
    {
        Team enemy = casterIdx < 5 ? Team.Red : Team.Blue;
        Fix64 r2 = radius * radius;
        for (int i = 0; i < heroes.Length; i++)
        {
            Team tT = i < 5 ? Team.Blue : Team.Red;
            if (tT != enemy) continue;
            ref var h = ref heroes[i];
            if (!h.Alive || h.MaxHp <= Fix64.Zero) continue;
            Fix64 dx = h.Pos.X - centre.X, dy = h.Pos.Y - centre.Y;
            if (dx * dx + dy * dy > r2) continue;
            if (h.Hp / h.MaxHp <= hpPct) return true;
        }
        return false;
    }

    [NoGC]
    private static void ExecuteSteps(Hero[] heroes, BuffInstance[,] buffs, int heroIdx, ushort skillDefId, TSVector2 aim,
                                     Projectile[] projectiles, uint frame,
                                     Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount,
                                     Wall[]? walls = null)
    {
        ref var def = ref SkillEngine.Defs[skillDefId];
        ref var caster = ref heroes[heroIdx];
        for (int i = 0; i < def.StepCount; i++)
        {
            ref var step = ref SkillEngine.Steps[def.StepStart + i];
            // Delayed steps (e.g. 陨石) are queued as stationary AoE projectiles.
            if (step.DelayFrames != 0)
            {
                if (step.Kind == EffectKind.Damage)
                    SpawnDelayedAoe(projectiles, aim, def.HitParamA, step, caster, heroIdx, skillDefId, frame);
                continue;
            }
            switch (step.Kind)
            {
                case EffectKind.Damage:
                {
                    // If DelayFrames > 0, spawn a stationary AoE projectile (PRD §7.7 陨石).
                    if (step.DelayFrames > 0)
                    {
                        Fix64 r = def.HitParamA > Fix64.Zero ? def.HitParamA : (Fix64)3;
                        SpawnDelayedAoe(projectiles, aim, r, in step, in caster, heroIdx, skillDefId, frame);
                        break;
                    }
                    Fix64 amount = ComputeDamage(in step, in caster);
                    Team enemy = heroIdx < 5 ? Team.Red : Team.Blue;
                    Fix64 rad = def.HitParamA > Fix64.Zero ? def.HitParamA : (Fix64)1;
                    if (def.HitShape == HitShape.Line)
                        ApplyLineDamage(heroes, minions: null, enemy, caster.Pos, aim, rad, def.HitParamB, amount, step.DmgType, frame, dmgQ, ref dmgCount, (byte)heroIdx);
                    else
                        ApplyAoeDamage(heroes, enemy, aim, rad, amount, step.DmgType, frame, dmgQ, ref dmgCount, (byte)heroIdx);
                    break;
                }
                case EffectKind.Heal:
                    caster.Hp += step.Param;
                    if (caster.Hp > caster.MaxHp) caster.Hp = caster.MaxHp;
                    break;
                case EffectKind.ApplyBuff:
                {
                    ushort buffId = (ushort)((int)step.Param);
                    if (step.Target == EffectTarget.Self || step.Target == EffectTarget.Caster)
                    {
                        BuffEngine.Apply(heroes, buffs, heroIdx, buffId, (byte)heroIdx, frame);
                    }
                    else
                    {
                        Team enemy = heroIdx < 5 ? Team.Red : Team.Blue;
                        Fix64 r = def.HitParamA > Fix64.Zero ? def.HitParamA : (Fix64)1;
                        Fix64 r2 = r * r;
                        for (int t = 0; t < heroes.Length; t++)
                        {
                            Team tT = t < 5 ? Team.Blue : Team.Red;
                            if (tT != enemy) continue;
                            ref var th = ref heroes[t];
                            if (!th.Alive) continue;
                            Fix64 dx = th.Pos.X - aim.X, dy = th.Pos.Y - aim.Y;
                            if (dx * dx + dy * dy > r2) continue;
                            BuffEngine.Apply(heroes, buffs, t, buffId, (byte)heroIdx, frame);
                        }
                    }
                    break;
                }
                case EffectKind.SpawnProjectile:
                {
                    int slot = -1;
                    for (int p = 0; p < projectiles.Length; p++) if (!projectiles[p].Alive) { slot = p; break; }
                    if (slot < 0) break;
                    Fix64 speed = (Fix64)step.Param2 / (Fix64)10;
                    uint life = (uint)step.Param3;
                    TSVector2 dir = aim - caster.Pos;
                    Fix64 absX = dir.X < Fix64.Zero ? -dir.X : dir.X;
                    Fix64 absY = dir.Y < Fix64.Zero ? -dir.Y : dir.Y;
                    Fix64 mag = absX + absY;
                    if (mag <= Fix64.Zero) break;
                    // On-hit damage: use sibling Damage step if present, else step.Param.
                    Fix64 dmg = step.Param;
                    for (int j = 0; j < def.StepCount; j++)
                    {
                        ref var s2 = ref SkillEngine.Steps[def.StepStart + j];
                        if (s2.Kind == EffectKind.Damage) { dmg = ComputeDamage(in s2, in caster); break; }
                    }
                    if (dmg <= Fix64.Zero) dmg = (Fix64)40;
                    // bit1 of step.Flags: pierce (don't stop on first hit)
                    bool pierce = (step.Flags & 2) != 0;
                    projectiles[slot] = new Projectile
                    {
                        Pos = caster.Pos,
                        Velocity = new TSVector2(dir.X / mag * speed, dir.Y / mag * speed),
                        Radius = pierce ? (Fix64)0.5f : (Fix64)0.4f,
                        Damage = dmg,
                        SkillDefId = (ushort)(skillDefId + 1),
                        OwnerSlot = (byte)heroIdx,
                        Team = heroIdx < 5 ? Team.Blue : Team.Red,
                        ExpireFrame = frame + life,
                        Alive = true,
                        BornFrame = frame,
                        AoeOnExpiry = false,
                        BuffOnHit = 0,
                    };
                    break;
                }
                case EffectKind.SpawnWall:
                {
                    // PRD §7.7 冰墙: place 4m wall at aim point for Param2 frames.
                    if (walls == null) break;
                    for (int w = 0; w < walls.Length; w++)
                    {
                        if (walls[w].Alive) continue;
                        walls[w] = new Wall
                        {
                            Pos = aim,
                            HalfLen = step.Param > Fix64.Zero ? step.Param : (Fix64)2,
                            HalfWidth = (Fix64)0.25f,
                            ExpireFrame = frame + (uint)step.Param2,
                            BornFrame = frame,
                            Alive = true,
                            OwnerSlot = (byte)heroIdx,
                        };
                        break;
                    }
                    break;
                }
                case EffectKind.Teleport:
                {
                    // bit2 of Flags: reverse direction (位移射击 — move away from aim).
                    bool reverse = (step.Flags & 4) != 0;
                    if (reverse)
                    {
                        TSVector2 d = aim - caster.Pos;
                        Fix64 absX = d.X < Fix64.Zero ? -d.X : d.X;
                        Fix64 absY = d.Y < Fix64.Zero ? -d.Y : d.Y;
                        Fix64 mag = absX + absY;
                        Fix64 dist = step.Param > Fix64.Zero ? step.Param : (Fix64)2;
                        if (mag > Fix64.Zero)
                        {
                            Fix64 k = dist / mag;
                            caster.Pos = new TSVector2(caster.Pos.X - d.X * k, caster.Pos.Y - d.Y * k);
                        }
                    }
                    else
                    {
                        caster.Pos = aim;
                    }
                    break;
                }
                case EffectKind.Knockback:
                {
                    // Push all enemies in HitParamA radius away from caster by step.Param metres.
                    Team enemy = heroIdx < 5 ? Team.Red : Team.Blue;
                    Fix64 r = def.HitParamA > Fix64.Zero ? def.HitParamA : (Fix64)2;
                    Fix64 r2 = r * r;
                    Fix64 pushDist = step.Param > Fix64.Zero ? step.Param : (Fix64)3;
                    for (int t = 0; t < heroes.Length; t++)
                    {
                        Team tT = t < 5 ? Team.Blue : Team.Red;
                        if (tT != enemy) continue;
                        ref var th = ref heroes[t];
                        if (!th.Alive) continue;
                        Fix64 dx = th.Pos.X - caster.Pos.X, dy = th.Pos.Y - caster.Pos.Y;
                        if (dx * dx + dy * dy > r2) continue;
                        Fix64 absX = dx < Fix64.Zero ? -dx : dx;
                        Fix64 absY = dy < Fix64.Zero ? -dy : dy;
                        Fix64 mag = absX + absY;
                        if (mag <= Fix64.Zero) { th.Pos = new TSVector2(th.Pos.X + pushDist, th.Pos.Y); continue; }
                        Fix64 k = pushDist / mag;
                        th.Pos = new TSVector2(th.Pos.X + dx * k, th.Pos.Y + dy * k);
                    }
                    break;
                }
                // ArmorShred and ConditionalDamageMul are passive-only (triggered in TickHeroes).
            }
        }
    }

    /// <summary>Spawn a stationary AoE projectile for delayed effects (PRD §7.7 陨石).</summary>
    [NoGC]
    private static void SpawnDelayedAoe(Projectile[] projectiles, TSVector2 pos, Fix64 radius,
                                        in EffectStep step, in Hero caster, int heroIdx, ushort skillDefId, uint frame)
    {
        int slot = -1;
        for (int p = 0; p < projectiles.Length; p++) if (!projectiles[p].Alive) { slot = p; break; }
        if (slot < 0) return;
        Fix64 dmg = ComputeDamage(in step, in caster);
        projectiles[slot] = new Projectile
        {
            Pos = pos,
            Velocity = default,          // stationary
            Radius = radius > Fix64.Zero ? radius : (Fix64)3,
            Damage = dmg,
            SkillDefId = (ushort)(skillDefId + 1),
            OwnerSlot = (byte)heroIdx,
            Team = heroIdx < 5 ? Team.Blue : Team.Red,
            ExpireFrame = frame + step.DelayFrames,
            Alive = true,
            BornFrame = frame,
            AoeOnExpiry = true,
            // BuffOnHit stored in the dedicated EffectStep field (v3.1).
            BuffOnHit = step.BuffOnHit,
        };
    }

    /// <summary>Trigger passive effects on basic-attack hit. Called by GameSystems.TickHeroes.
    /// PRD §7.7: 破甲 (ArmorShred on hit) and 狩猎之眼 (ConditionalDamageMul).</summary>
    [NoGC]
    public static Fix64 ApplyPassivesOnHit(Hero[] heroes, BuffInstance[,] buffs, int heroIdx, UnitRef target,
                                           Fix64 rawDamage, uint frame)
    {
        ref var h = ref heroes[heroIdx];
        Fix64 finalDmg = rawDamage;
        for (byte slot = 0; slot < 4; slot++)
        {
            ushort defId = BuiltinContent.HeroSkills[h.HeroDefId, slot];
            if (defId >= SkillEngine.DefCount) continue;
            ref var def = ref SkillEngine.Defs[defId];
            if ((def.SkillFlags & 1) == 0) continue; // not passive
            for (int s = 0; s < def.StepCount; s++)
            {
                ref var step = ref SkillEngine.Steps[def.StepStart + s];
                switch (step.Kind)
                {
                    case EffectKind.ArmorShred:
                        // Apply ArmorReduction buff to hit target hero.
                        if (target.Kind == UnitKind.Hero && target.Index < heroes.Length)
                        {
                            ushort arBuff = (ushort)(int)step.Param;
                            if (arBuff < BuffEngine.DefCount)
                                BuffEngine.Apply(heroes, buffs, target.Index, arBuff, (byte)heroIdx, frame);
                        }
                        break;
                    case EffectKind.ConditionalDamageMul:
                        // Multiply damage if target HP% < Param2.
                        if (target.Kind == UnitKind.Hero && target.Index < heroes.Length)
                        {
                            ref var th = ref heroes[target.Index];
                            if (th.MaxHp > Fix64.Zero)
                            {
                                Fix64 hpPct = th.Hp / th.MaxHp;
                                Fix64 threshold = (Fix64)step.Param2 / (Fix64)100;
                                if (hpPct < threshold)
                                    finalDmg = finalDmg * step.Param;
                            }
                        }
                        break;
                }
            }
        }
        return finalDmg;
    }

    [NoGC]
    private static Fix64 ComputeDamage(in EffectStep step, in Hero caster)
        => step.Param + caster.Ad * ((Fix64)step.Param2 / (Fix64)1000)
                      + caster.Ap * ((Fix64)step.Param3 / (Fix64)1000);

    [NoGC]
    private static void ApplyAoeDamage(Hero[] heroes, Team enemy, TSVector2 centre, Fix64 radius,
                                       Fix64 amount, DamageType dmgType, uint frame,
                                       Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount,
                                       byte sourceSlot = 0xFF)
    {
        Fix64 r2 = radius * radius;
        for (int i = 0; i < heroes.Length; i++)
        {
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam != enemy) continue;
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            Fix64 dx = h.Pos.X - centre.X, dy = h.Pos.Y - centre.Y;
            if (dx * dx + dy * dy > r2) continue;
            if (dmgCount >= dmgQ.Length) return;
            dmgQ[dmgCount++] = new GameSystems.DamageEvent
            {
                Target = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)i, BornFrame = 0 },
                Damage = amount,
                Frame = frame,
                DmgType = dmgType,
                SourceSlot = sourceSlot,
            };
        }
    }

    /// <summary>Line AoE — hits all enemies within <paramref name="width"/> of the line from
    /// <paramref name="origin"/> toward <paramref name="end"/>. Pierce flag: hits all; else stop on first.</summary>
    [NoGC]
    private static void ApplyLineDamage(Hero[] heroes, Minion[]? minions, Team enemy,
                                        TSVector2 origin, TSVector2 end,
                                        Fix64 length, Fix64 halfWidth, Fix64 amount,
                                        DamageType dmgType, uint frame,
                                        Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount,
                                        byte sourceSlot = 0xFF)
    {
        // Direction vector (Manhattan).
        TSVector2 d = new TSVector2(end.X - origin.X, end.Y - origin.Y);
        Fix64 absX = d.X < Fix64.Zero ? -d.X : d.X;
        Fix64 absY = d.Y < Fix64.Zero ? -d.Y : d.Y;
        Fix64 mag = absX + absY;
        if (mag <= Fix64.Zero) return;
        Fix64 hw = halfWidth > Fix64.Zero ? halfWidth : (Fix64)0.6f;
        for (int i = 0; i < heroes.Length; i++)
        {
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam != enemy) continue;
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            // Project hero onto line; check within [0, length] and within halfWidth laterally.
            Fix64 tx = h.Pos.X - origin.X, ty = h.Pos.Y - origin.Y;
            Fix64 proj = (tx * d.X + ty * d.Y) / mag; // approximate dot product / |d| using Manhattan
            if (proj < Fix64.Zero || proj > length) continue;
            Fix64 lat = (ty * d.X - tx * d.Y) / mag;
            if (lat < Fix64.Zero) lat = -lat;
            if (lat > hw) continue;
            if (dmgCount >= dmgQ.Length) return;
            dmgQ[dmgCount++] = new GameSystems.DamageEvent
            {
                Target = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)i, BornFrame = 0 },
                Damage = amount,
                Frame = frame,
                DmgType = dmgType,
                SourceSlot = sourceSlot,
            };
        }
        if (minions == null) return;
        for (int i = 0; i < minions.Length; i++)
        {
            ref var m = ref minions[i];
            if (!m.Alive || m.Team == enemy) continue;
            Fix64 tx = m.Pos.X - origin.X, ty = m.Pos.Y - origin.Y;
            Fix64 proj = (tx * d.X + ty * d.Y) / mag;
            if (proj < Fix64.Zero || proj > length) continue;
            Fix64 lat = (ty * d.X - tx * d.Y) / mag;
            if (lat < Fix64.Zero) lat = -lat;
            if (lat > hw) continue;
            if (dmgCount >= dmgQ.Length) return;
            dmgQ[dmgCount++] = new GameSystems.DamageEvent
            {
                Target = new UnitRef { Kind = UnitKind.Minion, Index = (ushort)i, BornFrame = m.BornFrame },
                Damage = amount,
                Frame = frame,
                DmgType = dmgType,
                SourceSlot = sourceSlot,
            };
        }
    }

    /// <summary>Move projectiles, detect hits, expire. Handles AoeOnExpiry for 陨石.</summary>
    [NoGC]
    public static void TickProjectiles(Projectile[] projectiles, Hero[] heroes, Minion[] minions, Tower[] towers,
                                       Fix64 dt, uint frame,
                                       Span<GameSystems.DamageEvent> dmgQ, ref int dmgCount,
                                       BuffInstance[,]? buffs = null)
    {
        for (int i = 0; i < projectiles.Length; i++)
        {
            ref var p = ref projectiles[i];
            if (!p.Alive) continue;
            if (frame >= p.ExpireFrame)
            {
                p.Alive = false;
                // AoeOnExpiry: delayed AOE hits (PRD §7.7 陨石).
                if (p.AoeOnExpiry)
                {
                    Fix64 r2 = p.Radius * p.Radius;
                    for (int h = 0; h < heroes.Length; h++)
                    {
                        Team hTeam = h < 5 ? Team.Blue : Team.Red;
                        if (hTeam == p.Team) continue;
                        ref var hh = ref heroes[h];
                        if (!hh.Alive) continue;
                        Fix64 dx = hh.Pos.X - p.Pos.X, dy = hh.Pos.Y - p.Pos.Y;
                        if (dx * dx + dy * dy > r2) continue;
                        if (dmgCount < dmgQ.Length)
                            dmgQ[dmgCount++] = new GameSystems.DamageEvent
                            {
                                Target = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)h, BornFrame = 0 },
                                Damage = p.Damage, Frame = frame, DmgType = DamageType.Magic,
                                SourceSlot = p.OwnerSlot,
                            };
                        if (p.BuffOnHit > 0 && buffs != null)
                            BuffEngine.Apply(heroes, buffs, h, (ushort)(p.BuffOnHit - 1), p.OwnerSlot, frame);
                    }
                }
                continue;
            }
            // Stationary delayed AOE — don't move.
            if (p.AoeOnExpiry) continue;
            p.Pos = new TSVector2(p.Pos.X + p.Velocity.X * dt, p.Pos.Y + p.Velocity.Y * dt);
            // Collision: enemy heroes and minions.
            Fix64 pr2 = p.Radius * p.Radius;
            for (int h = 0; h < heroes.Length; h++)
            {
                Team hTeam = h < 5 ? Team.Blue : Team.Red;
                if (hTeam == p.Team) continue;
                ref var hh = ref heroes[h];
                if (!hh.Alive) continue;
                Fix64 dx = hh.Pos.X - p.Pos.X, dy = hh.Pos.Y - p.Pos.Y;
                if (dx * dx + dy * dy > pr2 + (Fix64)0.25f) continue;
                if (dmgCount < dmgQ.Length) dmgQ[dmgCount++] = new GameSystems.DamageEvent
                {
                    Target = new UnitRef { Kind = UnitKind.Hero, Index = (ushort)h, BornFrame = 0 },
                    Damage = p.Damage, Frame = frame,
                    SourceSlot = p.OwnerSlot,
                };
                p.Alive = false; goto next;
            }
            for (int m = 0; m < minions.Length; m++)
            {
                ref var mm = ref minions[m];
                if (!mm.Alive || mm.Team == p.Team) continue;
                Fix64 dx = mm.Pos.X - p.Pos.X, dy = mm.Pos.Y - p.Pos.Y;
                if (dx * dx + dy * dy > pr2 + (Fix64)0.25f) continue;
                if (dmgCount < dmgQ.Length) dmgQ[dmgCount++] = new GameSystems.DamageEvent
                {
                    Target = new UnitRef { Kind = UnitKind.Minion, Index = (ushort)m, BornFrame = mm.BornFrame },
                    Damage = p.Damage, Frame = frame,
                    SourceSlot = p.OwnerSlot,
                };
                p.Alive = false; goto next;
            }
            // Out of map?
            if (p.Pos.X < (Fix64)(-55) || p.Pos.X > (Fix64)55 || p.Pos.Y < (Fix64)(-55) || p.Pos.Y > (Fix64)55)
                p.Alive = false;
            next: ;
        }
    }
}
