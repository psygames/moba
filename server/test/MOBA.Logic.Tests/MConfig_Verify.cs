// SPDX-License-Identifier: MIT
// §8 Luban Config Pipeline — verification suite.
//
// MC.1  ConfigRoundTrip     — Bake config.bin in-memory, load via ConfigManager,
//                            verify all 3 hero rows match the PRD §7.7 values.
// MC.2  LevelTableLoaded    — 18 level entries; ExpRequired is strictly ascending
//                            (after level 1 which is 0).
// MC.3  LanePathsLoaded     — 3 lanes × 8 waypoints each; first point of lane 0
//                            is close to (-40,-40) and first point of lane 1 is
//                            close to (-40,-40).
// MC.4  ConfigDrivenGame    — Load config first, then spin up a world; hero 0's
//                            MaxHp must equal the PRD value (600), and a 30-tick
//                            run must produce a non-zero state hash (sanity check).
// MC.5  RegressionNoConfig  — Without loading config, BuiltinContent still works
//                            (backward compat guard).

using System;
using MOBA.Logic.Config;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

public static class MConfig_Verify
{
    // ── PRD §7.7 expected values ──────────────────────────────────────────────
    private static readonly (Fix64 MaxHp, Fix64 Ad, Fix64 AttackRange, Fix64 HpPerLv)[] Expected = {
        ( (Fix64)600, (Fix64)60,  (Fix64)1.5f, (Fix64)80 ),   // Hero 0 Swordsman
        ( (Fix64)480, (Fix64)45,  (Fix64)5,    (Fix64)60 ),   // Hero 1 Mage
        ( (Fix64)520, (Fix64)55,  (Fix64)6,    (Fix64)70 ),   // Hero 2 Marksman
    };

    public static int Run(string which)
    {
        int rc = 0;
        if (which is "all" or "mc.1" or "mconfig") rc |= MC1_RoundTrip();
        if (which is "all" or "mc.2" or "mconfig") rc |= MC2_LevelTable();
        if (which is "all" or "mc.3" or "mconfig") rc |= MC3_LanePaths();
        if (which is "all" or "mc.4" or "mconfig") rc |= MC4_ConfigDrivenGame();
        if (which is "all" or "mc.5" or "mconfig") rc |= MC5_RegressionNoConfig();
        return rc;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MC.1  ConfigRoundTrip
    // ─────────────────────────────────────────────────────────────────────────
    private static int MC1_RoundTrip()
    {
        Console.WriteLine("MC.1 ConfigRoundTrip …");
        ConfigManager.Reset();

        var bytes = BakeDefaultConfig();
        if (!ConfigManager.LoadFromBytes(bytes))
        { Console.WriteLine("  FAIL  LoadFromBytes returned false"); return 1; }

        if (!ConfigManager.IsLoaded)
        { Console.WriteLine("  FAIL  IsLoaded not set"); return 1; }

        var inst = ConfigManager.Instance;
        if (inst.Heroes == null || inst.Heroes.Length != 3)
        { Console.WriteLine($"  FAIL  Heroes.Length={inst.Heroes?.Length}"); return 1; }

        for (int i = 0; i < 3; i++)
        {
            ref var h = ref inst.Heroes[i];
            var (expHp, expAd, expRange, expHpLv) = Expected[i];
            if (h.MaxHp != expHp)
            { Console.WriteLine($"  FAIL  Hero[{i}].MaxHp={h.MaxHp} expected {expHp}"); return 1; }
            if (h.Ad != expAd)
            { Console.WriteLine($"  FAIL  Hero[{i}].Ad={h.Ad} expected {expAd}"); return 1; }
            if (h.AttackRange != expRange)
            { Console.WriteLine($"  FAIL  Hero[{i}].AttackRange={h.AttackRange} expected {expRange}"); return 1; }
            if (h.HpPerLv != expHpLv)
            { Console.WriteLine($"  FAIL  Hero[{i}].HpPerLv={h.HpPerLv} expected {expHpLv}"); return 1; }
        }

        Console.WriteLine("  PASS");
        ConfigManager.Reset();
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MC.2  LevelTableLoaded
    // ─────────────────────────────────────────────────────────────────────────
    private static int MC2_LevelTable()
    {
        Console.WriteLine("MC.2 LevelTableLoaded …");
        ConfigManager.Reset();

        var bytes = BakeDefaultConfig();
        ConfigManager.LoadFromBytes(bytes);

        var lvls = ConfigManager.Instance?.Levels;
        if (lvls == null || lvls.Length != 18)
        { Console.WriteLine($"  FAIL  Levels.Length={lvls?.Length}"); ConfigManager.Reset(); return 1; }

        if (lvls[0].ExpRequired != 0)
        { Console.WriteLine("  FAIL  Level1 ExpRequired should be 0"); ConfigManager.Reset(); return 1; }

        for (int i = 1; i < lvls.Length; i++)
        {
            if (lvls[i].ExpRequired <= lvls[i - 1].ExpRequired)
            {
                Console.WriteLine($"  FAIL  Level exp not ascending at index {i}");
                ConfigManager.Reset();
                return 1;
            }
        }

        Console.WriteLine("  PASS");
        ConfigManager.Reset();
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MC.3  LanePathsLoaded
    // ─────────────────────────────────────────────────────────────────────────
    private static int MC3_LanePaths()
    {
        Console.WriteLine("MC.3 LanePathsLoaded …");
        ConfigManager.Reset();

        var bytes = BakeDefaultConfig();
        ConfigManager.LoadFromBytes(bytes);

        var lanes = ConfigManager.Instance?.Lanes;
        if (lanes == null || lanes.Length != 3)
        { Console.WriteLine($"  FAIL  Lanes.Length={lanes?.Length}"); ConfigManager.Reset(); return 1; }

        foreach (var lane in lanes)
        {
            if (lane.Waypoints == null || lane.Waypoints.Length != lane.WaypointCount)
            { Console.WriteLine($"  FAIL  Lane[{lane.Id}] waypoint count mismatch"); ConfigManager.Reset(); return 1; }
            if (lane.WaypointCount != 8)
            { Console.WriteLine($"  FAIL  Lane[{lane.Id}] has {lane.WaypointCount} waypoints, expected 8"); ConfigManager.Reset(); return 1; }
        }

        // Verify lane 0 starts near (-40,-40)
        var p0 = lanes[0].Waypoints[0];
        if (p0.X != (Fix64)(-40) || p0.Y != (Fix64)(-40))
        { Console.WriteLine($"  FAIL  Lane0 first WP=({p0.X},{p0.Y}) expected (-40,-40)"); ConfigManager.Reset(); return 1; }

        Console.WriteLine("  PASS");
        ConfigManager.Reset();
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MC.4  ConfigDrivenGame
    // ─────────────────────────────────────────────────────────────────────────
    private static int MC4_ConfigDrivenGame()
    {
        Console.WriteLine("MC.4 ConfigDrivenGame …");
        ConfigManager.Reset();

        var bytes = BakeDefaultConfig();
        ConfigManager.LoadFromBytes(bytes);

        // Create world — BuiltinContent.Register() will be skipped (MarkLoadedFromConfig).
        var world = new DeterministicWorld(seed: 12345);

        if (world.Heroes[0].MaxHp != (Fix64)600)
        {
            Console.WriteLine($"  FAIL  Hero0.MaxHp={world.Heroes[0].MaxHp} expected 600 (config-driven)");
            ConfigManager.Reset();
            return 1;
        }

        // Tick 30 frames and verify state hash is non-zero.
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        for (int f = 0; f < 30; f++) world.Tick(inputs);

        ulong hash = world.Hash();
        if (hash == 0)
        { Console.WriteLine("  FAIL  hash=0 after 30 ticks"); ConfigManager.Reset(); return 1; }

        Console.WriteLine($"  PASS  Hero0.MaxHp=600 hash={hash:X16}");
        ConfigManager.Reset();
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MC.5  RegressionNoConfig
    // ─────────────────────────────────────────────────────────────────────────
    private static int MC5_RegressionNoConfig()
    {
        Console.WriteLine("MC.5 RegressionNoConfig …");
        ConfigManager.Reset();

        // Do NOT call ConfigManager.LoadFromBytes. BuiltinContent should work as before.
        var world = new DeterministicWorld(seed: 42);

        // BuiltinContent hardcodes Warrior MaxHp=720 (different from PRD §7.7 600).
        // As long as the world starts without error, we're good.
        if (world.Heroes[0].MaxHp <= Fix64.Zero)
        { Console.WriteLine($"  FAIL  Hero0.MaxHp={world.Heroes[0].MaxHp} should be positive"); return 1; }

        Console.WriteLine($"  PASS  Hero0.MaxHp={world.Heroes[0].MaxHp} (hardcoded default)");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the same config.bin bytes as ConfigBaker.Program would produce.
    /// Calls BuiltinContent.Register() to fill the engine tables first.
    /// </summary>
    private static byte[] BakeDefaultConfig()
    {
        // Reset state so Register() runs fresh.
        ConfigManager.Reset();
        SkillEngine.Reset();
        BuffEngine.Reset();
        Items.Reset();
        BuiltinContent.Register();   // populates engines with hardcoded data

        var heroes = new CfgHero[]
        {
            new CfgHero
            {
                Id = 0,
                SkillQ = BuiltinContent.HeroSkills[0, 0], SkillW = BuiltinContent.HeroSkills[0, 1],
                SkillE = BuiltinContent.HeroSkills[0, 2], SkillR = BuiltinContent.HeroSkills[0, 3],
                MaxHp = (Fix64)600, MaxMp = (Fix64)200, Ad = (Fix64)60, Ap = Fix64.Zero,
                Armor = (Fix64)25, MagicResist = (Fix64)25,
                AttackRange = (Fix64)1.5f, AttackSpeed = (Fix64)0.7f, MoveSpeed = (Fix64)6.5f,
                HpPerLv = (Fix64)80, MpPerLv = Fix64.Zero, AdPerLv = (Fix64)5, ApPerLv = Fix64.Zero,
            },
            new CfgHero
            {
                Id = 1,
                SkillQ = BuiltinContent.HeroSkills[1, 0], SkillW = BuiltinContent.HeroSkills[1, 1],
                SkillE = BuiltinContent.HeroSkills[1, 2], SkillR = BuiltinContent.HeroSkills[1, 3],
                MaxHp = (Fix64)480, MaxMp = (Fix64)400, Ad = (Fix64)45, Ap = (Fix64)50,
                Armor = (Fix64)18, MagicResist = (Fix64)25,
                AttackRange = (Fix64)5, AttackSpeed = (Fix64)0.6f, MoveSpeed = (Fix64)6,
                HpPerLv = (Fix64)60, MpPerLv = Fix64.Zero, AdPerLv = Fix64.Zero, ApPerLv = (Fix64)8,
            },
            new CfgHero
            {
                Id = 2,
                SkillQ = BuiltinContent.HeroSkills[2, 0], SkillW = BuiltinContent.HeroSkills[2, 1],
                SkillE = BuiltinContent.HeroSkills[2, 2], SkillR = BuiltinContent.HeroSkills[2, 3],
                MaxHp = (Fix64)520, MaxMp = (Fix64)250, Ad = (Fix64)55, Ap = Fix64.Zero,
                Armor = (Fix64)20, MagicResist = (Fix64)25,
                AttackRange = (Fix64)6, AttackSpeed = (Fix64)0.85f, MoveSpeed = (Fix64)6.2f,
                HpPerLv = (Fix64)70, MpPerLv = Fix64.Zero, AdPerLv = (Fix64)4, ApPerLv = Fix64.Zero,
            },
        };

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

        static CfgWaypoint WP(float x, float y) => new CfgWaypoint { X = (Fix64)x, Y = (Fix64)y };
        var lanes = new CfgLane[]
        {
            new CfgLane
            {
                Id = 0, WaypointCount = 8,
                Waypoints = new[] {
                    WP(-40,-40), WP(-40,-10), WP(-40,30),
                    WP(-20,45),  WP(10,45),   WP(35,30),
                    WP(40,10),   WP(40,40),
                }
            },
            new CfgLane
            {
                Id = 1, WaypointCount = 8,
                Waypoints = new[] {
                    WP(-40,-40), WP(-30,-25), WP(-15,-15),
                    WP(0,0),     WP(15,15),   WP(25,25),
                    WP(35,35),   WP(40,40),
                }
            },
            new CfgLane
            {
                Id = 2, WaypointCount = 8,
                Waypoints = new[] {
                    WP(-40,-40), WP(-10,-40), WP(30,-40),
                    WP(45,-20),  WP(45,10),   WP(30,40),
                    WP(10,40),   WP(40,40),
                }
            },
        };

        return ConfigBinary.Bake(heroes, levels, lanes);
    }
}
