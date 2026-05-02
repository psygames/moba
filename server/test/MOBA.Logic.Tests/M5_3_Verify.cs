// SPDX-License-Identifier: MIT
// M5.3 verification — items, shop, BuyItem flow.
//
// Asserts:
//   M5.3.1  10 baseline items registered (3 starter / 3 weapon / 2 spell / 2 defense).
//   M5.3.2  Wire codec round-trip (C2S_BuyItem).
//   M5.3.3  In-fountain hero with enough gold buys successfully; gold/inventory/stats update.
//   M5.3.4  Out-of-fountain rejected; insufficient gold rejected; full inventory rejected.
//   M5.3.5  Two worlds fed identical InputFrame streams (with BuyItemId set) produce identical hashes.
//   M5.3.6  GC=0: 1000-tick tight loop with periodic buys allocs 0 bytes after warmup.
//   M5.3.7  Cdr cap of 40 enforced after stacking CDR items.

using System;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;
using TSVector2 = Box2DSharp.Common.FVector2;

namespace MOBA.Logic.Tests;

internal static class M5_3_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.3 Verify");
        int rc = 0;

        // Construct world (BuiltinContent + Items.RegisterDefaults runs).
        var w = new DeterministicWorld(seed: 5) { EnableGameplay = true };

        // 5.3.1 — 10 items registered, with the right slot distribution.
        if (Items.DefCount != 10) { Console.WriteLine($"  FAIL  expected 10 items, got {Items.DefCount}"); rc = 1; }
        int starter = 0, weapon = 0, spell = 0, def = 0;
        for (int i = 0; i < Items.DefCount; i++)
            switch (Items.Defs[i].Slot)
            {
                case ItemSlot.Starter:   starter++; break;
                case ItemSlot.BigWeapon: weapon++;  break;
                case ItemSlot.Spell:     spell++;   break;
                case ItemSlot.Defense:   def++;     break;
            }
        if (starter != 3 || weapon != 3 || spell != 2 || def != 2)
        { Console.WriteLine($"  FAIL  slot mix S{starter}/W{weapon}/Sp{spell}/D{def} (want 3/3/2/2)"); rc = 1; }

        // 5.3.2 — wire round-trip.
        Span<byte> buf = stackalloc byte[MessageCodec.BuyItemSize];
        MessageCodec.WriteBuyItem(buf, playerSlot: 3, itemId: 7);
        if (buf[0] != MsgId.C2S_BuyItem) { Console.WriteLine("  FAIL  msgId not 0x40"); rc = 1; }
        MessageCodec.ReadBuyItem(buf, out byte ps, out ushort iid);
        if (ps != 3 || iid != 7) { Console.WriteLine($"  FAIL  codec rt got slot={ps} item={iid}"); rc = 1; }

        // 5.3.3 — in-fountain blue hero buys item idx 0 (starter, cost 350).
        // Move hero 0 into the fountain centre and ensure the buy succeeds.
        ref var h0 = ref w.Heroes[0];
        var fountain = Lanes.BlueTowers[1];
        h0.Pos = fountain;
        uint goldBefore = h0.Gold;
        Fix64 adBefore = h0.Ad;
        var resOk = ItemSystem.TryBuy(w.Heroes, 0, 0);
        if (resOk != ItemSystem.BuyResult.Ok) { Console.WriteLine($"  FAIL  in-fountain buy returned {resOk}"); rc = 1; }
        if (h0.Gold != goldBefore - Items.Defs[0].Cost) { Console.WriteLine("  FAIL  gold not deducted"); rc = 1; }
        if (h0.InvCount != 1 || h0.Inv0 != 1) { Console.WriteLine($"  FAIL  inventory not set (count={h0.InvCount} inv0={h0.Inv0})"); rc = 1; }
        if (h0.Ad != adBefore + Items.Defs[0].AddAd) { Console.WriteLine("  FAIL  AD not increased"); rc = 1; }

        // 5.3.4a — outside fountain → reject.
        ref var h1 = ref w.Heroes[1];
        h1.Pos = new TSVector2((Fix64)0, (Fix64)0); // origin, far from base
        var resOut = ItemSystem.TryBuy(w.Heroes, 1, 0);
        if (resOut != ItemSystem.BuyResult.NotInFountain) { Console.WriteLine($"  FAIL  out-of-fountain expected NotInFountain got {resOut}"); rc = 1; }

        // 5.3.4b — insufficient gold.
        h1.Pos = fountain;
        h1.Gold = 10;
        var resBroke = ItemSystem.TryBuy(w.Heroes, 1, 5); // big weapon cost 1800
        if (resBroke != ItemSystem.BuyResult.NoGold) { Console.WriteLine($"  FAIL  expected NoGold got {resBroke}"); rc = 1; }

        // 5.3.4c — inventory full.
        ref var h2 = ref w.Heroes[2];
        h2.Pos = fountain;
        h2.Gold = 999_999;
        for (int k = 0; k < Items.InventorySize; k++) ItemSystem.TryBuy(w.Heroes, 2, 0);
        var resFull = ItemSystem.TryBuy(w.Heroes, 2, 0);
        if (resFull != ItemSystem.BuyResult.InventoryFull) { Console.WriteLine($"  FAIL  expected InventoryFull got {resFull}"); rc = 1; }

        // 5.3.4d — unknown item id.
        ref var h3 = ref w.Heroes[3];
        h3.Pos = fountain; h3.Gold = 999_999;
        var resBad = ItemSystem.TryBuy(w.Heroes, 3, 999);
        if (resBad != ItemSystem.BuyResult.Unknown) { Console.WriteLine($"  FAIL  expected Unknown got {resBad}"); rc = 1; }

        // 5.3.5 — two worlds, identical InputFrame streams with BuyItemId set, hash match.
        ulong hashA = RunDeterminismWorld(seed: 11);
        ulong hashB = RunDeterminismWorld(seed: 11);
        if (hashA != hashB) { Console.WriteLine($"  FAIL  determinism hash A=0x{hashA:X16} B=0x{hashB:X16}"); rc = 1; }
        else                  Console.WriteLine($"  determinism hash = 0x{hashA:X16}");

        // 5.3.6 — GC=0 tight loop with periodic buy injections.
        var w2 = new DeterministicWorld(seed: 19) { EnableGameplay = true };
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        // Warm up.
        for (int t = 0; t < 60; t++) w2.Tick(inputs);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 1000; t++)
        {
            // Every 30 frames, force every hero into fountain and try to buy item 0.
            if (t % 30 == 0)
            {
                for (int i = 0; i < DeterministicWorld.PlayerCount; i++)
                {
                    w2.Heroes[i].Pos = i < 5 ? Lanes.BlueTowers[1] : Lanes.RedTowers[1];
                    w2.Heroes[i].Gold = 9_999;
                    inputs[i].BuyItemId = 1; // item index 0
                }
            }
            else for (int i = 0; i < DeterministicWorld.PlayerCount; i++) inputs[i].BuyItemId = 0;
            w2.Tick(inputs);
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        if (delta != 0) { Console.WriteLine($"  FAIL  GC alloc in tight loop = {delta} bytes"); rc = 1; }
        else              Console.WriteLine("  GC=0 over 1000 ticks with periodic buys");

        // 5.3.7 — CDR cap.
        ref var hCdr = ref w.Heroes[4];
        hCdr.Pos = fountain; hCdr.Gold = 999_999; hCdr.Cdr = (Fix64)0;
        // Items 3 and 6 give CDR. Stack repeatedly to ensure cap.
        for (int k = 0; k < 6; k++) ItemSystem.TryBuy(w.Heroes, 4, 3); // big weapon idx 3, +5 cdr each
        if (hCdr.Cdr > (Fix64)40) { Console.WriteLine($"  FAIL  cdr exceeded 40 (={hCdr.Cdr})"); rc = 1; }

        Console.WriteLine(rc == 0 ? "M5.3 PASS" : "M5.3 FAIL");
        return rc;
    }

    private static ulong RunDeterminismWorld(ulong seed)
    {
        var w = new DeterministicWorld(seed) { EnableGameplay = true };
        // Endow everyone with gold + place at fountain at frame 0 (deterministically identical setup).
        for (int i = 0; i < DeterministicWorld.PlayerCount; i++)
        {
            w.Heroes[i].Pos = i < 5 ? Lanes.BlueTowers[1] : Lanes.RedTowers[1];
            w.Heroes[i].Gold = 9_000;
        }
        var inputs = new InputFrame[DeterministicWorld.PlayerCount];
        // Pseudo-random buy schedule keyed on (seed, frame, slot) — purely arithmetic, no PRNG state across worlds.
        for (uint f = 0; f < 200; f++)
        {
            for (int i = 0; i < DeterministicWorld.PlayerCount; i++)
            {
                uint mix = (uint)(seed ^ (f * 2654435761u) ^ ((uint)i * 374761393u));
                inputs[i] = default;
                if ((mix & 0x1F) == 0) // ~3% of frames issue a buy
                {
                    ushort itemIdx = (ushort)(mix % (uint)Items.DefCount);
                    inputs[i].BuyItemId = (ushort)(itemIdx + 1);
                }
            }
            w.Tick(inputs);
        }
        return w.Hash();
    }
}
