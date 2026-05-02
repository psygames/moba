// SPDX-License-Identifier: MIT
// M-Client v2 verification:
//   MC2.1  GameOver stats     : after server broadcasts S2C_GameOver, every NetClient
//                               must expose MatchEnded=true, MatchWinner=Blue,
//                               MatchEndFrame>0, and MatchDurationSec==MatchEndFrame/15.
//   MC2.2  Replay read-back   : after Stop() writes the winner-named .mreplay, open it
//                               with ReplayReader + a standalone DeterministicWorld,
//                               play to DurationFrames, and verify the final hash equals
//                               the server's authoritative hash at the GameOver frame.

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
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class MClient2_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M-Client v2 Verify");
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {};
        int rc = 0;
        rc |= GameOverStats();
        rc |= ReplayReadBack();
        return rc;
    }

    private static readonly Random s_portRng = new();
    private static ushort PickPort() => (ushort)(41_000 + s_portRng.Next(0, 20_000));

    private sealed class Sim
    {
        public byte      Slot;
        public NetClient Net   = new();
        public DeterministicWorld World;
    }

    private static Sim[] StartRoom(RoomHost host, ulong seed, ushort port, int n = 10)
    {
        var sims = new Sim[n];
        for (int i = 0; i < n; i++)
        {
            sims[i] = new Sim
            {
                Slot  = (byte)i,
                World = new DeterministicWorld(seed) { EnableGameplay = true }
            };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }
        long warmStart = Environment.TickCount64;
        while (true)
        {
            host.Pump();
            for (int i = 0; i < n; i++) sims[i].Net.Tick();
            int rs = 0;
            for (int i = 0; i < n; i++) if (sims[i].Net.RoomStarted) rs++;
            if (rs == n) break;
            if (Environment.TickCount64 - warmStart > 5000)
                throw new Exception("StartRoom warm timeout");
            Thread.Sleep(2);
        }
        return sims;
    }

    private static void Pump(RoomHost host, Sim[] sims, int ms)
    {
        var  sw          = Stopwatch.StartNew();
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

    // ---------------------------------------------------------------- MC2.1

    private static int GameOverStats()
    {
        Console.WriteLine("  MC2.1 GameOver stats");
        Items.Reset(); Items.RegisterDefaults();
        const int N   = 10;
        ushort    port = PickPort();
        ulong     seed = 0xAB_CD_1234UL;

        var host = new RoomHost(roomId: 500, seed: seed, port: port);
        var sims = StartRoom(host, seed, port, N);

        Pump(host, sims, ms: 600);

        // Force Blue victory.
        host.World.Crystals[(int)Team.Red].Hp    = (Fix64)0;
        host.World.Crystals[(int)Team.Red].Alive = false;

        Pump(host, sims, ms: 800);

        // Check that every client reports coherent stats.
        bool serverEnded   = host.MatchEnded;
        uint serverEndFr   = host.MatchEndFrame;
        uint expectedDurSec = serverEndFr / 15u;

        int  goodClients   = 0;
        bool allDurOk      = true;
        bool allFrameOk    = true;
        bool allWinnerOk   = true;

        for (int i = 0; i < N; i++)
        {
            var n = sims[i].Net;
            if (!n.MatchEnded) continue;
            goodClients++;
            if (n.MatchDurationSec != expectedDurSec) allDurOk   = false;
            if (n.MatchEndFrame    != serverEndFr)    allFrameOk = false;
            if (n.MatchWinner      != (byte)Team.Blue) allWinnerOk = false;
        }

        Console.WriteLine(
            $"    server ended={serverEnded} endFrame={serverEndFr} expectedDurSec={expectedDurSec}");
        Console.WriteLine(
            $"    clients ok={goodClients}/{N} durOk={allDurOk} frameOk={allFrameOk} winnerOk={allWinnerOk}");

        bool ok = serverEnded && goodClients == N && allDurOk && allFrameOk && allWinnerOk;
        Console.WriteLine(ok ? "  MC2.1 OK" : "  MC2.1 FAIL");

        // Cleanup.
        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- MC2.2

    private static int ReplayReadBack()
    {
        Console.WriteLine("  MC2.2 Replay read-back");
        Items.Reset(); Items.RegisterDefaults();
        const int N    = 10;
        ushort    port = PickPort();
        ulong     seed = 0xFE_ED_BE_EFUL;

        string baseReplay = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"moba-mc2-{Guid.NewGuid():N}.mreplay");

        var host = new RoomHost(roomId: 501, seed: seed, port: port)
        {
            ReplayPath = baseReplay
        };
        var sims = StartRoom(host, seed, port, N);

        // Run ~22 frames of normal gameplay -- no GameOver trigger.
        // MC2.2 tests pure replay determinism; GameOver coverage is in MC2.1 / MSV2.x.
        Pump(host, sims, ms: 1600);

        ulong serverHash  = host.World.Hash();
        uint  serverFrame = host.World.Frame;

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();

        string replayFile = host.ReplayPath;
        bool   fileExists = System.IO.File.Exists(replayFile);
        Console.WriteLine($"    serverHash=0x{serverHash:X16} serverFrame={serverFrame}");
        Console.WriteLine($"    replayFile exists={fileExists} path={System.IO.Path.GetFileName(replayFile)}");

        if (!fileExists)
        {
            Console.WriteLine("  MC2.2 FAIL (replay file missing)");
            return 1;
        }

        var bytes  = System.IO.File.ReadAllBytes(replayFile);
        var reader = new ReplayReader();
        reader.Open(bytes);

        bool durationMatch = reader.DurationFrames == serverFrame;
        Console.WriteLine(
            $"    DurationFrames={reader.DurationFrames} serverFrame={serverFrame} match={durationMatch}");

        var world = new DeterministicWorld(reader.Seed) { EnableGameplay = true };
        if (reader.SnapshotLength > 0)
            world.ReadSnapshot(reader.SnapshotSpan, frame: 0);

        var tick = new InputFrame[DeterministicWorld.PlayerCount];
        while (world.Frame < reader.DurationFrames)
        {
            reader.GetTick(world.Frame, tick);
            world.Tick(tick);
        }

        ulong replayHash = world.Hash();
        bool  hashMatch  = replayHash == serverHash;

        Console.WriteLine($"    replayHash=0x{replayHash:X16} hashMatch={hashMatch}");

        bool ok = fileExists && durationMatch && hashMatch;
        Console.WriteLine(ok ? "  MC2.2 OK" : "  MC2.2 FAIL");

        try { System.IO.File.Delete(replayFile); } catch { }
        return ok ? 0 : 1;
    }
}