// SPDX-License-Identifier: MIT
// M5.6 verification — .mreplay record / replay round-trip.
//
// Asserts:
//   M5.6.1 Header round-trip: writer→reader preserves magic / version / seed / mapId / playerSlots / duration.
//   M5.6.2 Per-frame hash match: live world hashes (recorded as we tick) equal replayed world hashes.
//   M5.6.3 Final hash match between live and replayed simulations.
//   M5.6.4 Initial snapshot bytes recorded equal a fresh world's WriteSnapshot output (frame-0 invariant).
//   M5.6.5 GC=0 tight RecordTick loop (after warmup) — replay writer must be allocation-free per tick.

using System;
using System.Buffers;
using MOBA.Logic.Replay;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Tests;

internal static class M5_6_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M5.6 Verify");
        int rc = 0;

        const int RecordFrames = 450; // 30 seconds @15Hz
        const ulong Seed       = 0xBEEFC0DECAFEFEEDUL;
        const uint  MapId      = 0x4D4F4241;          // "MOBA"
        ReadOnlySpan<byte> playerSlots = stackalloc byte[10] { 0,1,2,3,4,5,6,7,8,9 };

        // --- Live world: tick + record + capture per-frame hashes ---
        var live = new DeterministicWorld(Seed) { EnableGameplay = true };
        var snapBuf = new ArrayBufferWriter<byte>(64 * 1024);
        live.WriteSnapshot(snapBuf);
        int snapLen = snapBuf.WrittenSpan.Length;

        // 5.6.4 — fresh-world snapshot must match (frame-0 invariant: same seed, no inputs applied).
        var fresh = new DeterministicWorld(Seed) { EnableGameplay = true };
        var freshBuf = new ArrayBufferWriter<byte>(64 * 1024);
        fresh.WriteSnapshot(freshBuf);
        if (!snapBuf.WrittenSpan.SequenceEqual(freshBuf.WrittenSpan))
        { Console.WriteLine("  FAIL  M5.6.4 frame-0 snapshot diverges between two seeded worlds"); rc = 1; }

        var writer = new ReplayWriter(initialCapacity: 64 * 1024);
        writer.BeginRecording(MapId, Seed, playerSlots, startUnixSec: 1_700_000_000UL, snapBuf.WrittenSpan);

        var inputs = new InputFrame[10];
        var rng = new System.Random(20240131);
        var liveHashes = new ulong[RecordFrames];
        for (int f = 0; f < RecordFrames; f++)
        {
            for (int i = 0; i < 10; i++)
            {
                inputs[i].JoyX = (sbyte)rng.Next(-100, 101);
                inputs[i].JoyY = (sbyte)rng.Next(-100, 101);
                inputs[i].SkillBits = (byte)rng.Next(0, 16);
                inputs[i].AimAngleDeg = (ushort)rng.Next(0, 360);
                inputs[i].TargetSlot = 0;
                inputs[i].Flags = 0;
                inputs[i].Pad = 0;
                inputs[i].BuyItemId = 0;
            }
            writer.RecordTick(inputs);
            live.Tick(inputs);
            liveHashes[f] = live.Hash();
        }
        var bytes = writer.Finish();
        Console.WriteLine($"  Recorded   : {bytes.Length} bytes ({writer.FramesWritten} frames; snapshot={snapLen}B; payload={(long)bytes.Length - ReplayWriter.HeaderSizeFixed - 4 - snapLen}B)");

        // --- Reader side ---
        var reader = new ReplayReader();
        reader.Open(bytes);

        // 5.6.1 header round-trip
        if (reader.Version != ReplayWriter.CurrentVersion) { Console.WriteLine($"  FAIL  M5.6.1 version {reader.Version}"); rc = 1; }
        if (reader.Seed != Seed)                            { Console.WriteLine("  FAIL  M5.6.1 seed");          rc = 1; }
        if (reader.MapId != MapId)                          { Console.WriteLine("  FAIL  M5.6.1 mapId");         rc = 1; }
        if (reader.DurationFrames != (uint)RecordFrames)    { Console.WriteLine($"  FAIL  M5.6.1 duration={reader.DurationFrames}"); rc = 1; }
        if (reader.SnapshotLength != snapLen)               { Console.WriteLine("  FAIL  M5.6.1 snapshotLen");   rc = 1; }
        for (int i = 0; i < 10; i++)
            if (reader.PlayerSlots[i] != playerSlots[i])    { Console.WriteLine($"  FAIL  M5.6.1 playerSlot[{i}]"); rc = 1; break; }
        if (!reader.SnapshotSpan.SequenceEqual(snapBuf.WrittenSpan))
        { Console.WriteLine("  FAIL  M5.6.1 snapshot bytes diverged through writer/reader"); rc = 1; }

        // --- Replay simulation: fresh world, same seed, decoded inputs, hash per frame ---
        var rep = new DeterministicWorld(reader.Seed) { EnableGameplay = true };
        var replInputs = new InputFrame[10];
        int firstMismatch = -1;
        for (uint f = 0; f < reader.DurationFrames; f++)
        {
            reader.GetTick(f, replInputs);
            rep.Tick(replInputs);
            ulong h = rep.Hash();
            if (h != liveHashes[(int)f]) { firstMismatch = (int)f; break; }
        }

        if (firstMismatch >= 0)
        {
            Console.WriteLine($"  FAIL  M5.6.2 hash diverges at frame {firstMismatch}: live=0x{liveHashes[firstMismatch]:X16} replay=0x{rep.Hash():X16}");
            rc = 1;
        }
        else
        {
            Console.WriteLine($"  Per-frame hash : {RecordFrames}/{RecordFrames} frames match");
        }

        if (rep.Hash() != liveHashes[RecordFrames - 1])
        { Console.WriteLine($"  FAIL  M5.6.3 final hash mismatch live=0x{liveHashes[RecordFrames - 1]:X16} replay=0x{rep.Hash():X16}"); rc = 1; }
        else
            Console.WriteLine($"  Final hash : 0x{rep.Hash():X16}");

        // --- 5.6.5 GC=0 tight RecordTick loop ---
        var w2 = new DeterministicWorld(seed: 99) { EnableGameplay = true };
        var wsnap = new ArrayBufferWriter<byte>(64 * 1024);
        w2.WriteSnapshot(wsnap);
        var w2Writer = new ReplayWriter(initialCapacity: 256 * 1024);
        w2Writer.BeginRecording(0, 99UL, playerSlots, 0UL, wsnap.WrittenSpan);
        for (int f = 0; f < 600; f++) { w2Writer.RecordTick(inputs); } // warmup growth
        // Reset to clear buffer length (avoid re-grow during measurement) yet keep capacity.
        w2Writer.Reset();
        w2Writer.BeginRecording(0, 99UL, playerSlots, 0UL, wsnap.WrittenSpan);

        _ = GC.GetAllocatedBytesForCurrentThread();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        const int tight = 1000;
        for (int f = 0; f < tight; f++) w2Writer.RecordTick(inputs);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        long delta = a1 - a0;
        Console.WriteLine($"  RecordTick : alloc Δ={delta} bytes over {tight} ticks");
        if (delta != 0) { Console.WriteLine($"  FAIL  M5.6.5 RecordTick alloc Δ {delta} bytes"); rc = 1; }

        if (rc == 0) Console.WriteLine("M5.6 PASS");
        return rc;
    }
}
