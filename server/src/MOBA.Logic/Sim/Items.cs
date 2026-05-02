// SPDX-License-Identifier: MIT
// M5.3 — items, shop, BuyItem.
// Per PRD §7.4: 10 items (3 starter / 3 big weapon / 2 spell / 2 defense).
// Shop is fountain-only. BuyItem flows: client → server (0x40) → next FrameBatch
// embeds itemId+1 into InputFrame.BuyItemId for that slot → World.Tick applies
// it deterministically via ItemSystem.TryBuy.

using System;

namespace MOBA.Logic.Sim;

public enum ItemSlot : byte { Starter = 0, BigWeapon = 1, Spell = 2, Defense = 3 }

/// <summary>Item definition. Modifiers are flat additions to the matching Hero stat.</summary>
public struct ItemDef
{
    public ushort Id;
    public ItemSlot Slot;
    public uint Cost;          // gold
    public Fix64 AddAd;
    public Fix64 AddAp;
    public Fix64 AddArmor;
    public Fix64 AddMr;
    public Fix64 AddMaxHp;
    public Fix64 AddMaxMp;
    public Fix64 AddMoveSpeed;
    public byte AddCdr;        // flat percent
}

public static class Items
{
    public const int MaxItems = 16;
    public const int InventorySize = 6;
    public const uint StartingGold = 500;
    public const uint PassiveGoldPerSecond = 2; // PRD-ish; per 15 frames
    public const uint MinionKillGold = 25;
    public const uint TowerKillGold = 200;
    public const uint HeroKillGold = 300;

    public static readonly ItemDef[] Defs = new ItemDef[MaxItems];
    public static int DefCount;

    public static bool Registered { get; private set; }

    public static ushort Register(ItemDef def)
    {
        if (DefCount >= MaxItems) throw new InvalidOperationException("Items.Defs overflow");
        Defs[DefCount] = def;
        return (ushort)DefCount++;
    }

    /// <summary>Idempotent registration of the 10 baseline items.</summary>
    public static void Reset()
    {
        DefCount = 0;
        Array.Clear(Defs, 0, Defs.Length);
        Registered = false;
    }

    public static void RegisterDefaults()
    {
        if (Registered) return;
        // ---- Starter (3) -----------------------------------------------------------
        Register(new ItemDef { Id = 1,  Slot = ItemSlot.Starter, Cost = 350,  AddAd = (Fix64)8 });
        Register(new ItemDef { Id = 2,  Slot = ItemSlot.Starter, Cost = 350,  AddAp = (Fix64)15 });
        Register(new ItemDef { Id = 3,  Slot = ItemSlot.Starter, Cost = 400,  AddArmor = (Fix64)15, AddMaxHp = (Fix64)100 });
        // ---- Big weapons (3) -------------------------------------------------------
        Register(new ItemDef { Id = 4,  Slot = ItemSlot.BigWeapon, Cost = 1300, AddAd = (Fix64)35, AddCdr = 5 });
        Register(new ItemDef { Id = 5,  Slot = ItemSlot.BigWeapon, Cost = 1600, AddAd = (Fix64)45, AddMaxHp = (Fix64)150 });
        Register(new ItemDef { Id = 6,  Slot = ItemSlot.BigWeapon, Cost = 1800, AddAd = (Fix64)60 });
        // ---- Spells (2) ------------------------------------------------------------
        Register(new ItemDef { Id = 7,  Slot = ItemSlot.Spell,    Cost = 1500, AddAp = (Fix64)70, AddCdr = 10 });
        Register(new ItemDef { Id = 8,  Slot = ItemSlot.Spell,    Cost = 2000, AddAp = (Fix64)100 });
        // ---- Defense (2) -----------------------------------------------------------
        Register(new ItemDef { Id = 9,  Slot = ItemSlot.Defense,  Cost = 1100, AddArmor = (Fix64)50, AddMaxHp = (Fix64)250 });
        Register(new ItemDef { Id = 10, Slot = ItemSlot.Defense,  Cost = 1100, AddMr = (Fix64)45, AddMaxHp = (Fix64)250, AddMoveSpeed = (Fix64)0.3f });
        Registered = true;
    }
}

public static class ItemSystem
{
    /// <summary>Fountain radius squared per team. Centre = nearest base tower (Lanes.BlueTowers[1] or RedTowers[1]).</summary>
    public static readonly Fix64 FountainRadius = (Fix64)6;

    /// <summary>Result codes for analytics.</summary>
    public enum BuyResult : byte { Ok = 0, Unknown = 1, NotInFountain = 2, NoGold = 3, InventoryFull = 4, Dead = 5 }

    /// <summary>Fountain check: hero must be within FountainRadius of an own-team base tower.</summary>
    [NoGC]
    public static bool InFountain(in Hero h, int slot)
    {
        Team team = slot < 5 ? Team.Blue : Team.Red;
        TSVector2 centre = team == Team.Blue ? Lanes.BlueTowers[1] : Lanes.RedTowers[1];
        Fix64 dx = h.Pos.X - centre.X, dy = h.Pos.Y - centre.Y;
        Fix64 r2 = FountainRadius * FountainRadius;
        return dx * dx + dy * dy <= r2;
    }

    /// <summary>Apply the deltas of one item to a hero's combat stats.</summary>
    [NoGC]
    public static void ApplyItem(ref Hero h, in ItemDef d)
    {
        h.Ad += d.AddAd;
        h.Ap += d.AddAp;
        h.Armor += d.AddArmor;
        h.MagicResist += d.AddMr;
        h.MaxHp += d.AddMaxHp; h.Hp += d.AddMaxHp;
        h.MaxMp += d.AddMaxMp; h.Mp += d.AddMaxMp;
        h.MoveSpeed += d.AddMoveSpeed;
        h.Cdr += (Fix64)d.AddCdr;
        if (h.Cdr > (Fix64)40) h.Cdr = (Fix64)40;
    }

    /// <summary>Try to buy item <paramref name="itemDefIdx"/> for hero in slot <paramref name="slot"/>.
    /// Validates fountain, gold, inventory space. Returns result code.</summary>
    [NoGC]
    public static BuyResult TryBuy(Hero[] heroes, int slot, ushort itemDefIdx)
    {
        ref var h = ref heroes[slot];
        if (!h.Alive) return BuyResult.Dead;
        if (itemDefIdx >= Items.DefCount) return BuyResult.Unknown;
        ref var def = ref Items.Defs[itemDefIdx];
        if (!InFountain(in h, slot)) return BuyResult.NotInFountain;
        if (h.Gold < def.Cost) return BuyResult.NoGold;
        if (h.InvCount >= Items.InventorySize) return BuyResult.InventoryFull;
        h.Gold -= def.Cost;
        // Place into first free inline slot.
        byte stored = (byte)(itemDefIdx + 1);
        if      (h.Inv0 == 0) h.Inv0 = stored;
        else if (h.Inv1 == 0) h.Inv1 = stored;
        else if (h.Inv2 == 0) h.Inv2 = stored;
        else if (h.Inv3 == 0) h.Inv3 = stored;
        else if (h.Inv4 == 0) h.Inv4 = stored;
        else if (h.Inv5 == 0) h.Inv5 = stored;
        h.InvCount++;
        ApplyItem(ref h, in def);
        return BuyResult.Ok;
    }

    /// <summary>Per-tick passive gold drip (1 gold every 0.5s = every 8 frames @15Hz, approx).</summary>
    [NoGC]
    public static void TickGold(Hero[] heroes, uint frame)
    {
        // Every 8 frames, +1 gold to each living hero (PRD has flat 2g/s; 15Hz / 2 = 7.5 ≈ 8).
        if ((frame & 7) != 0) return;
        for (int i = 0; i < heroes.Length; i++)
            if (heroes[i].Alive) heroes[i].Gold += 1;
    }

    /// <summary>Award gold for a confirmed kill.</summary>
    [NoGC]
    public static void AwardKillGold(Hero[] heroes, int killerSlot, UnitKind victimKind)
    {
        if ((uint)killerSlot >= (uint)heroes.Length) return;
        uint amount = victimKind switch
        {
            UnitKind.Minion => Items.MinionKillGold,
            UnitKind.Tower  => Items.TowerKillGold,
            UnitKind.Hero   => Items.HeroKillGold,
            _ => 0u,
        };
        heroes[killerSlot].Gold += amount;
    }
}
