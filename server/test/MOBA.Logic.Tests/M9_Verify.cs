// SPDX-License-Identifier: MIT
// M9 verification — Economy & Progression (PRD §7.3 / §7.4).
//
// M9.1  Passive gold drip: every 8 frames +1g to living heroes.
// M9.2  Tower kill gold: full killer-team +150g, dead members and enemies excluded.
// M9.3  XP → level-up → stat growth (Swordsman HP+80, AD+5 at level 2).
// M9.4  Shop purchase: Ok / NotInFountain / NoGold / InventoryFull / Dead.
// M9.5  Item CDR cap: ApplyItem clamps CDR at 40.

using System;
using MOBA.Logic.Sim;
using MOBA.Logic.Config;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M9_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M9 Verify");
        int rc = 0;
        rc |= M9_1_PassiveGold();
        rc |= M9_2_TowerKillGold();
        rc |= M9_3_XpLevelUp();
        rc |= M9_4_ShopBuy();
        rc |= M9_5_CdrCap();
        return rc;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M9.1 — Passive gold drip: every 8 frames +1g.
    // Frames 0..79 = 80 calls; triggers at 0,8,16,24,32,40,48,56,64,72 → +10g.
    // ─────────────────────────────────────────────────────────────────────────
    static int M9_1_PassiveGold()
    {
        const string name = "M9.1 PassiveGold";
        var heroes = new Hero[1];
        heroes[0].Alive = true;
        heroes[0].Gold  = 0;

        for (uint f = 0; f < 80; f++)
            ItemSystem.TickGold(heroes, f);

        if (heroes[0].Gold != 10)
        {
            Console.WriteLine($"  FAIL {name}: expected 10g after 80 frames, got {heroes[0].Gold}");
            return 1;
        }

        // Dead heroes must NOT receive drip.
        var h2 = new Hero[1];
        h2[0].Alive = false;
        h2[0].Gold  = 0;
        for (uint f = 0; f < 80; f++) ItemSystem.TickGold(h2, f);
        if (h2[0].Gold != 0)
        {
            Console.WriteLine($"  FAIL {name}: dead hero should gain 0g, got {h2[0].Gold}");
            return 1;
        }

        Console.WriteLine($"  PASS {name}");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M9.2 — Tower kill gold: killer team (all living) +150g each.
    //   Blue slots 0-4, Red slots 5-9.
    //   heroes[1] is dead. killerSlot=3 (blue).
    //   Expect: alive blue (0,2,3,4) +150; dead blue (1) unchanged; red (5-9) unchanged.
    // ─────────────────────────────────────────────────────────────────────────
    static int M9_2_TowerKillGold()
    {
        const string name = "M9.2 TowerKillGold";
        var heroes = new Hero[10];
        for (int i = 0; i < 10; i++) { heroes[i].Alive = true; heroes[i].Gold = 0; }
        heroes[1].Alive = false; // dead blue hero

        LevelSystem.AwardTowerKill(heroes, killerSlot: 3);

        int fail = 0;
        // Alive blue team members should get +150.
        foreach (int i in new[] { 0, 2, 3, 4 })
        {
            if (heroes[i].Gold != LevelSystem.TowerKillGold)
            {
                Console.WriteLine($"  FAIL {name}: heroes[{i}] gold={heroes[i].Gold}, want {LevelSystem.TowerKillGold}");
                fail = 1;
            }
        }
        // Dead blue hero must NOT receive gold.
        if (heroes[1].Gold != 0)
        {
            Console.WriteLine($"  FAIL {name}: dead hero[1] gold={heroes[1].Gold}, want 0");
            fail = 1;
        }
        // Enemy red team must NOT receive gold.
        for (int i = 5; i < 10; i++)
        {
            if (heroes[i].Gold != 0)
            {
                Console.WriteLine($"  FAIL {name}: enemy heroes[{i}] gold={heroes[i].Gold}, want 0");
                fail = 1;
            }
        }

        if (fail == 0) Console.WriteLine($"  PASS {name}");
        return fail;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M9.3 — XP → Level-up → stat growth (Swordsman, HeroDefId=0).
    //   Swordsman base: HP 600, AD 60, HP/lv +80, AD/lv +5.
    //   Level 2 needs cumulative 280 XP.
    //   After 279 XP: still level 1.
    //   After 1 more XP (total 280): level 2, HP=680, AD=65.
    //   After another 99 XP (total 379): still level 2 (needs 380 for level 3).
    // ─────────────────────────────────────────────────────────────────────────
    static int M9_3_XpLevelUp()
    {
        const string name = "M9.3 XpLevelUp";
        // ConfigManager must NOT be loaded for fallback path; ensure it is not.
        // (Unit tests run without config.bin by default.)
        var heroes = new Hero[1];
        ref var h = ref heroes[0];
        h.Alive   = true;
        h.Level   = 1;
        h.Xp      = 0;
        h.HeroDefId = 0;          // Swordsman
        h.MaxHp   = (Fix64)600;
        h.Hp      = (Fix64)600;
        h.Ad      = (Fix64)60;
        h.AttackSpeed = (Fix64)0.7f; // Swordsman initial AS (no growth)

        // Award 279 XP — should still be level 1.
        LevelSystem.AwardXp(heroes, 0, 279);
        if (h.Level != 1)
        {
            Console.WriteLine($"  FAIL {name}: expected level 1 after 279 XP, got {h.Level}");
            return 1;
        }

        // Award 1 more XP → total 280 → level up to 2.
        LevelSystem.AwardXp(heroes, 0, 1);
        if (h.Level != 2)
        {
            Console.WriteLine($"  FAIL {name}: expected level 2 after 280 XP, got {h.Level}");
            return 1;
        }
        if (h.MaxHp != (Fix64)680)
        {
            Console.WriteLine($"  FAIL {name}: expected MaxHp=680 at level 2, got {h.MaxHp}");
            return 1;
        }
        if (h.Ad != (Fix64)65)
        {
            Console.WriteLine($"  FAIL {name}: expected AD=65 at level 2, got {h.Ad}");
            return 1;
        }

        // Award 99 more XP → total 379 → still level 2 (needs 380 for level 3).
        LevelSystem.AwardXp(heroes, 0, 99);
        if (h.Level != 2)
        {
            Console.WriteLine($"  FAIL {name}: expected level 2 at 379 total XP, got {h.Level}");
            return 1;
        }

        Console.WriteLine($"  PASS {name}");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M9.4 — Shop purchase validation (PRD §7.4).
    //   Uses item index 0 (starter AD blade, cost=350, AddAd=8).
    //   Fountain centre = BlueTowers[1] = (-45, -45), radius = 6m.
    // ─────────────────────────────────────────────────────────────────────────
    static int M9_4_ShopBuy()
    {
        const string name = "M9.4 ShopBuy";
        Items.Reset();
        Items.RegisterDefaults();

        var heroes = new Hero[5];
        for (int i = 0; i < heroes.Length; i++) heroes[i].Alive = true;

        TSVector2 fountain = Lanes.BlueTowers[1]; // (-45, -45)

        // ── Slot 0: happy path — at fountain, enough gold ──
        heroes[0].Gold     = 500;
        heroes[0].Pos      = fountain;
        heroes[0].InvCount = 0;
        heroes[0].Ad       = (Fix64)0;

        var r0 = ItemSystem.TryBuy(heroes, 0, itemDefIdx: 0);
        if (r0 != ItemSystem.BuyResult.Ok)
        {
            Console.WriteLine($"  FAIL {name}: expected Ok, got {r0}");
            return 1;
        }
        if (heroes[0].Gold != 150) // 500 - 350
        {
            Console.WriteLine($"  FAIL {name}: expected gold=150 after purchase, got {heroes[0].Gold}");
            return 1;
        }
        if (heroes[0].InvCount != 1)
        {
            Console.WriteLine($"  FAIL {name}: expected InvCount=1, got {heroes[0].InvCount}");
            return 1;
        }
        // Item AD bonus applied.
        if (heroes[0].Ad != (Fix64)8)
        {
            Console.WriteLine($"  FAIL {name}: expected Ad=8 after buy, got {heroes[0].Ad}");
            return 1;
        }

        // ── Slot 1: away from fountain ──
        heroes[1].Gold = 500;
        heroes[1].Pos  = new TSVector2((Fix64)50, (Fix64)50); // far away
        var r1 = ItemSystem.TryBuy(heroes, 1, 0);
        if (r1 != ItemSystem.BuyResult.NotInFountain)
        {
            Console.WriteLine($"  FAIL {name}: expected NotInFountain, got {r1}");
            return 1;
        }

        // ── Slot 2: not enough gold ──
        heroes[2].Gold = 100; // item costs 350
        heroes[2].Pos  = fountain;
        var r2 = ItemSystem.TryBuy(heroes, 2, 0);
        if (r2 != ItemSystem.BuyResult.NoGold)
        {
            Console.WriteLine($"  FAIL {name}: expected NoGold, got {r2}");
            return 1;
        }

        // ── Slot 3: inventory full ──
        heroes[3].Gold     = 5000;
        heroes[3].Pos      = fountain;
        heroes[3].InvCount = Items.InventorySize; // 6
        var r3 = ItemSystem.TryBuy(heroes, 3, 0);
        if (r3 != ItemSystem.BuyResult.InventoryFull)
        {
            Console.WriteLine($"  FAIL {name}: expected InventoryFull, got {r3}");
            return 1;
        }

        // ── Slot 4: hero is dead ──
        heroes[4].Gold  = 500;
        heroes[4].Pos   = fountain;
        heroes[4].Alive = false;
        var r4 = ItemSystem.TryBuy(heroes, 4, 0);
        if (r4 != ItemSystem.BuyResult.Dead)
        {
            Console.WriteLine($"  FAIL {name}: expected Dead, got {r4}");
            return 1;
        }

        Console.WriteLine($"  PASS {name}");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M9.5 — Item CDR cap: ApplyItem must clamp CDR at 40 (PRD §7.2).
    //   Start CDR=35, add item with +10 CDR → result must be 40 (not 45).
    // ─────────────────────────────────────────────────────────────────────────
    static int M9_5_CdrCap()
    {
        const string name = "M9.5 CdrCap";
        var hero = new Hero();
        hero.Cdr = (Fix64)35;

        var item = new ItemDef { AddCdr = 10 };
        ItemSystem.ApplyItem(ref hero, in item);

        if (hero.Cdr != (Fix64)40)
        {
            Console.WriteLine($"  FAIL {name}: expected CDR=40 (capped), got {hero.Cdr}");
            return 1;
        }

        // Verify that a later +5 CDR item still keeps it at 40.
        var item2 = new ItemDef { AddCdr = 5 };
        ItemSystem.ApplyItem(ref hero, in item2);
        if (hero.Cdr != (Fix64)40)
        {
            Console.WriteLine($"  FAIL {name}: expected CDR=40 after second item, got {hero.Cdr}");
            return 1;
        }

        Console.WriteLine($"  PASS {name}");
        return 0;
    }
}
