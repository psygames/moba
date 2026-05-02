// SPDX-License-Identifier: MIT
// §8 ConfigBaker — generates server/config/config.bin from PRD §7.7 data.
// Usage: dotnet run --project tools/ConfigBaker/ConfigBaker.csproj [output-dir]
//
// This plays the role of "luban_export.cmd": reads the authoritative data
// (hardcoded here, would be Excel sheets in a full Luban pipeline) and writes
// the binary config that server / client load at startup via ConfigManager.

using System;
using System.IO;
using MOBA.Logic.Config;
using MOBA.Logic.Sim;
using Fix64 = Box2DSharp.Common.FP;

// ── Determine output path ─────────────────────────────────────────────────────
string outDir = args.Length > 0 ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config");
Directory.CreateDirectory(outDir);
string outFile = Path.Combine(outDir, "config.bin");

// ── Register all runtime tables via BuiltinContent (exactly as in production) ─
MOBA.Logic.Sim.BuiltinContent.Register();

// ── PRD §7.7 Hero config (level-1 stats + per-level growth) ──────────────────
//
// Hero 0 — Swordsman (Warrior, melee AD)
// HP 600(+80/lv)  MP 200  AD 60(+5)  AP 0  ARM 25  MR 25  AS 0.7  MS 6.5
//
// Hero 1 — Mage (mid-range AP)
// HP 480(+60/lv)  MP 400  AD 45  AP 50(+8/lv)  ARM 18  MR 25  AS 0.6  MS 6.0
//
// Hero 2 — Marksman (ranged AD)
// HP 520(+70/lv)  MP 250  AD 55(+4/lv)  AP 0  ARM 20  MR 25  AS 0.85(+0.03)  MS 6.2
var heroes = new CfgHero[]
{
    new CfgHero
    {
        Id = 0,
        SkillQ = BuiltinContent.HeroSkills[0, 0],
        SkillW = BuiltinContent.HeroSkills[0, 1],
        SkillE = BuiltinContent.HeroSkills[0, 2],
        SkillR = BuiltinContent.HeroSkills[0, 3],
        MaxHp = (Fix64)600, MaxMp = (Fix64)200, Ad = (Fix64)60, Ap = Fix64.Zero,
        Armor = (Fix64)25, MagicResist = (Fix64)25,
        AttackRange = (Fix64)1.5f, AttackSpeed = (Fix64)0.7f, MoveSpeed = (Fix64)6.5f,
        HpPerLv = (Fix64)80, MpPerLv = Fix64.Zero, AdPerLv = (Fix64)5, ApPerLv = Fix64.Zero,
    },
    new CfgHero
    {
        Id = 1,
        SkillQ = BuiltinContent.HeroSkills[1, 0],
        SkillW = BuiltinContent.HeroSkills[1, 1],
        SkillE = BuiltinContent.HeroSkills[1, 2],
        SkillR = BuiltinContent.HeroSkills[1, 3],
        MaxHp = (Fix64)480, MaxMp = (Fix64)400, Ad = (Fix64)45, Ap = (Fix64)50,
        Armor = (Fix64)18, MagicResist = (Fix64)25,
        AttackRange = (Fix64)5, AttackSpeed = (Fix64)0.6f, MoveSpeed = (Fix64)6,
        HpPerLv = (Fix64)60, MpPerLv = Fix64.Zero, AdPerLv = Fix64.Zero, ApPerLv = (Fix64)8,
    },
    new CfgHero
    {
        Id = 2,
        SkillQ = BuiltinContent.HeroSkills[2, 0],
        SkillW = BuiltinContent.HeroSkills[2, 1],
        SkillE = BuiltinContent.HeroSkills[2, 2],
        SkillR = BuiltinContent.HeroSkills[2, 3],
        MaxHp = (Fix64)520, MaxMp = (Fix64)250, Ad = (Fix64)55, Ap = Fix64.Zero,
        Armor = (Fix64)20, MagicResist = (Fix64)25,
        AttackRange = (Fix64)6, AttackSpeed = (Fix64)0.85f, MoveSpeed = (Fix64)6.2f,
        HpPerLv = (Fix64)70, MpPerLv = Fix64.Zero, AdPerLv = (Fix64)4, ApPerLv = Fix64.Zero,
    },
};

// ── PRD §7.3 Level-exp table (Level 1→18) ────────────────────────────────────
// Exp to reach each level from the previous one.
// Level 1 = starting level (ExpRequired = 0).
var levels = new CfgLevel[]
{
    new CfgLevel { Level =  1, ExpRequired =    0 },
    new CfgLevel { Level =  2, ExpRequired =  280 },
    new CfgLevel { Level =  3, ExpRequired =  380 },
    new CfgLevel { Level =  4, ExpRequired =  480 },
    new CfgLevel { Level =  5, ExpRequired =  580 },
    new CfgLevel { Level =  6, ExpRequired =  680 },
    new CfgLevel { Level =  7, ExpRequired =  780 },
    new CfgLevel { Level =  8, ExpRequired =  880 },
    new CfgLevel { Level =  9, ExpRequired = 1000 },
    new CfgLevel { Level = 10, ExpRequired = 1100 },
    new CfgLevel { Level = 11, ExpRequired = 1200 },
    new CfgLevel { Level = 12, ExpRequired = 1300 },
    new CfgLevel { Level = 13, ExpRequired = 1400 },
    new CfgLevel { Level = 14, ExpRequired = 1500 },
    new CfgLevel { Level = 15, ExpRequired = 1600 },
    new CfgLevel { Level = 16, ExpRequired = 1700 },
    new CfgLevel { Level = 17, ExpRequired = 1800 },
    new CfgLevel { Level = 18, ExpRequired = 2000 },
};

// ── PRD §7.1 Lane paths (map_path.xlsx) ──────────────────────────────────────
// Map: 100×100 m, origin at centre. Blue base ≈ (-45,-45). Red base ≈ (+45,+45).
// Three lanes × 8 waypoints each (Blue→Red).
static CfgWaypoint WP(float x, float y) => new CfgWaypoint { X = (Fix64)x, Y = (Fix64)y };

var lanes = new CfgLane[]
{
    // Lane 0 — Top: hugs the top edge
    new CfgLane
    {
        Id = 0, WaypointCount = 8,
        Waypoints = new[]
        {
            WP(-40, -40), WP(-40, -10), WP(-40,  30),
            WP(-20,  45), WP( 10,  45), WP( 35,  30),
            WP( 40,  10), WP( 40,  40),
        }
    },
    // Lane 1 — Mid: diagonal
    new CfgLane
    {
        Id = 1, WaypointCount = 8,
        Waypoints = new[]
        {
            WP(-40, -40), WP(-30, -25), WP(-15, -15),
            WP(  0,   0), WP( 15,  15), WP( 25,  25),
            WP( 35,  35), WP( 40,  40),
        }
    },
    // Lane 2 — Bot: hugs the bottom edge
    new CfgLane
    {
        Id = 2, WaypointCount = 8,
        Waypoints = new[]
        {
            WP(-40, -40), WP(-10, -40), WP( 30, -40),
            WP( 45, -20), WP( 45,  10), WP( 30,  40),
            WP( 10,  40), WP( 40,  40),
        }
    },
};

// ── Bake ──────────────────────────────────────────────────────────────────────
byte[] bytes = ConfigBinary.Bake(heroes, levels, lanes);
File.WriteAllBytes(outFile, bytes);

Console.WriteLine($"[ConfigBaker] Wrote {bytes.Length} bytes → {outFile}");
Console.WriteLine($"  Heroes={heroes.Length}  SkillDefs={SkillEngine.DefCount}" +
                  $"  EffectSteps={SkillEngine.StepCount}  BuffDefs={BuffEngine.DefCount}" +
                  $"  Items={Items.DefCount}  Levels={levels.Length}  Lanes={lanes.Length}");
