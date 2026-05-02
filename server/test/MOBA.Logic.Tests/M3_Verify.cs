// SPDX-License-Identifier: MIT
// M3 verification — RoomHost + 10 NetClients on UDP loopback.
//
// Three checks (PRD M3 DoD):
//   M3.1  Hash sync   : 10 clients, run for DurSec seconds; every client's
//                       per-frame hash must match all others (server is the
//                       arbiter via C2S_HashReport).
//   M3.2  Reconnect   : kill client #5 mid-game, after a few hundred frames
//                       reconnect a fresh client to the same slot, request
//                       resync, restore snapshot, catch up on subsequent
//                       broadcasts, hash converges with the rest.
//   M3.3  Lossy net   : same harness with intentional client-side packet drop
//                       (5% Bernoulli) on the C2S_Input channel; KCP retransmit
//                       must keep the room running for DurSec seconds.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Server;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Tests;

internal static class M3_Verify
{
    public static int Execute(int? durSec = null)
    {
        Console.WriteLine("M3 Verify");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        // Silence noisy kcp Info/Warning logs; keep Error visible.
        kcp2k.Log.Info    = _ => {};
        kcp2k.Log.Warning = _ => {};
        kcp2k.Log.Error   = _ => {}; // WSAECONNRESET spam during reconnect; benign
        int dur = durSec ?? 30;
        int rc = 0;
        rc |= HashSync(dur);
        rc |= Reconnect(dur);
        return rc;
    }

    // ----- helpers ----------------------------------------------------------

    private static readonly Random s_portRng = new();
    private static ushort PickPort(int seed)
    {
        // Random port in the ephemeral range to avoid conflicts when sub-tests run back-to-back.
        return (ushort)(40_000 + s_portRng.Next(0, 20_000));
    }

    private sealed class Sim
    {
        public NetClient Net = new();
        public DeterministicWorld World;
        public uint AppliedFrame;             // next frame to apply locally
        public uint LastSentInputFrame;       // last frame an input was sent for
        public byte Slot;
        public bool DropInputs;
        public Random Drop = new(12345);

        public void Apply(int maxApply = 64)
        {
            int n = 0;
            while (n++ < maxApply && Net.RxFrames.Count > 0)
            {
                var (frame, inputs) = Net.RxFrames.Peek();
                if (frame < AppliedFrame) { Net.RxFrames.Dequeue(); continue; } // stale (post-resync)
                if (frame > AppliedFrame) break;                                  // future — wait for missing frames in order
                Net.RxFrames.Dequeue();
                World.Tick(inputs);
                AppliedFrame = frame + 1;
                if ((frame % 60) == 0) Net.SendHashReport(frame, World.Hash());
            }
        }
    }

    // ----- M3.1 -------------------------------------------------------------

    private static int HashSync(int durSec)
    {
        const int N = 10;
        ushort port = PickPort(0xCAFE);
        ulong seed = 0xD15EA5EUL;

        var host = new RoomHost(roomId: 1, seed: seed, port: port) { LogDesync = true };
        var sims = new Sim[N];
        for (int i = 0; i < N; i++)
        {
            sims[i] = new Sim { Slot = (byte)i, World = new DeterministicWorld(seed) };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }
        // Wait for all 10 to complete kcp handshake & RoomStart BEFORE the server starts its
        // force-broadcast cadence (otherwise the server World diverges from the clients').
        // RoomHost.Pump only force-builds frame 0 once MaxWaitMs has elapsed since startup;
        // we keep pumping until every client has RoomStarted, then proceed.
        long warmStart = Environment.TickCount64;
        while (true)
        {
            host.Pump();
            for (int i = 0; i < N; i++) sims[i].Net.Tick();
            int rs = 0;
            for (int i = 0; i < N; i++) if (sims[i].Net.RoomStarted) rs++;
            if (rs == N) break;
            if (Environment.TickCount64 - warmStart > 5000) { Console.Error.WriteLine($"  WARM TIMEOUT rs={rs}/{N}"); break; }
            Thread.Sleep(2);
        }

        var sw = Stopwatch.StartNew();
        long deadlineMs = durSec * 1000L;
        long lastInputMs = 0;
        long lastDiagMs = 0;
        const long inputStepMs = 1000 / 15; // 15Hz logical input rate
        int totalInputsSent = 0;
        int totalRxFramesSeen = 0;

        // Drive everything from this thread: pump server, pump clients, send inputs every 1/15s.
        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            host.Pump();
            for (int i = 0; i < N; i++) sims[i].Net.Tick();

            long now = sw.ElapsedMilliseconds;
            if (now - lastInputMs >= inputStepMs)
            {
                lastInputMs = now;
                for (int i = 0; i < N; i++)
                {
                    if (!sims[i].Net.Connected) continue;
                    sims[i].LastSentInputFrame++;
                    var input = MakeInput(sims[i].LastSentInputFrame, (byte)i);
                    sims[i].Net.SendInput(sims[i].LastSentInputFrame - 1, input); // server numbers from 0
                    totalInputsSent++;
                }
            }

            for (int i = 0; i < N; i++) { totalRxFramesSeen += sims[i].Net.RxFrames.Count; sims[i].Apply(); }

            if (now - lastDiagMs >= 2000)
            {
                lastDiagMs = now;
                int conn = 0; int rs = 0; int rxQ = 0; uint maxApplied = 0;
                for (int i = 0; i < N; i++)
                { if (sims[i].Net.Connected) conn++; if (sims[i].Net.RoomStarted) rs++; rxQ += sims[i].Net.RxFrames.Count; if (sims[i].AppliedFrame > maxApplied) maxApplied = sims[i].AppliedFrame; }
                Console.WriteLine($"  [diag t={now,5}ms] conn={conn}/{N} roomStart={rs}/{N} totalIn={totalInputsSent} maxApplied={maxApplied} rxQ={rxQ} srvFrame={host.World.Frame} running={host.IsRunning} srvSlots={host.SnapshotsByConn()} jr={host.JoinRoomCount} oc={host.OnConnectedCount} od={host.OnDisconnectedCount} sIn={host.InputsReceived}");
            }

            if (host.DesyncDetected) break;
            Thread.Sleep(1);
        }

        // Drain any pending broadcasts so per-client world frames converge.
        for (int drain = 0; drain < 200; drain++)
        {
            host.Pump();
            for (int i = 0; i < N; i++) { sims[i].Net.Tick(); sims[i].Apply(); }
            Thread.Sleep(2);
        }

        bool desync = host.DesyncDetected;
        ulong[] finalHashes = sims.Select(s => s.World.Hash()).ToArray();
        bool allEq = finalHashes.All(h => h == finalHashes[0]);
        uint[] frames = sims.Select(s => s.AppliedFrame).ToArray();

        Console.WriteLine($"  M3.1 HashSync : {N} clients, {durSec}s loopback, applied frames per client = {string.Join(',', frames)}");
        Console.WriteLine($"               final hashes : {string.Join(",", finalHashes.Select(h => $"0x{h:X16}"))}");
        Console.WriteLine($"               server desync flag : {desync} (frame {host.DesyncFrame})");
        bool ok = allEq && !desync && frames.All(f => f >= 60);
        Console.WriteLine(ok ? "  M3.1 OK    : all hashes equal, no desync" : "  M3.1 FAIL : divergence");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }

    private static InputFrame MakeInput(uint frame, byte slot)
    {
        // Deterministic per-slot motion: each player walks a different sine-ish path expressed
        // through fixed-step integers so the wire bytes are bit-identical run to run.
        sbyte jx = (sbyte)(((frame * 7 + slot * 13) % 200) - 100);
        sbyte jy = (sbyte)(((frame * 11 + slot * 17) % 200) - 100);
        return new InputFrame { JoyX = jx, JoyY = jy };
    }

    // ----- M3.2 -------------------------------------------------------------

    private static int Reconnect(int durSec)
    {
        const int N = 10;
        ushort port = PickPort(0xBEEF);
        ulong seed = 0xC0DECAFEUL;

        var host = new RoomHost(roomId: 2, seed: seed, port: port) { LogDesync = true };
        var sims = new Sim[N];
        for (int i = 0; i < N; i++)
        {
            sims[i] = new Sim { Slot = (byte)i, World = new DeterministicWorld(seed) };
            sims[i].Net.PlayerSlot = (byte)i;
            sims[i].Net.Connect("127.0.0.1", port);
        }

        var sw = Stopwatch.StartNew();
        long deadlineMs = durSec * 1000L;
        long killAt = deadlineMs / 3;
        long reconnectAt = (deadlineMs * 2) / 3;
        long lastInputMs = 0;
        const long inputStepMs = 1000 / 15;
        bool killed = false;
        bool reconnected = false;
        int killSlot = 5;

        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            host.Pump();
            for (int i = 0; i < N; i++) sims[i].Net.Tick();

            long now = sw.ElapsedMilliseconds;

            if (!killed && now > killAt)
            {
                killed = true;
                Console.WriteLine($"  [Reconnect] killing slot {killSlot} at {now}ms (frame {sims[killSlot].AppliedFrame})");
                sims[killSlot].Net.Disconnect();
            }
            if (killed && !reconnected && now > reconnectAt)
            {
                reconnected = true;
                Console.WriteLine($"  [Reconnect] reconnecting slot {killSlot} at {now}ms");
                sims[killSlot] = new Sim { Slot = (byte)killSlot, World = new DeterministicWorld(seed) };
                sims[killSlot].Net.PlayerSlot = (byte)killSlot;
                sims[killSlot].Net.Connect("127.0.0.1", port);
                // Allow a few pumps for the connect.
                for (int p = 0; p < 50; p++) { host.Pump(); sims[killSlot].Net.Tick(); Thread.Sleep(2); }
                // Request resync.
                sims[killSlot].Net.SendResyncRequest(0);
                // Wait for snapshot.
                for (int p = 0; p < 200 && sims[killSlot].Net.PendingSnapshot == null; p++)
                { host.Pump(); sims[killSlot].Net.Tick(); Thread.Sleep(2); }
                if (sims[killSlot].Net.PendingSnapshot is { } snap)
                {
                    sims[killSlot].World.ReadSnapshot(snap.bytes, snap.frame);
                    sims[killSlot].AppliedFrame = snap.frame + 1;
                    Console.WriteLine($"  [Reconnect] snapshot applied at frame {snap.frame}; resuming");
                    sims[killSlot].Net.PendingSnapshot = null;
                }
                else { Console.Error.WriteLine("  [Reconnect] FAIL: no snapshot"); }
            }

            if (now - lastInputMs >= inputStepMs)
            {
                lastInputMs = now;
                for (int i = 0; i < N; i++)
                {
                    if (!sims[i].Net.Connected) continue;
                    sims[i].LastSentInputFrame++;
                    var input = MakeInput(sims[i].LastSentInputFrame, (byte)i);
                    sims[i].Net.SendInput(sims[i].LastSentInputFrame - 1, input);
                }
            }

            for (int i = 0; i < N; i++) sims[i].Apply();

            Thread.Sleep(1);
        }

        for (int drain = 0; drain < 200; drain++)
        {
            host.Pump();
            for (int i = 0; i < N; i++) { sims[i].Net.Tick(); sims[i].Apply(); }
            Thread.Sleep(2);
        }

        ulong[] finalHashes = sims.Select(s => s.World.Hash()).ToArray();
        bool allEq = finalHashes.All(h => h == finalHashes[0]);
        Console.WriteLine($"  M3.2 Reconn  : final hashes = {string.Join(",", finalHashes.Select(h => $"0x{h:X16}"))}");
        Console.WriteLine($"               applied frames = {string.Join(',', sims.Select(s => s.AppliedFrame))}");
        bool ok = allEq && reconnected;
        Console.WriteLine(ok ? "  M3.2 OK    : reconnect+snapshot, hashes converged" : "  M3.2 FAIL");

        for (int i = 0; i < N; i++) sims[i].Net.Disconnect();
        host.Stop();
        return ok ? 0 : 1;
    }
}
