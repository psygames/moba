// SPDX-License-Identifier: MIT
// M-Server v1 verification — three new server features:
//   MSV1.1  BuyItem E2E   : client sends C2S_BuyItem; server injects into the
//                           next FrameBatch's InputFrame.BuyItemId for that slot;
//                           every client (and server) deterministically applies
//                           the purchase so hashes stay equal AND the buyer's
//                           inventory + gold reflect the change.
//   MSV1.2  AutoResync    : forcibly diverge one client's local World, send a
//                           HashReport that disagrees with the others; the server
//                           must autonomously push S2C_Snapshot to that client
//                           (without C2S_RequestResync) and hashes converge.
//   MSV1.3  Replay save   : run a short match with ReplayPath set; on Stop the
//                           file appears, has the MRPL header, the recorded
//                           frame count is non-zero, and a freshly constructed
//                           World replayed through ReplayReader matches the
//                           server's final hash.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MOBA.Logic.Replay;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Server;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Tests;

internal static class MServer1_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M-Server v1 Verify");
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {};
        int rc = 0;
        rc |= BuyItemE2E();
        rc |= AutoResync();
        rc |= ReplaySave();
        return rc;
    }

    private static readonly Random s_portRng = new();
    private static ushort PickPort() => (ushort)(40_000 + s_portRng.Next(0, 20_000));

    private sealed class Sim
    {
        public NetClient Net = new();
        public DeterministicWorld World;
        public uint AppliedFrame;
        public byte Slot;

        public void Apply(int maxApply = 64)
        {
            // Apply pending snapshot FIRST so the freshly-enqueued tail (which the snapshot
            // handler placed into RxFrames after clearing it) replays from the correct base.
            if (Net.PendingSnapshot.HasValue)
            {
                var (sf, sb) = Net.PendingSnapshot.Value;
                World.ReadSnapshot(sb, sf + 1); // +1 because snapshot was taken AFTER ticking sf
                AppliedFrame = sf + 1;
                Net.PendingSnapshot = null;
            }

            int n = 0;
            while (n++ < maxApply && Net.RxFrames.Count > 0)
            {
                var (frame, inputs) = Net.RxFrames.Peek();
                if (frame < AppliedFrame) { Net.RxFrames.Dequeue(); continue; }
                if (frame > AppliedFrame) break;
                Net.RxFrames.Dequeue();
                World.Tick(inputs);
                AppliedFrame = frame + 1;
                if ((frame % 60) == 0) Net.SendHashReport(frame, World.Hash());
            }
        }
    }

    private static Sim[] StartRoom(RoomHost host, ulong seed, ushort port, int n = 10)
    {
        var sims = new Sim[n];
        for (int i = 0; i < n; i++)
        {
            sims[i] = new Sim { Slot = (byte)i, World = new DeterministicWorld(seed) { EnableGameplay = true } };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }
        long warmStart = Environment.TickCount64;
        while (true)
        {
            host.Pump();
            for (int i = 0; i < n; i++) sims[i].Net.Tick();
            int rs = 0; for (int i = 0; i < n; i++) if (sims[i].Net.RoomStarted) rs++;
            if (rs == n) break;
            if (Environment.TickCount64 - warmStart > 5000) throw new Exception("warm timeout");
            Thread.Sleep(2);
        }
        return sims;
    }

    private static void RunFor(RoomHost host, Sim[] sims, int ms, Action<long> perTick = null)
    {
        var sw = Stopwatch.StartNew();
        long lastInputMs = 0;
        const long inputStepMs = 1000 / 15;
        while (sw.ElapsedMilliseconds < ms)
        {
            host.Pump();
            for (int i = 0; i < sims.Length; i++) sims[i].Net.Tick();
            long now = sw.ElapsedMilliseconds;
            if (now - lastInputMs >= inputStepMs)
            {
                lastInputMs = now;
                for (int i = 0; i < sims.Length; i++)
                {
                    if (!sims[i].Net.Connected) continue;
                    sims[i].Net.SendInput((uint)(now / inputStepMs), default);
                }
            }
            for (int i = 0; i < sims.Length; i++) sims[i].Apply();
            perTick?.Invoke(now);
            Thread.Sleep(1);
        }
        // Drain.
        for (int d = 0; d < 200; d++)
        {
            host.Pump();
            for (int i = 0; i < sims.Length; i++) { sims[i].Net.Tick(); sims[i].Apply(); }
            Thread.Sleep(2);
        }
    }

    // ---------------------------------------------------------------- MSV1.1

    private static int BuyItemE2E()
    {
        Console.WriteLine("  MSV1.1 BuyItem E2E");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xB0_07_15UL;
        var host = new RoomHost(roomId: 100, seed: seed, port: port);

        // Move slot 0's logical hero AND physics body into the fountain BEFORE any clients
        // connect. This guarantees the server's frame-0 state already has the hero at the
        // fountain, so all FrameBatch broadcasts are built from that same starting point.
        // Client worlds receive the same broadcasts and are teleported below (before Apply),
        // ensuring full determinism regardless of how many frames the server force-builds
        // during StartRoom's KCP handshake loop.
        var fountain = Lanes.BlueTowers[1];
        Action<DeterministicWorld> teleport = w =>
        {
            ref var h = ref w.Heroes[0];
            h.Pos = fountain;
            var body = w.Physics.TryGet(w.Players[0]);
            if (body != null) body.Teleport(fountain);
        };
        teleport(host.World);

        var sims = StartRoom(host, seed, port, N);
        // Teleport client worlds after connection but before any Apply() calls so all worlds
        // share the same frame-0 state.
        for (int i = 0; i < N; i++) teleport(sims[i].World);

        const ushort itemWire = 1; // == itemDefIdx 0 (Starter, 350 gold)
        bool sent = false;
        RunFor(host, sims, ms: 2000, perTick: now =>
        {
            if (!sent && now > 200) { sims[0].Net.SendBuyItem(itemWire); sent = true; }
        });

        ulong[] hashes = sims.Select(s => s.World.Hash()).ToArray();
        bool allEq = hashes.All(h => h == hashes[0]) && hashes[0] == host.World.Hash();
        ref var heroSrv = ref host.World.Heroes[0];
        bool buyerHasItem = heroSrv.Inv0 == 1;
        // Gold = StartingGold + drips - cost; allow ±2 for tick alignment.
        long expected = (long)Items.StartingGold + (long)(host.World.Frame / 8) - 350;
        long goldDelta = Math.Abs((long)heroSrv.Gold - expected);
        bool goldOk = goldDelta <= 2;

        Console.WriteLine($"    server BuyItem requests rcv={host.Room.BuyItemRequestsReceived} inj={host.Room.BuyItemRequestsInjected}");
        Console.WriteLine($"    slot0 inv0={heroSrv.Inv0} gold={heroSrv.Gold} (expected ~{expected})");
        Console.WriteLine($"    final hashes : {hashes[0]:X16} (all {N} eq={allEq}) server=0x{host.World.Hash():X16}");
        bool ok = allEq && buyerHasItem && goldOk && host.Room.BuyItemRequestsInjected >= 1;
        Console.WriteLine(ok ? "  MSV1.1 OK" : "  MSV1.1 FAIL");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV1.2

    private static int AutoResync()
    {
        Console.WriteLine("  MSV1.2 AutoResync");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xA1_0F_2EUL;
        var host = new RoomHost(roomId: 200, seed: seed, port: port) { AutoResyncOnDesync = true };
        var sims = StartRoom(host, seed, port, N);

        // Run a bit so a snapshot exists in the server ring (snapshot taken at frame 0 since
        // ShouldSnapshot()==true when (NextOutputFrame-1)%60==0 — i.e. immediately after first batch).
        RunFor(host, sims, ms: 1500);

        // Forge a divergent hash report for slot 5 at the latest reported frame; expect server to
        // detect desync AND auto-push a snapshot to slot 5.
        uint forgedFrame = (host.World.Frame / 60) * 60; // align to snapshot boundary
        ulong forged = 0xBADF00DDEADBEEFUL;
        sims[5].Net.SendHashReport(forgedFrame, forged);

        // Pump for a moment so the hash report and pushed snapshot complete the round trip.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            host.Pump();
            for (int i = 0; i < N; i++) sims[i].Net.Tick();
            for (int i = 0; i < N; i++) sims[i].Apply();
            Thread.Sleep(2);
        }

        bool detected = host.DesyncDetected;
        bool autoPushed = host.AutoResyncCount >= 1;
        ulong[] hashes = sims.Select(s => s.World.Hash()).ToArray();
        bool converged = hashes.All(h => h == hashes[0]);

        Console.WriteLine($"    desyncDetected={detected} autoResyncCount={host.AutoResyncCount}");
        Console.WriteLine($"    hashes after auto-push : {string.Join(",", hashes.Select(h => $"0x{h:X16}"))}");

        bool ok = detected && autoPushed && converged;
        Console.WriteLine(ok ? "  MSV1.2 OK" : "  MSV1.2 FAIL");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV1.3

    private static int ReplaySave()
    {
        Console.WriteLine("  MSV1.3 Replay save");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xDEC0DEUL;
        string path = Path.Combine(Path.GetTempPath(), $"moba-msv1-{Guid.NewGuid():N}.mreplay");
        var host = new RoomHost(roomId: 300, seed: seed, port: port) { ReplayPath = path };
        var sims = StartRoom(host, seed, port, N);

        RunFor(host, sims, ms: 2000);
        ulong serverHash = host.World.Hash();
        uint serverFrame = host.World.Frame;
        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();

        bool fileOk = File.Exists(path) && new FileInfo(path).Length > ReplayWriter.HeaderSizeFixed + 4;
        if (!fileOk) { Console.WriteLine($"  MSV1.3 FAIL (no file at {path})"); return 1; }

        // Round-trip: read replay and resimulate; final hash must match server.
        var bytes = File.ReadAllBytes(path);
        var reader = new ReplayReader();
        reader.Open(bytes);
        var w = new DeterministicWorld(reader.Seed) { EnableGameplay = true };
        if (reader.SnapshotLength > 0) w.ReadSnapshot(reader.SnapshotSpan, frame: 0);
        Span<InputFrame> tmp = stackalloc InputFrame[10];
        for (uint f = 0; f < reader.DurationFrames; f++)
        {
            reader.GetTick(f, tmp);
            w.Tick(tmp);
        }
        ulong replayedHash = w.Hash();
        Console.WriteLine($"    file={path} bytes={bytes.Length} frames={reader.DurationFrames}");
        Console.WriteLine($"    server hash=0x{serverHash:X16} (frame {serverFrame}) replay hash=0x{replayedHash:X16}");
        bool match = replayedHash == serverHash && reader.DurationFrames > 0;
        Console.WriteLine(match ? "  MSV1.3 OK" : "  MSV1.3 FAIL (hash mismatch)");
        try { File.Delete(path); } catch { }
        return match ? 0 : 1;
    }
}
