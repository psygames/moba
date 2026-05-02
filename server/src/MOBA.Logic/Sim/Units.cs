// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Sim;

public enum Team : byte { Blue = 0, Red = 1 }
public enum UnitKind : byte { None = 0, Hero = 1, Minion = 2, Tower = 3, Projectile = 4, JungleMonster = 5 }
public enum MinionType : byte { Melee = 0, Caster = 1, Siege = 2 }

/// <summary>Compact target reference: 4 bytes packed.</summary>
public struct UnitRef
{
    public UnitKind Kind;
    public ushort Index;
    public uint BornFrame; // versioning so a slot reuse can't be mistaken for the old unit
    public bool IsValid => Kind != UnitKind.None;
    public static readonly UnitRef None = default;
}

/// <summary>Hero state. 10 instances, one per slot.</summary>
public struct Hero
{
    public TSVector2 Pos;          // mirrored from physics body each tick
    public Fix64 Hp, MaxHp;
    public Fix64 Mp, MaxMp;
    public Fix64 Ad, Ap;
    public Fix64 Armor, MagicResist;
    public Fix64 AttackRange;
    public Fix64 AttackSpeed;      // attacks/sec (Fix64)
    public Fix64 MoveSpeed;
    public Fix64 Cdr;              // 0..40
    public byte Level;
    public byte HeroDefId;         // 0..2 (sample heroes A/B/C)
    public uint Xp;
    public uint Gold;
    public bool Alive;
    public uint RespawnFrame;
    public uint AttackCdEndFrame;
    public uint Kills;
    public uint Deaths;
    public byte KillStreak;    // consecutive hero kills; resets to 0 on death
    public UnitRef Target;
    // M5: skill cooldowns (4 active skills) end-frames.
    public uint SkillCd0, SkillCd1, SkillCd2, SkillCd3;
    // M5: gameplay tags (ulong bitmask, see GameplayTags).
    public ulong Tags;
    // M5: number of active buff slots (max GameSystems.MaxBuffsPerHero).
    public byte BuffCount;
    // M5.3: 6-slot inventory (item id+1; 0 = empty). Inline (no array per hero).
    public byte Inv0, Inv1, Inv2, Inv3, Inv4, Inv5;
    public byte InvCount;
}

public struct Minion
{
    public TSVector2 Pos;
    public Fix64 Hp, MaxHp;
    public Fix64 Ad, Armor;
    public Fix64 AttackRange;
    public Fix64 MoveSpeed;
    public MinionType Type;
    public Team Team;
    public byte Lane;          // 0..2
    public byte WaypointIdx;
    public bool Alive;
    public uint BornFrame;
    public uint AttackCdEndFrame;
    public UnitRef Target;
}

public struct Tower
{
    public TSVector2 Pos;
    public Fix64 Hp, MaxHp;
    public Fix64 Ad, Armor;
    public Fix64 AttackRange;
    public Team Team;
    public byte Lane;
    public byte Tier;          // 0 = outer, 1 = base
    public bool Alive;
    public uint BornFrame;
    public uint AttackCdEndFrame;
    public UnitRef Target;
}

/// <summary>
/// Jungle camp monster (PRD §4.3 / §7.1). Stationary; spawns at frame 900 (60 s),
/// respawns 90 s after death. Attacks any hero within aggro range.
/// </summary>
public struct JungleMonster
{
    public TSVector2 Pos;           // home / spawn position (does not move)
    public Fix64 Hp, MaxHp;
    public Fix64 Ad, Armor;
    public Fix64 AggroRange;        // aggro + attack range
    public byte CampId;             // 0..3 (four jungle quadrants)
    public byte MonsterId;          // 0 or 1 within camp
    public bool Alive;
    public uint BornFrame;          // 0 = never spawned yet
    public uint AttackCdEndFrame;
    public uint RespawnFrame;       // >0 = waiting to respawn at this frame
}
