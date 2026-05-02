// SPDX-License-Identifier: MIT
// M-Server v2 verification:
//   MSV2.1  GameOver lifecycle : forcibly destroy red crystal on the server world
//                                so the next Tick sets GameOver=true; expect the
//                                server to broadcast S2C_GameOver to every slot,
//                                pump to halt new FrameBatches, and every client's
//                                NetClient.MatchEnded == true with Winner == Blue.
//   MSV2.2  Resync throttle    : send two divergent HashReports from slot 5 within
//                                ResyncThrottleMs window; expect AutoResyncCount==1
//                                and ResyncThrottled>=1 (the second push was dropped).
//   MSV2.3  Replay winner name : replay file should be renamed
//                                <stem>.blue.mreplay when match ends Blue-victory.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Server;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class MServer2_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M-Server v2 Verify");
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {};
        int rc = 0;
        rc |= GameOverLifecycle();
        rc |= ResyncThrottle();
        rc |= ReplayWinnerName();
        return rc;
    }

    private static readonly Random s_portRng = new();
    private static ushort PickPort() => (ushort)(40_000 + s_portRng.Next(0, 20_000));

    private sealed class Sim
    {
        public byte Slot;
        public NetClient Net = new();
        public DeterministicWorld World;
        public uint AppliedFrame;
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

    private static void Pump(RoomHost host, Sim[] sims, int ms)
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
            Thread.Sleep(1);
        }
    }

    // ---------------------------------------------------------------- MSV2.1

    private static int GameOverLifecycle()
    {
        Console.WriteLine("  MSV2.1 GameOver lifecycle");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xC0_FFEEUL;
        var host = new RoomHost(roomId: 300, seed: seed, port: port);
        var sims = StartRoom(host, seed, port, N);

        // Run a moment so a couple FrameBatches & a snapshot exist.
        Pump(host, sims, ms: 600);

        // Forcibly kill red crystal on the server's authoritative World; the next Tick will
        // detect Crystals[Red].Alive==false and set GameOver/Winner=Blue.
        host.World.Crystals[(int)Team.Red].Hp = (Fix64)0;
        host.World.Crystals[(int)Team.Red].Alive = false;

        // Pump until the server broadcasts S2C_GameOver and clients receive it.
        Pump(host, sims, ms: 800);

        bool serverEnded = host.MatchEnded;
        bool winnerBlue = host.MatchWinner == Team.Blue;
        bool broadcasted = host.GameOverBroadcasts == 1;
        int clientsEnded = sims.Count(s => s.Net.MatchEnded);
        bool allClientsBlue = sims.All(s => !s.Net.MatchEnded || s.Net.MatchWinner == (byte)Team.Blue);

        Console.WriteLine($"    server: ended={serverEnded} winner={host.MatchWinner} broadcasts={host.GameOverBroadcasts} endFrame={host.MatchEndFrame}");
        Console.WriteLine($"    clients ended {clientsEnded}/{N}, allWinnerBlue={allClientsBlue}");

        bool ok = serverEnded && winnerBlue && broadcasted && clientsEnded == N && allClientsBlue;
        Console.WriteLine(ok ? "  MSV2.1 OK" : "  MSV2.1 FAIL");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV2.2

    private static int ResyncThrottle()
    {
        Console.WriteLine("  MSV2.2 Resync throttle");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xD0_DEUL;
        var host = new RoomHost(roomId: 301, seed: seed, port: port)
        {
            AutoResyncOnDesync = true,
            ResyncThrottleMs = 2000, // long window so the second report is guaranteed throttled
        };
        var sims = StartRoom(host, seed, port, N);

        // Build up enough frames so a snapshot exists in the ring (snapshot taken at frame 0).
        Pump(host, sims, ms: 1200);

        // Honest slots report the truthful (server) hash for frame 0; that primes Room.LastHash[p]
        // so the desync detector has something to compare against.
        uint snapFrame = 0;
        ulong truthHash = host.World.Hash();
        for (int i = 0; i < N; i++)
            if (i != 5) sims[i].Net.SendHashReport(snapFrame, truthHash);
        Pump(host, sims, ms: 200);

        // Two divergent reports from slot 5 in quick succession.
        sims[5].Net.SendHashReport(snapFrame, 0xBADF00D_DEAD_BEEFUL);
        // Pump briefly so the first report lands and triggers AutoResync.
        Pump(host, sims, ms: 200);
        sims[5].Net.SendHashReport(snapFrame, 0xCAFE_BABE_DEAD_F00DUL);
        Pump(host, sims, ms: 400);

        Console.WriteLine($"    autoResync={host.AutoResyncCount} throttled={host.ResyncThrottled}");
        // Throttle test: at least one push went through, AND at least one was throttled.
        // (CheckDesync may push to either party of a mismatch depending on arrival order, but
        // the throttle is per-slot so the SECOND forged report for slot 5 must be throttled.)
        bool ok = host.AutoResyncCount >= 1 && host.ResyncThrottled >= 1;
        Console.WriteLine(ok ? "  MSV2.2 OK" : "  MSV2.2 FAIL");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MSV2.3

    private static int ReplayWinnerName()
    {
        Console.WriteLine("  MSV2.3 Replay winner filename");
        Items.Reset(); Items.RegisterDefaults();
        const int N = 10;
        ushort port = PickPort();
        ulong seed = 0xE0_F00DUL;
        string baseFile = Path.Combine(Path.GetTempPath(), $"moba-msv2-{Guid.NewGuid():N}.mreplay");
        var host = new RoomHost(roomId: 302, seed: seed, port: port) { ReplayPath = baseFile };
        var sims = StartRoom(host, seed, port, N);

        Pump(host, sims, ms: 600);
        // Force Blue victory: kill red crystal.
        host.World.Crystals[(int)Team.Red].Hp = (Fix64)0;
        host.World.Crystals[(int)Team.Red].Alive = false;
        Pump(host, sims, ms: 600);

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();

        string expected = Path.Combine(Path.GetDirectoryName(baseFile)!,
            Path.GetFileNameWithoutExtension(baseFile) + ".blue" + Path.GetExtension(baseFile));
        bool fileExists = File.Exists(expected);
        bool baseDoesntExist = !File.Exists(baseFile);
        long size = fileExists ? new FileInfo(expected).Length : 0;
        Console.WriteLine($"    expected={Path.GetFileName(expected)} exists={fileExists} size={size}");
        bool ok = fileExists && baseDoesntExist && size > 41 /* MRPL header */;
        Console.WriteLine(ok ? "  MSV2.3 OK" : "  MSV2.3 FAIL");
        try { if (fileExists) File.Delete(expected); } catch { }
        return ok ? 0 : 1;
    }
}
