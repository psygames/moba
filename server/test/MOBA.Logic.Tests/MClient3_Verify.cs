// SPDX-License-Identifier: MIT
// M-Client v3 verification:
//   MC3.1 SpectatorHashMatch : spectator connects BEFORE the match starts, receives every
//                              FrameBatch from frame 0, ticks a local DeterministicWorld
//                              and compares its final hash with the server's authoritative hash.
//   MC3.2 SpectatorGameOver  : spectator mid-game; server ends with Blue victory;
//                              spectator's NetClient.MatchEnded == true, MatchWinner == Blue.

using System;
using System.Diagnostics;
using System.Threading;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Server;
using MOBA.Shared.Protocol;
using Fix64 = Box2DSharp.Common.FP;

namespace MOBA.Logic.Tests;

internal static class MClient3_Verify
{
    public static int Execute()
    {
        Console.WriteLine("M-Client v3 Verify");
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {};
        int rc = 0;
        rc |= SpectatorHashMatch();
        rc |= SpectatorGameOver();
        return rc;
    }

    private static readonly Random s_portRng = new();
    // 66000-66999 — dedicated range for MC3 to avoid collisions.
    private static ushort PickPort() => (ushort)(66_000 + s_portRng.Next(0, 1_000));

    // ─── helpers ────────────────────────────────────────────────────────────────

    private sealed class Sim { public byte Slot; public NetClient Net = new(); }

    /// <summary>Connect <paramref name="n"/> players and wait until all have RoomStarted.
    /// <paramref name="extra"/> is optional and gets Tick()ed alongside the sims.</summary>
    private static Sim[] StartRoom(RoomHost host, ushort port, int n, NetClient extra = null)
    {
        var sims = new Sim[n];
        for (int i = 0; i < n; i++)
        {
            sims[i] = new Sim { Slot = (byte)i };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }
        long t0 = Environment.TickCount64;
        while (true)
        {
            host.Pump();
            foreach (var s in sims) s.Net.Tick();
            extra?.Tick();
            int rs = 0; foreach (var s in sims) if (s.Net.RoomStarted) rs++;
            if (rs == n) break;
            if (Environment.TickCount64 - t0 > 6000) throw new Exception("StartRoom timeout");
            Thread.Sleep(2);
        }
        return sims;
    }

    private static void Pump(RoomHost host, Sim[] sims, NetClient extra, int ms)
    {
        var sw = Stopwatch.StartNew();
        long lastInputMs = 0;
        const long inputStepMs = 1000 / 15;
        while (sw.ElapsedMilliseconds < ms)
        {
            host.Pump();
            foreach (var s in sims) s.Net.Tick();
            extra?.Tick();
            long now = sw.ElapsedMilliseconds;
            if (now - lastInputMs >= inputStepMs)
            {
                lastInputMs = now;
                foreach (var s in sims)
                {
                    if (!s.Net.Connected) continue;
                    s.Net.SendInput((uint)(now / inputStepMs), default);
                }
            }
            Thread.Sleep(1);
        }
    }

    // ─── MC3.1 ──────────────────────────────────────────────────────────────────

    private static int SpectatorHashMatch()
    {
        Console.WriteLine("  MC3.1 Spectator hash match");
        Items.Reset(); Items.RegisterDefaults();
        const int N    = 10;
        ushort    port = PickPort();
        ulong     seed = 0x5EC_5EC_00UL;

        var host = new RoomHost(roomId: 600, seed: seed, port: port);

        // Connect spectator BEFORE the room starts so it receives every FrameBatch.
        var spec = new NetClient(); // PlayerSlot = byte.MaxValue → no auto-join
        spec.Connect("127.0.0.1", port);
        long tConn = Environment.TickCount64;
        while (!spec.Connected)
        {
            host.Pump(); spec.Tick();
            if (Environment.TickCount64 - tConn > 2000) throw new Exception("spec connect timeout");
            Thread.Sleep(2);
        }
        spec.SendSpectateRoom(host.Room.RoomId);

        // Wait for SpectateAck (seed is now in spec.Seed).
        long tAck = Environment.TickCount64;
        while (!spec.IsSpectating)
        {
            host.Pump(); spec.Tick();
            if (Environment.TickCount64 - tAck > 2000) throw new Exception("SpectateAck timeout");
            Thread.Sleep(2);
        }

        // Room hasn't started yet (no players); no snapshot was pushed. spec.PendingSnapshot == null.
        // Now connect players → room starts → broadcasts from frame 0.
        var sims = StartRoom(host, port, N, extra: spec);

        // Run ~24 frames of gameplay; spec receives every FrameBatch.
        Pump(host, sims, spec, ms: 1600);

        // Extra flush so the last in-flight FrameBatches reach the spectator
        // before we stop the host (no new inputs, just drain the KCP queues).
        Pump(host, sims, spec, ms: 200);

        // Capture server final state.
        ulong serverHash  = host.World.Hash();
        uint  serverFrame = host.World.Frame;

        foreach (var s in sims) s.Net.Disconnect();
        host.Stop();

        // Drain remaining KCP packets buffered in the OS receive queue.
        var drain = Stopwatch.StartNew();
        while (drain.ElapsedMilliseconds < 300) { spec.Tick(); Thread.Sleep(2); }
        spec.Disconnect();

        Console.WriteLine($"    isSpectating={spec.IsSpectating} seed=0x{spec.Seed:X}");
        Console.WriteLine($"    serverFrame={serverFrame} serverHash=0x{serverHash:X16}");
        Console.WriteLine($"    rxFrames queued={spec.RxFrames.Count}");

        // Rebuild the spectator's world.
        // Since spec joined before room start, PendingSnapshot should be null.
        var specWorld = new DeterministicWorld(spec.Seed) { EnableGameplay = true };
        if (spec.PendingSnapshot.HasValue)
        {
            // Defensive: snapshot was pushed (e.g. room started briefly before ack reached us).
            var (sf, sb) = spec.PendingSnapshot.Value;
            specWorld.ReadSnapshot(sb, frame: (uint)sf);
            spec.PendingSnapshot = null;
        }

        // Apply received frames in order.
        while (spec.RxFrames.TryDequeue(out var pair))
        {
            if (pair.frame == specWorld.Frame)
                specWorld.Tick(pair.inputs);
        }

        uint  specFrame = specWorld.Frame;
        ulong specHash  = specWorld.Hash();
        bool  frameOk   = specFrame == serverFrame;
        bool  hashOk    = specHash  == serverHash;

        Console.WriteLine($"    specFrame={specFrame} specHash=0x{specHash:X16} frameOk={frameOk} hashOk={hashOk}");

        // frameOk allows ±2 tolerance: the server's last tick may not have been broadcast
        // before Stop() was called. Hash equality is the authoritative determinism proof.
        bool ok = spec.IsSpectating && hashOk && specFrame >= serverFrame - 2;
        Console.WriteLine(ok ? "  MC3.1 OK" : "  MC3.1 FAIL");
        return ok ? 0 : 1;
    }

    // ─── MC3.2 ──────────────────────────────────────────────────────────────────

    private static int SpectatorGameOver()
    {
        Console.WriteLine("  MC3.2 Spectator GameOver");
        Items.Reset(); Items.RegisterDefaults();
        const int N    = 10;
        ushort    port = PickPort();
        ulong     seed = 0xC0_DE_CA_FEUL;

        var host = new RoomHost(roomId: 601, seed: seed, port: port);
        var sims = StartRoom(host, port, N);

        // Connect spectator mid-game.
        Pump(host, sims, null, ms: 400);

        var spec = new NetClient();
        spec.Connect("127.0.0.1", port);
        long tConn = Environment.TickCount64;
        while (!spec.Connected)
        {
            host.Pump(); spec.Tick();
            if (Environment.TickCount64 - tConn > 2000) throw new Exception("spec connect timeout");
            Thread.Sleep(2);
        }
        spec.SendSpectateRoom(host.Room.RoomId);

        // Let spectator register + receive snapshot.
        Pump(host, sims, spec, ms: 200);

        // Force Blue victory via direct crystal mutation (same as MSV2.1 / MC2.1).
        host.World.Crystals[(int)Team.Red].Hp    = (Fix64)0;
        host.World.Crystals[(int)Team.Red].Alive = false;

        // Pump until GameOver broadcast reaches spectator.
        Pump(host, sims, spec, ms: 800);

        bool specEnded = spec.MatchEnded;
        bool specBlue  = spec.MatchWinner == (byte)Team.Blue;
        Console.WriteLine($"    spectator matchEnded={specEnded} winner={(Team)spec.MatchWinner}");

        bool ok = specEnded && specBlue;
        Console.WriteLine(ok ? "  MC3.2 OK" : "  MC3.2 FAIL");

        foreach (var s in sims) s.Net.Disconnect();
        spec.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }
}
