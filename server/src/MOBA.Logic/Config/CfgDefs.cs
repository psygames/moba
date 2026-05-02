// SPDX-License-Identifier: MIT
// §8 — Configuration table data types (analogous to Luban-generated Cfg.* classes).
// These are what config.bin stores; loaded by ConfigManager at startup.

namespace MOBA.Logic.Config;

/// <summary>
/// Hero config row (hero.xlsx).
/// Holds level-1 base stats and per-level growth values.
/// </summary>
public struct CfgHero
{
    public byte   Id;               // 0..HeroCount-1

    /// <summary>Runtime SkillEngine.Defs indices for Q / W / E / R slots.</summary>
    public ushort SkillQ, SkillW, SkillE, SkillR;

    // ── Level-1 base stats ────────────────────────────────────────────────
    public Fix64 MaxHp, MaxMp, Ad, Ap, Armor, MagicResist;
    public Fix64 AttackRange, AttackSpeed, MoveSpeed;

    // ── Per-level growth (added each time the hero levels up) ─────────────
    public Fix64 HpPerLv, MpPerLv, AdPerLv, ApPerLv;
}

/// <summary>
/// Level-exp table row (level.xlsx).
/// ExpRequired = experience needed to advance from (Level-1) → Level.
/// Level 1 has ExpRequired = 0 (starting level).
/// </summary>
public struct CfgLevel
{
    public byte  Level;         // 1..18
    public uint  ExpRequired;
}

/// <summary>One (X,Y) waypoint in a lane path.</summary>
public struct CfgWaypoint
{
    public Fix64 X, Y;
}

/// <summary>
/// Lane path definition (map_path.xlsx).
/// Three lanes (Top=0, Mid=1, Bot=2) × 8 waypoints each.
/// </summary>
public struct CfgLane
{
    public byte          Id;              // 0=Top 1=Mid 2=Bot
    public byte          WaypointCount;
    public CfgWaypoint[] Waypoints;       // length == WaypointCount
}
