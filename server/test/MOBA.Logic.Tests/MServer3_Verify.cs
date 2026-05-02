// SPDX-License-Identifier: MIT
// M-Server v3 verification:
//   MSV3.1  MultiRoom        : spin up 2 rooms via RoomManager, join 10 clients each, pump
//                              both concurrently for 1600 ms, verify both rooms advanced frames
//                              and RoomManager.ActiveRooms == 2.
//   MSV3.2  Spectator        : 10 clients join a room, one spectator sends C2S_SpectateRoom,
//                              pump 800 ms — verify SpectatorCount==1 and
//                              SpectatorFramesForwarded > 0.
//   MSV3.3  AutoReconnect    : run room 600 ms, disconnect slot 0, reconnect slot 0 — verify
//                              AutoReconnectPushCount >= 1 (server auto-pushed snapshot without
//                              the client needing to send C2S_RequestResync).

using System;
using System.Diagnostics;
using System.Threading;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Server;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class MServer3_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M-Server v3 Verify");
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {};
        int rc = 0;
        rc |= MultiRoom();
        rc |= Spectator();
        rc |= AutoReconnect();
        return rc;
    }

    private static readonly Random s_portRng = new();
    // Use 63000+ to avoid collisions with MServer1/2 (40000-60000) and MClient2 (41000-61000).
    private static ushort PickPort() => (ushort)(63_000 + s_portRng.Next(0, 2_000));
    private static ushort PickPort2(ushort exclude)
    {
        ushort p; do { p = PickPort(); } while (p == exclude); return p;
    }

    private sealed class Sim
    {
        public byte Slot;
        public NetClient Net = new();
    }

    /// <summary>Connect N clients but do NOT wait for RoomStart.</summary>
    private static Sim[] ConnectClients(ushort port, ulong seed, int n)
    {
        var sims = new Sim[n];
        for (int i = 0; i < n; i++)
        {
            sims[i] = new Sim { Slot = (byte)i };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }
        return sims;
    }

    /// <summary>Pump host + one set of sims until all sims have RoomStarted.</summary>
    private static void WaitRoomStart(RoomHost host, Sim[] sims)
    {
        long t0 = Environment.TickCount64;
        while (true)
        {
            host.Pump();
            foreach (var s in sims) s.Net.Tick();
            int rs = 0; foreach (var s in sims) if (s.Net.RoomStarted) rs++;
            if (rs == sims.Length) break;
            if (Environment.TickCount64 - t0 > 5000) throw new Exception("WaitRoomStart timeout");
            Thread.Sleep(2);
        }
    }

    private static void PumpSims(Sim[] sims) { foreach (var s in sims) s.Net.Tick(); }
    private static void DisconnectSims(Sim[] sims) { foreach (var s in sims) s.Net.Disconnect(); }

    private static void PumpDuration(RoomHost[] hosts, Sim[][] simsAll, NetClient[] extras, int ms)
    {
        var sw = Stopwatch.StartNew();
        long lastInputMs = 0;
        const long inputStepMs = 1000 / 15;
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var h in hosts) h.Pump();
            foreach (var sims in simsAll) PumpSims(sims);
            if (extras != null) foreach (var e in extras) e.Tick();
            long now = sw.ElapsedMilliseconds;
            if (now - lastInputMs >= inputStepMs)
            {
                lastInputMs = now;
                foreach (var sims in simsAll)
                    for (int i = 0; i < sims.Length; i++)
                    {
                        if (!sims[i].Net.Connected) continue;
                        sims[i].Net.SendInput((uint)(now / inputStepMs), default);
                    }
            }
            Thread.Sleep(1);
        }
    }

    // ---------------------------------------------------------------- MSV3.1

    private static int MultiRoom()
    {
        Console.WriteLine("  MSV3.1 MultiRoom");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ulong  seedA = 0xAA_BB_CC_DDUL;
        ulong  seedB = 0x11_22_33_44UL;

        var mgr   = new RoomManager();
        ushort portA = PickPort();
        ushort portB = PickPort2(portA);
        var hostA = mgr.GetOrCreate(roomId: 700, seed: seedA, port: portA);
        var hostB = mgr.GetOrCreate(roomId: 701, seed: seedB, port: portB);

        var simsA = ConnectClients(portA, seedA, N);
        var simsB = ConnectClients(portB, seedB, N);

        // Wait for both rooms to signal RoomStart on all clients.
        long t0 = Environment.TickCount64;
        while (true)
        {
            mgr.Pump(); PumpSims(simsA); PumpSims(simsB);
            int rsA = 0; foreach (var s in simsA) if (s.Net.RoomStarted) rsA++;
            int rsB = 0; foreach (var s in simsB) if (s.Net.RoomStarted) rsB++;
            if (rsA == N && rsB == N) break;
            if (Environment.TickCount64 - t0 > 8000) throw new Exception("MultiRoom warm timeout");
            Thread.Sleep(2);
        }

        PumpDuration(new[] { hostA, hostB }, new[] { simsA, simsB }, null, ms: 1600);

        bool frameA  = hostA.World.Frame > 0;
        bool frameB  = hostB.World.Frame > 0;
        bool inputA  = hostA.InputsReceived > 0;
        bool inputB  = hostB.InputsReceived > 0;
        bool actOk   = mgr.ActiveRooms == 2;
        bool totalOk = mgr.TotalRooms  == 2;

        Console.WriteLine($"    roomA: frame={hostA.World.Frame} inputs={hostA.InputsReceived}");
        Console.WriteLine($"    roomB: frame={hostB.World.Frame} inputs={hostB.InputsReceived}");
        Console.WriteLine($"    mgr: active={mgr.ActiveRooms} total={mgr.TotalRooms}");

        bool ok = frameA && frameB && inputA && inputB && actOk && totalOk;
        Console.WriteLine(ok ? "  MSV3.1 OK" : "  MSV3.1 FAIL");

        DisconnectSims(simsA); DisconnectSims(simsB);
        mgr.StopAll();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV3.2

    private static int Spectator()
    {
        Console.WriteLine("  MSV3.2 Spectator");
        Items.Reset(); Items.RegisterDefaults();
        const int N    = 10;
        ushort    port = PickPort();
        ulong     seed = 0x5C_EC_5C_EFUL;

        var host = new RoomHost(roomId: 702, seed: seed, port: port);
        var sims = ConnectClients(port, seed, N);
        WaitRoomStart(host, sims);

        // Create spectator client (no PlayerSlot — does not send C2S_JoinRoom automatically).
        var spec = new NetClient(); // PlayerSlot defaults to byte.MaxValue → no auto-join
        spec.Connect("127.0.0.1", port);

        // Wait for KCP handshake (spec.Connected == true).
        long tConn = Environment.TickCount64;
        while (!spec.Connected)
        {
            host.Pump(); spec.Tick();
            if (Environment.TickCount64 - tConn > 2000) throw new Exception("spectator connect timeout");
            Thread.Sleep(2);
        }

        // Send C2S_SpectateRoom.
        spec.SendSpectateRoom(host.Room.RoomId);

        // Pump briefly until server registers the spectator.
        long tSpec = Environment.TickCount64;
        while (host.SpectatorCount == 0)
        {
            host.Pump(); spec.Tick(); PumpSims(sims);
            if (Environment.TickCount64 - tSpec > 2000) throw new Exception("spectator register timeout");
            Thread.Sleep(2);
        }

        // Run 800 ms with spectator receiving FrameBatches.
        PumpDuration(new[] { host }, new[] { sims }, new[] { spec }, ms: 800);

        bool specCountOk  = host.SpectatorCount == 1;
        bool framesOk     = host.SpectatorFramesForwarded > 0;
        bool ackOk        = spec.IsSpectating;
        Console.WriteLine($"    spectatorCount={host.SpectatorCount} framesForwarded={host.SpectatorFramesForwarded} ack={spec.IsSpectating}");

        bool ok = specCountOk && framesOk && ackOk;
        Console.WriteLine(ok ? "  MSV3.2 OK" : "  MSV3.2 FAIL");

        DisconnectSims(sims); spec.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV3.3

    private static int AutoReconnect()
    {
        Console.WriteLine("  MSV3.3 Auto-reconnect push");
        Items.Reset(); Items.RegisterDefaults();
        const int N    = 10;
        ushort    port = PickPort();
        ulong     seed = 0xB0_0B_FEEDUL;

        var host = new RoomHost(roomId: 703, seed: seed, port: port);
        var sims = ConnectClients(port, seed, N);
        WaitRoomStart(host, sims);

        // Run 600 ms so at least one snapshot exists in the ring.
        PumpDuration(new[] { host }, new[] { sims }, null, ms: 600);

        bool snapshotExists = host.Room.SnapshotsTaken > 0;
        Console.WriteLine($"    snapshotsTaken={host.Room.SnapshotsTaken}");
        if (!snapshotExists)
        {
            Console.WriteLine("  MSV3.3 FAIL (no snapshot yet — increase pump duration)");
            DisconnectSims(sims); host.Stop();
            return 1;
        }

        // Disconnect slot 0.
        sims[0].Net.Disconnect();
        PumpDuration(new[] { host }, new[] { sims }, null, ms: 120); // let server see disconnect

        // Reconnect slot 0 with a fresh NetClient.
        sims[0] = new Sim { Slot = 0 };
        sims[0].Net.PlayerSlot = 0;
        sims[0].Net.Connect("127.0.0.1", port);

        // Wait for reconnect to be acknowledged (RoomStart received = new session active).
        long tReconn = Environment.TickCount64;
        while (!sims[0].Net.RoomStarted)
        {
            host.Pump(); sims[0].Net.Tick();
            if (Environment.TickCount64 - tReconn > 3000) break;
            Thread.Sleep(2);
        }
        // Flush a bit more to let the auto-push arrive.
        PumpDuration(new[] { host }, new[] { sims }, null, ms: 200);

        bool pushOk  = host.AutoReconnectPushCount >= 1;
        bool startOk = sims[0].Net.RoomStarted;
        Console.WriteLine($"    autoReconnectPushCount={host.AutoReconnectPushCount} reconnectRoomStarted={startOk}");

        bool ok = pushOk;
        Console.WriteLine(ok ? "  MSV3.3 OK" : "  MSV3.3 FAIL");

        DisconnectSims(sims);
        host.Stop();
        return ok ? 0 : 1;
    }
}
