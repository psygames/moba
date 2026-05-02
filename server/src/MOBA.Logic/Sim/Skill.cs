// SPDX-License-Identifier: MIT
// PRD §4.2.3 — SkillDef + EffectStep model.
// v3.0 extensions: ArmorShred (破甲), ConditionalDamageMul (狩猎之眼), SpawnWall (冰墙),
//                  passive flag, execute condition (斩首), reverse-teleport flag (位移射击),
//                  AoeOnExpiry projectile (陨石).
namespace MOBA.Logic.Sim;

public enum HitShape : byte { None = 0, Circle = 1, Sector = 2, Line = 3 }
public enum CastType : byte { Instant = 0, Direction = 1, Position = 2, Target = 3 }
public enum EffectKind : byte
{
    None = 0,
    Damage = 1,              // Param=base dmg; Param2=AD scale*1000; Param3=AP scale*1000
    Heal = 2,
    ApplyBuff = 3,           // Param=BuffDef.Id; Target determines who it applies to
    Pull = 4,                // Param=distance toward caster (HitTargets only)
    Knockback = 5,           // Param=distance pushed away from caster
    SpawnProjectile = 6,     // Param2=speed*10; Param3=life frames; Flags bit0=AoeOnExpiry; Flags bit1=BuffOnHit low byte
    SpawnWall = 7,           // spawn ice-wall; Param=half-length; Param2=duration frames; Param3=wall-slot-hint
    Teleport = 8,            // caster only; Param=distance; Flags bit2=reverse-dir (move away from aim)
    ArmorShred = 9,          // on-hit passive: apply stacking ArmorReduction buff; Param=ArmorReduction buff id
    ConditionalDamageMul = 10, // on-basic-attack passive: if target HP% < Param2, multiply raw dmg by Param (Fix64)
}
public enum EffectTarget : byte { Self = 0, Caster = 1, HitTargets = 2, Position = 3 }
public enum DamageType  : byte { Physical = 0, Magic = 1, True = 2 }

/// <summary>One step of a skill. v3.1: added BuffOnHit (ushort) for spells that apply a buff on damage.
/// Param2 = AD scale × 1000; Param3 = AP scale × 1000; BuffOnHit = BuffEngine.Defs index (0 = none).</summary>
public struct EffectStep
{
    public uint   DelayFrames;
    public EffectKind Kind;
    public EffectTarget Target;
    public DamageType  DmgType;
    /// <summary>bit0=stop-on-first-hit(proj); bit1=pierce(line); bit2=reverse-dir(teleport).</summary>
    public byte   Flags;
    public Fix64  Param;          // primary parameter
    public int    Param2;         // AD scale × 1000 (for Damage steps)
    public int    Param3;         // AP scale × 1000 (for Damage steps)
    /// <summary>Buff to apply on damage hit (0 = none). Used by e.g. 陨石 (stun on hit).</summary>
    public ushort BuffOnHit;
}

/// <summary>Skill definition (PRD §4.2.3). v3.0: SkillFlags bit0=IsPassive; CastTargetHpMaxPct for execute.</summary>
public struct SkillDef
{
    public ushort Id;
    public byte   OwnerHeroDefId;     // 0..2; 255 = generic
    /// <summary>bit0 = IsPassive (triggered by basic attack, not directly castable).</summary>
    public byte   SkillFlags;
    public uint   CdFrames;
    public Fix64  ManaCost;
    public Fix64  CastRange;
    public CastType Cast;
    public uint   PreCastFrames;
    public HitShape HitShape;
    public Fix64  HitParamA;          // radius for Circle / length for Line / sector arc-deg for Sector
    public Fix64  HitParamB;          // line width or sector half-angle (unused for circle)
    /// <summary>Execute condition: skill can only be cast when locked target HP/MaxHp ≤ this value.
    /// 0 = no condition. 0.30 = 30% (used by 斩首).</summary>
    public Fix64  CastTargetHpMaxPct;
    public byte   StepCount;
    public ushort StepStart;          // index into SkillEngine.Steps
}

/// <summary>Pending projectile (PRD §4.2.4). v3.0: AoeOnExpiry for 陨石.</summary>
public struct Projectile
{
    public TSVector2 Pos;
    public TSVector2 Velocity;       // m / sec (zero = stationary delayed AOE)
    public Fix64  Radius;
    public Fix64  Damage;            // computed at spawn from caster stats + step.Param
    public ushort SkillDefId;        // 0 = unused; otherwise (skillId+1) for analytics
    public byte   OwnerSlot;         // attacker hero slot (0..9)
    public Team   Team;
    public uint   ExpireFrame;
    public bool   Alive;
    public uint   BornFrame;
    /// <summary>If true: on expiry, deal Damage in AoE of Radius around Pos, then apply BuffOnHit.</summary>
    public bool   AoeOnExpiry;
    /// <summary>Buff id to apply to AoE targets on expiry / projectile impact (0=none).</summary>
    public ushort BuffOnHit;
}

/// <summary>Ice-wall placed by Mage W (冰墙). Static physics body blocks hero/minion movement. PRD §7.7.</summary>
public struct Wall
{
    public TSVector2 Pos;
    public Fix64  HalfLen;           // default 2m (total 4m)
    public Fix64  HalfWidth;         // default 0.25m
    public uint   ExpireFrame;
    public uint   BornFrame;
    public bool   Alive;
    public byte   OwnerSlot;
}
