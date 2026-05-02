// SPDX-License-Identifier: MIT
// M6 verification — PRD §4.3 siege minions, §7.1 jungle camps, §7.3 kill-streak bonus.
//
// Asserts:
//   M6.1  SpawnWave with waveIdx=30 produces at least one MinionType.Siege minion.
//   M6.2  Jungle monsters appear at frame 900; after being killed they respawn
//         1350 frames later (RespawnFrames=1350).
//   M6.3  Kill-streak gold: 1st kill=+300g, 2nd kill=+350g, 3rd kill=+400g;
//         death resets streak to 0.
//   M6.4  Zero-alloc: 1000-tick tight loop with jungle active allocates 0 bytes.

using System;
using System.Runtime.CompilerServices;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class M6_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M6 Verify");
        int rc = 0;
        rc |= M6_1_SiegeSpawn();
        rc |= M6_2_JungleSpawnRespawn();
        rc |= M6_3_KillStreakGold();
        rc |= M6_4_ZeroAlloc();
        return rc;
    }

    // ── M6.1 ─────────────────────────────────────────────────────────────────────
    static int M6_1_SiegeSpawn()
    {
        const string name = "M6.1 SiegeSpawn";
        try
        {
            // Construct world so BuiltinContent is registered and hero stats are set.
            var world = new DeterministicWorld(0);
            var minions = new Minion[GameSystems.MaxMinions];
            int count = GameSystems.SpawnWave(minions, 0, frame: 450 * 30, waveIdx: 30);
            int siegeCount = 0;
            for (int i = 0; i < count; i++)
                if (minions[i].Alive && minions[i].Type == MinionType.Siege) siegeCount++;
            if (siegeCount == 0)
            {
                Console.WriteLine($"  FAIL {name}: no siege minions spawned (count={count})");
                return 1;
            }
            // Expect one siege per lane per side = 3 lanes × 2 sides = 6
            if (siegeCount != 6)
            {
                Console.WriteLine($"  FAIL {name}: expected 6 siege minions, got {siegeCount}");
                return 1;
            }
            Console.WriteLine($"  PASS {name}: {siegeCount} siege minions at wave 30");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    // ── M6.2 ─────────────────────────────────────────────────────────────────────
    static int M6_2_JungleSpawnRespawn()
    {
        const string name = "M6.2 JungleSpawnRespawn";
        try
        {
            var monsters = new JungleMonster[JungleSystem.MaxMonsters];
            JungleSystem.Init(monsters);

            // Before InitialSpawnFrame: all dead.
            var heroes  = new Hero[10];
            var dmq     = new GameSystems.DamageEvent[64];
            int dmCount = 0;

            JungleSystem.Tick(monsters, heroes, frame: JungleSystem.InitialSpawnFrame - 1, dmq, ref dmCount);
            for (int i = 0; i < monsters.Length; i++)
            {
                if (monsters[i].Alive)
                {
                    Console.WriteLine($"  FAIL {name}: monster {i} alive before InitialSpawnFrame");
                    return 1;
                }
            }

            // At InitialSpawnFrame: all alive.
            JungleSystem.Tick(monsters, heroes, frame: JungleSystem.InitialSpawnFrame, dmq, ref dmCount);
            for (int i = 0; i < monsters.Length; i++)
            {
                if (!monsters[i].Alive)
                {
                    Console.WriteLine($"  FAIL {name}: monster {i} not alive at InitialSpawnFrame");
                    return 1;
                }
            }

            // Kill monster 0 manually and set its RespawnFrame.
            monsters[0].Alive = false;
            monsters[0].RespawnFrame = JungleSystem.InitialSpawnFrame + JungleSystem.RespawnFrames;

            // One frame before respawn: still dead.
            JungleSystem.Tick(monsters, heroes, frame: monsters[0].RespawnFrame - 1, dmq, ref dmCount);
            if (monsters[0].Alive)
            {
                Console.WriteLine($"  FAIL {name}: monster 0 respawned one frame too early");
                return 1;
            }

            // At respawn frame: alive again.
            JungleSystem.Tick(monsters, heroes, frame: monsters[0].RespawnFrame, dmq, ref dmCount);
            if (!monsters[0].Alive)
            {
                Console.WriteLine($"  FAIL {name}: monster 0 did not respawn at RespawnFrame");
                return 1;
            }

            Console.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    // ── M6.3 ─────────────────────────────────────────────────────────────────────
    static int M6_3_KillStreakGold()
    {
        const string name = "M6.3 KillStreakGold";
        try
        {
            var world = new DeterministicWorld(0);
            // killer = slot 0 (blue), victim = slot 5 (red team, using hero slot 5).
            // We'll drive damage directly through ResolveDamage so we don't depend on
            // hero physics positions.
            Hero[] heroes = new Hero[10];
            for (int i = 0; i < heroes.Length; i++)
            {
                heroes[i].Alive = true;
                heroes[i].Level = 1;
                heroes[i].Hp = (Fix64)100;
                heroes[i].MaxHp = (Fix64)100;
                heroes[i].Armor = Fix64.Zero;
                heroes[i].MagicResist = Fix64.Zero;
                heroes[i].Gold = 500u;
            }

            var minions = new Minion[0];
            var towers  = new Tower[0];
            var dmq     = new GameSystems.DamageEvent[32];

            // ── kill 1 ──
            // Deliver lethal damage from slot 0 to slot 5.
            int dmCount = 0;
            GameSystems.EnqueueDamage(dmq, ref dmCount,
                new UnitRef { Kind = UnitKind.Hero, Index = 5 },
                (Fix64)200, frame: 1, DamageType.True, sourceSlot: 0);
            GameSystems.ResolveDamage(dmq, dmCount, minions, towers, heroes, 1, out _, jungle: null);
            uint goldAfterK1 = heroes[0].Gold;
            uint expectedK1 = 500 + Items.HeroKillGold; // streak=1, bonus=0
            if (goldAfterK1 != expectedK1)
            {
                Console.WriteLine($"  FAIL {name}: after kill 1 gold={goldAfterK1}, expected {expectedK1}");
                return 1;
            }

            // ── kill 2 ──
            heroes[5].Alive = true; heroes[5].Hp = (Fix64)100;
            dmCount = 0;
            GameSystems.EnqueueDamage(dmq, ref dmCount,
                new UnitRef { Kind = UnitKind.Hero, Index = 5 },
                (Fix64)200, frame: 2, DamageType.True, sourceSlot: 0);
            GameSystems.ResolveDamage(dmq, dmCount, minions, towers, heroes, 2, out _, jungle: null);
            uint goldAfterK2 = heroes[0].Gold;
            uint expectedK2 = expectedK1 + Items.HeroKillGold + 50; // streak=2, bonus=50
            if (goldAfterK2 != expectedK2)
            {
                Console.WriteLine($"  FAIL {name}: after kill 2 gold={goldAfterK2}, expected {expectedK2}");
                return 1;
            }

            // ── kill 3 ──
            heroes[5].Alive = true; heroes[5].Hp = (Fix64)100;
            dmCount = 0;
            GameSystems.EnqueueDamage(dmq, ref dmCount,
                new UnitRef { Kind = UnitKind.Hero, Index = 5 },
                (Fix64)200, frame: 3, DamageType.True, sourceSlot: 0);
            GameSystems.ResolveDamage(dmq, dmCount, minions, towers, heroes, 3, out _, jungle: null);
            uint goldAfterK3 = heroes[0].Gold;
            uint expectedK3 = expectedK2 + Items.HeroKillGold + 100; // streak=3, bonus=100
            if (goldAfterK3 != expectedK3)
            {
                Console.WriteLine($"  FAIL {name}: after kill 3 gold={goldAfterK3}, expected {expectedK3}");
                return 1;
            }

            // ── killer dies → streak reset ──
            heroes[0].Hp = (Fix64)100;
            dmCount = 0;
            GameSystems.EnqueueDamage(dmq, ref dmCount,
                new UnitRef { Kind = UnitKind.Hero, Index = 0 },
                (Fix64)200, frame: 4, DamageType.True, sourceSlot: 5);
            GameSystems.ResolveDamage(dmq, dmCount, minions, towers, heroes, 4, out _, jungle: null);
            if (heroes[0].KillStreak != 0)
            {
                Console.WriteLine($"  FAIL {name}: streak not reset after death (streak={heroes[0].KillStreak})");
                return 1;
            }

            Console.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    // ── M6.4 ─────────────────────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int M6_4_ZeroAlloc()
    {
        const string name = "M6.4 ZeroAlloc";
        try
        {
            // Build world with gameplay + jungle active.
            LevelSystem.WarmUp();
            var world = new DeterministicWorld(42);
            world.EnableGameplay = true;
            for (int i = 0; i < 10; i++) world.Heroes[i].Alive = true;

            // Warm-up ticks to JIT all paths (jungle spawns at frame 900).
            var warmInput = new InputFrame[DeterministicWorld.PlayerCount];
            for (int t = 0; t < 1000; t++) world.Tick(warmInput);

            // Measurement: 1000 ticks, expect 0 bytes allocated.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 1000; t++) world.Tick(warmInput);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            if (delta != 0)
            {
                Console.WriteLine($"  FAIL {name}: allocated {delta} bytes in tight loop");
                return 1;
            }
            Console.WriteLine($"  PASS {name}: Δ={delta} bytes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
