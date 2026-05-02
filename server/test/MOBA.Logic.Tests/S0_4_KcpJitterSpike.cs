// SPDX-License-Identifier: MIT
// Milestone 0 — Spike S0.4
// 10-client KCP jitter test (loopback).
//
// PRD §11/M0 DoD:  300 s sustained run with 10 clients, ~100 ms RTT mean and
// 50 ms jitter, 5 % loss, no client drops, average input latency below 200 ms.
//
// Topology:
//   client_i  --UDP-->  UdpJitterProxy  --UDP-->  KcpServer (echo)
// The proxy applies independent loss + delay per direction. KCP's own RTT
// estimator + retransmit must keep all 10 sessions alive for 5 minutes.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using kcp2k;

namespace MOBA.Logic.Tests;

internal static class S0_4_KcpJitterSpike
{
    private const int    ClientCount      = 10;
    private const ushort ServerPort       = 27700;
    private const ushort ProxyPort        = 27710;
    private const int    InputHz          = 15;            // PRD lockstep tick
    private const int    DurationSeconds  = 300;           // PRD: 5 minutes
    private const int    DelayMeanMs      = 100;
    private const int    DelayJitterMs    = 50;
    private const double LossRate         = 0.05;
    private const int    PayloadBytes     = 32;            // input frame

    public static int Execute(int? durationOverrideSec = null)
    {
        int durationSec = durationOverrideSec ?? DurationSeconds;
        Console.WriteLine($"S0.4 KCP Jitter Spike: 10 clients, 100±50ms / 5% loss / {durationSec}s");
        Console.WriteLine($"  Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

        // Quiet down kcp2k so the harness is readable; re-enable on error.
        Log.Info    = _ => { };
        Log.Warning = _ => { };
        Log.Error   = msg => Console.Error.WriteLine("[KCP-ERR] " + msg);

        var cfg = new KcpConfig(
            DualMode:        false,
            NoDelay:         true,
            Interval:        10,
            FastResend:      2,
            CongestionWindow:false,
            SendWindowSize:  256,
            ReceiveWindowSize: 256,
            Timeout:         15_000,
            MaxRetransmits:  Kcp.DEADLINK);

        // ---- echo server ----
        int connectedCount = 0, disconnectedCount = 0;
        long serverEchoes = 0;
        KcpServer server = null!;
        server = new KcpServer(
            OnConnected:    cid => Interlocked.Increment(ref connectedCount),
            OnData:         (cid, data, ch) =>
            {
                Interlocked.Increment(ref serverEchoes);
                server.Send(cid, data, ch); // echo so client can compute RTT
            },
            OnDisconnected: cid => Interlocked.Increment(ref disconnectedCount),
            OnError:        (cid, err, msg) => Console.Error.WriteLine($"[SRV-ERR] {cid} {err} {msg}"),
            config:         cfg);
        server.Start(ServerPort);

        // ---- proxy ----
        using var proxy = new UdpJitterProxy(
            listenPort:   ProxyPort,
            upstreamEP:   new IPEndPoint(IPAddress.Loopback, ServerPort),
            meanMs:       DelayMeanMs,
            jitterMs:     DelayJitterMs,
            lossRate:     LossRate,
            seed:         unchecked((int)0xC0FFEEDEADUL));
        proxy.Start();

        // ---- 10 clients ----
        var sw = Stopwatch.StartNew();
        var clients = new ClientHarness[ClientCount];
        for (int i = 0; i < ClientCount; i++)
        {
            int idx = i;
            clients[i] = new ClientHarness(idx, cfg, sw);
            clients[i].Connect("127.0.0.1", ProxyPort);
        }

        // ---- main pump loop ----
        long durationMs   = durationSec * 1000L;
        long stopSendingMs = durationMs - 1500L;  // give in-flight inputs time to round-trip
        long sendIntervalMs = 1000L / InputHz;
        var nextStatMs = sw.ElapsedMilliseconds + 10_000L;
        int tick = 0;
        while (sw.ElapsedMilliseconds < durationMs)
        {
            long nowMs = sw.ElapsedMilliseconds;
            server.Tick();
            for (int i = 0; i < clients.Length; i++) clients[i].Tick(nowMs);

            if (nowMs < stopSendingMs)
                for (int i = 0; i < clients.Length; i++) clients[i].MaybeSend(nowMs, sendIntervalMs);

            if (nowMs >= nextStatMs)
            {
                nextStatMs += 10_000L;
                long sentTot = 0, recvTot = 0; double rttSum = 0; long rttN = 0; int alive = 0;
                for (int i = 0; i < clients.Length; i++)
                {
                    var c = clients[i];
                    sentTot += c.Sent; recvTot += c.Received;
                    rttSum  += c.RttSumMs; rttN += c.RttSamples;
                    if (c.Connected) alive++;
                }
                double avgRtt = rttN > 0 ? rttSum / rttN : 0;
                Console.WriteLine($"  t={sw.Elapsed.TotalSeconds,6:F1}s  alive={alive}/{ClientCount}  sent={sentTot,7}  recv={recvTot,7}  loss={(sentTot==0?0:1.0 - (double)recvTot/sentTot)*100:F2}%  avgRTT={avgRtt:F1}ms");
            }

            Thread.Sleep(2);
            tick++;
        }

        // ---- shutdown ----
        int disconnectedDuringRun = disconnectedCount;
        for (int i = 0; i < clients.Length; i++) clients[i].BeginShutdown();
        for (int i = 0; i < clients.Length; i++) clients[i].Disconnect();
        // Pump a few more ticks to deliver disconnect packets.
        var swEnd = Stopwatch.StartNew();
        while (swEnd.ElapsedMilliseconds < 500)
        {
            server.Tick();
            for (int i = 0; i < clients.Length; i++) clients[i].Tick(sw.ElapsedMilliseconds);
            Thread.Sleep(5);
        }
        server.Stop();
        proxy.Stop();

        // ---- final stats ----
        long totalSent = 0, totalRecv = 0; double totalRttSum = 0; long totalRttN = 0;
        int alive2 = 0; double maxRttMax = 0;
        for (int i = 0; i < clients.Length; i++)
        {
            var c = clients[i];
            totalSent += c.Sent; totalRecv += c.Received;
            totalRttSum += c.RttSumMs; totalRttN += c.RttSamples;
            if (c.MaxRttMs > maxRttMax) maxRttMax = c.MaxRttMs;
            // Alive == it never observed an OnDisconnected during the run.
            if (!c.WasDisconnectedDuringRun) alive2++;
        }
        double avgRttFinal = totalRttN > 0 ? totalRttSum / totalRttN : 0;
        double avgOneWayMs = avgRttFinal / 2.0;
        double measuredLoss = totalSent == 0 ? 0 : 1.0 - (double)totalRecv / totalSent;

        Console.WriteLine();
        Console.WriteLine("S0.4 RESULT");
        Console.WriteLine($"  Duration         : {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Clients alive    : {alive2}/{ClientCount}  (server-side connected={connectedCount}, disconnected-during-run={disconnectedDuringRun})");
        Console.WriteLine($"  Inputs sent      : {totalSent}");
        Console.WriteLine($"  Echoes received  : {totalRecv}  (KCP-app-level loss={measuredLoss*100:F3}% — should be ~0 because KCP is reliable)");
        Console.WriteLine($"  Avg RTT          : {avgRttFinal:F2} ms");
        Console.WriteLine($"  Avg one-way      : {avgOneWayMs:F2} ms  (DoD: avg input latency < 200 ms)");
        Console.WriteLine($"  Max RTT          : {maxRttMax:F1} ms");
        Console.WriteLine($"  Proxy stats      : sent={proxy.Forwarded} dropped={proxy.Dropped} ({(proxy.Forwarded+proxy.Dropped == 0 ? 0 : (double)proxy.Dropped / (proxy.Forwarded + proxy.Dropped) * 100):F2}% raw drop)");

        bool dodPass =
            alive2 == ClientCount &&
            measuredLoss < 0.001 &&     // KCP must deliver everything (reliable channel)
            avgOneWayMs < 200.0 &&
            disconnectedDuringRun == 0;
        Console.WriteLine(dodPass ? "  RESULT     : OK (DoD met)" : "  RESULT     : FAIL (DoD violated)");
        return dodPass ? 0 : 1;
    }

    // -----------------------------------------------------------------------

    private sealed class ClientHarness
    {
        public readonly int Id;
        private readonly KcpClient _client;
        private readonly Stopwatch _sw;
        private long _nextSendMs = 0;
        private uint _seq = 0;

        public long Sent;
        public long Received;
        public double RttSumMs;
        public long RttSamples;
        public double MaxRttMs;
        public bool Connected => _client.connected;
        public bool WasDisconnectedDuringRun;
        private bool _shuttingDown;
        public void BeginShutdown() => _shuttingDown = true;

        public ClientHarness(int id, KcpConfig cfg, Stopwatch sharedClock)
        {
            Id = id;
            _sw = sharedClock;
            _client = new KcpClient(
                OnConnected:    () => { },
                OnData:         OnData,
                OnDisconnected: () => { if (!_shuttingDown) WasDisconnectedDuringRun = true; },
                OnError:        (e, m) => Console.Error.WriteLine($"[CLI{id}-ERR] {e} {m}"),
                config:         cfg);
        }

        public void Connect(string host, ushort port) => _client.Connect(host, port);
        public void Disconnect() => _client.Disconnect();
        public void Tick(long nowMs) => _client.Tick();

        public void MaybeSend(long nowMs, long intervalMs)
        {
            if (!_client.connected) return;
            if (nowMs < _nextSendMs) return;
            _nextSendMs = nowMs + intervalMs;

            var buf = new byte[PayloadBytes];
            uint seq = ++_seq;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0,  4), Id);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4,  4), seq);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8,  8), nowMs);
            _client.Send(new ArraySegment<byte>(buf), KcpChannel.Reliable);
            Interlocked.Increment(ref Sent);
        }

        private void OnData(ArraySegment<byte> data, KcpChannel channel)
        {
            if (data.Count < 16) return;
            long sentAt = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan().Slice(8, 8));
            long now    = _sw.ElapsedMilliseconds;
            double rtt  = now - sentAt;
            if (rtt < 0) rtt = 0;
            RttSumMs += rtt;
            RttSamples++;
            if (rtt > MaxRttMs) MaxRttMs = rtt;
            Interlocked.Increment(ref Received);
        }
    }

    /// <summary>UDP loss + delay proxy. Single listen socket, single upstream socket.
    /// Each direction has an independent delay queue.</summary>
    public sealed class UdpJitterProxy : IDisposable
    {
        private readonly ushort _listenPort;
        private readonly IPEndPoint _upstreamEP;
        private readonly int _meanMs, _jitterMs;
        private readonly double _lossRate;
        private readonly Random _rng;
        private Socket _listenSock;
        private CancellationTokenSource _cts;

        // Per-client upstream socket: server replies on each upstream socket's
        // local endpoint, so we map back to the originating client EP.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, Socket> _upPerClient = new();

        public long Forwarded;
        public long Dropped;

        // Delay scheduler (one shared min-heap protected by lock).
        private readonly object _qLock = new();
        private readonly PriorityQueue<DelayedPacket, long> _q = new();
        private readonly AutoResetEvent _qSignal = new(false);
        private Stopwatch _clock;

        private struct DelayedPacket
        {
            public byte[] Data;
            public Socket ViaSock;
            public EndPoint Dest;
        }

        public UdpJitterProxy(ushort listenPort, IPEndPoint upstreamEP, int meanMs, int jitterMs, double lossRate, int seed)
        {
            _listenPort = listenPort;
            _upstreamEP = upstreamEP;
            _meanMs = meanMs;
            _jitterMs = jitterMs;
            _lossRate = lossRate;
            _rng = new Random(seed);
        }

        public void Start()
        {
            _clock = Stopwatch.StartNew();
            _cts = new CancellationTokenSource();
            _listenSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _listenSock.Bind(new IPEndPoint(IPAddress.Loopback, _listenPort));
            // Disable ICMP-port-unreachable causing receive failure on Windows.
            try { const int SIO_UDP_CONNRESET = -1744830452; _listenSock.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch {}

            Task.Run(() => RecvFromClientsLoop(_cts.Token));
            Task.Run(() => SchedulerLoop(_cts.Token));
        }

        public void Stop() { _cts?.Cancel(); _qSignal.Set(); try { _listenSock?.Close(); } catch {} foreach (var s in _upPerClient.Values) { try { s.Close(); } catch {} } }
        public void Dispose() => Stop();

        private async Task RecvFromClientsLoop(CancellationToken ct)
        {
            var buf = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int n = _listenSock.ReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref any);
                    var clientEP = (IPEndPoint)any;
                    var copy = new byte[n]; Buffer.BlockCopy(buf, 0, copy, 0, n);

                    var up = _upPerClient.GetOrAdd(clientEP, ep =>
                    {
                        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                        try { const int SIO_UDP_CONNRESET = -1744830452; s.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch {}
                        Task.Run(() => RecvFromServerLoop(s, ep, _cts.Token));
                        return s;
                    });

                    Schedule(copy, up, _upstreamEP);
                }
                catch (SocketException) { if (ct.IsCancellationRequested) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void RecvFromServerLoop(Socket up, IPEndPoint clientEP, CancellationToken ct)
        {
            var buf = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int n = up.ReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref any);
                    var copy = new byte[n]; Buffer.BlockCopy(buf, 0, copy, 0, n);
                    Schedule(copy, _listenSock, clientEP);
                }
                catch (SocketException) { if (ct.IsCancellationRequested) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void Schedule(byte[] data, Socket via, EndPoint dest)
        {
            int delay;
            lock (_rng)
            {
                if (_rng.NextDouble() < _lossRate)
                {
                    Interlocked.Increment(ref Dropped);
                    return;
                }
                // Triangular jitter approx: sum of two uniforms ~ bell-shape.
                int u1 = _rng.Next(-_jitterMs, _jitterMs + 1);
                int u2 = _rng.Next(-_jitterMs, _jitterMs + 1);
                delay = _meanMs + (u1 + u2) / 2;
                if (delay < 0) delay = 0;
            }
            long when = _clock.ElapsedMilliseconds + delay;
            lock (_qLock) _q.Enqueue(new DelayedPacket { Data = data, ViaSock = via, Dest = dest }, when);
            _qSignal.Set();
        }

        private void SchedulerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                long now = _clock.ElapsedMilliseconds;
                int waitMs = 5;
                while (true)
                {
                    DelayedPacket pkt; long when;
                    lock (_qLock)
                    {
                        if (!_q.TryPeek(out pkt, out when)) { waitMs = 5; break; }
                        if (when > now) { waitMs = (int)Math.Min(when - now, 5); break; }
                        _q.Dequeue();
                    }
                    try { pkt.ViaSock.SendTo(pkt.Data, pkt.Dest); Interlocked.Increment(ref Forwarded); }
                    catch { /* socket closed */ }
                }
                _qSignal.WaitOne(waitMs);
            }
        }
    }
}
