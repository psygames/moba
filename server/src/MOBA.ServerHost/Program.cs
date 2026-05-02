// SPDX-License-Identifier: MIT
// MOBA.ServerHost — minimal multi-room dedicated server entry point.
//
// Args:
//   --port <basePort>     base UDP port (default 7777). Room N listens on basePort+N.
//   --rooms <count>       number of rooms (default 1).
//   --seed <ulong>        PRNG seed (default 0xDEADBEEFCAFEBABE).
//   --replay <dir>        if set, each room writes <dir>/room-<id>.mreplay on shutdown.
//   --log-desync          enable per-frame DESYNC stderr logs.
//   --metrics-port <n>    if non-zero, expose Prometheus-style /metrics over HTTP (default 0=off).
//
// SIGINT (Ctrl+C) triggers graceful Stop(), which finalises replay files.
//
// appsettings.json (next to the exe) provides default values for all flags above.
// CLI args take precedence over appsettings values.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using MOBA.Server;

namespace MOBA.ServerHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Load appsettings.json defaults (CLI args override).
        LoadAppSettings(out ushort cfgPort, out int cfgRooms, out ulong cfgSeed,
                        out string cfgReplayDir, out bool cfgLogDesync, out ushort cfgMetricsPort);

        ushort basePort    = ParseUShort(args, "--port", cfgPort);
        int    roomCount   = (int)ParseULong(args, "--rooms", (ulong)cfgRooms);
        ulong  seed        = ParseULong(args, "--seed", cfgSeed);
        string replayDir   = ParseString(args, "--replay", cfgReplayDir);
        bool   logDesync   = HasFlag(args, "--log-desync") || cfgLogDesync;
        ushort metricsPort = ParseUShort(args, "--metrics-port", cfgMetricsPort);

        if (roomCount <= 0 || roomCount > 256)
        {
            Console.Error.WriteLine("--rooms must be 1..256");
            return 2;
        }

        var hosts = new List<RoomHost>(roomCount);
        for (uint i = 0; i < roomCount; i++)
        {
            ushort port = (ushort)(basePort + i);
            var host = new RoomHost(roomId: i, seed: seed ^ i, port: port)
            {
                LogDesync = logDesync,
                AutoResyncOnDesync = true,
                ReplayPath = replayDir != null ? System.IO.Path.Combine(replayDir, $"room-{i}.mreplay") : null,
            };
            hosts.Add(host);
            Console.Out.WriteLine($"[ServerHost] room {i} listening on udp/{port}");
        }

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var pumpThread = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                for (int i = 0; i < hosts.Count; i++) hosts[i].Pump();
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "moba-pump" };
        pumpThread.Start();

        HttpListener metrics = null;
        if (metricsPort != 0)
        {
            metrics = StartMetrics(metricsPort, hosts, stop);
            Console.Out.WriteLine($"[ServerHost] metrics on http://localhost:{metricsPort}/metrics");
        }

        Console.Out.WriteLine($"[ServerHost] {hosts.Count} room(s) up; Ctrl+C to stop.");
        stop.Wait();
        Console.Out.WriteLine("[ServerHost] stopping…");
        try { metrics?.Stop(); } catch { /* best-effort */ }
        for (int i = 0; i < hosts.Count; i++) hosts[i].Stop();
        pumpThread.Join(TimeSpan.FromSeconds(2));
        Console.Out.WriteLine("[ServerHost] bye.");
        return 0;
    }

    /// <summary>Spawns a tiny background HTTP listener that serves Prometheus-style metrics.
    /// Single endpoint <c>/metrics</c>; everything else returns 404.</summary>
    private static HttpListener StartMetrics(ushort port, List<RoomHost> hosts, ManualResetEventSlim stop)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        new Thread(() =>
        {
            while (!stop.IsSet && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { return; }
                try
                {
                    if (ctx.Request.Url?.AbsolutePath != "/metrics")
                    {
                        ctx.Response.StatusCode = 404; ctx.Response.Close(); continue;
                    }
                    var body = Encoding.UTF8.GetBytes(RenderMetrics(hosts));
                    ctx.Response.ContentType = "text/plain; version=0.0.4";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                    ctx.Response.Close();
                }
                catch (Exception e) { Console.Error.WriteLine($"[metrics] {e.Message}"); }
            }
        }) { IsBackground = true, Name = "moba-metrics" }.Start();
        return listener;
    }

    private static string RenderMetrics(List<RoomHost> hosts)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("# HELP moba_rooms_total Number of configured rooms.");
        sb.AppendLine("# TYPE moba_rooms_total gauge");
        sb.Append("moba_rooms_total ").Append(hosts.Count).AppendLine();

        AppendCounter(sb, "moba_inputs_received_total", "C2S inputs received per room.", hosts, h => h.InputsReceived);
        AppendCounter(sb, "moba_join_room_total",       "JoinRoom messages received.",   hosts, h => h.JoinRoomCount);
        AppendCounter(sb, "moba_connections_total",     "Kcp connections opened.",       hosts, h => h.OnConnectedCount);
        AppendCounter(sb, "moba_disconnections_total",  "Kcp connections closed.",       hosts, h => h.OnDisconnectedCount);
        AppendCounter(sb, "moba_buyitem_received_total","C2S BuyItem requests received.",hosts, h => (long)h.Room.BuyItemRequestsReceived);
        AppendCounter(sb, "moba_buyitem_injected_total","BuyItem requests injected into broadcast.", hosts, h => (long)h.Room.BuyItemRequestsInjected);
        AppendCounter(sb, "moba_auto_resync_total",     "Auto-resync snapshots pushed.", hosts, h => h.AutoResyncCount);
        AppendCounter(sb, "moba_resync_throttled_total","Resync requests dropped due to throttle.", hosts, h => h.ResyncThrottled);
        AppendCounter(sb, "moba_gameover_total",        "Game over broadcasts.",         hosts, h => h.GameOverBroadcasts);
        AppendGauge  (sb, "moba_match_ended",           "1 if the room's match has ended, else 0.", hosts, h => h.MatchEnded ? 1 : 0);
        AppendGauge  (sb, "moba_world_frame",           "Current authoritative World frame.", hosts, h => (long)h.World.Frame);
        return sb.ToString();
    }

    private static void AppendCounter(StringBuilder sb, string name, string help, List<RoomHost> hosts, Func<RoomHost, long> sel)
    {
        sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        sb.Append("# TYPE ").Append(name).AppendLine(" counter");
        for (int i = 0; i < hosts.Count; i++)
            sb.Append(name).Append("{room=\"").Append(i).Append("\"} ").Append(sel(hosts[i])).AppendLine();
    }

    private static void AppendGauge(StringBuilder sb, string name, string help, List<RoomHost> hosts, Func<RoomHost, long> sel)
    {
        sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        sb.Append("# TYPE ").Append(name).AppendLine(" gauge");
        for (int i = 0; i < hosts.Count; i++)
            sb.Append(name).Append("{room=\"").Append(i).Append("\"} ").Append(sel(hosts[i])).AppendLine();
    }

    private static bool HasFlag(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++) if (args[i] == name) return true;
        return false;
    }

    private static string ParseString(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static ulong ParseULong(string[] args, string name, ulong fallback)
    {
        var s = ParseString(args, name, null);
        if (s == null) return fallback;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber);
        return ulong.Parse(s);
    }

    private static ushort ParseUShort(string[] args, string name, ushort fallback)
        => (ushort)ParseULong(args, name, fallback);

    /// <summary>Load appsettings.json from the executable directory (if present).
    /// Silently ignored when the file is absent or malformed.</summary>
    private static void LoadAppSettings(
        out ushort port, out int rooms, out ulong seed,
        out string replayDir, out bool logDesync, out ushort metricsPort)
    {
        // Defaults (also the hard-coded fallbacks when no file exists).
        port        = 7777;
        rooms       = 1;
        seed        = 0xDEADBEEFCAFEBABEUL;
        replayDir   = null;
        logDesync   = false;
        metricsPort = 0;

        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Server", out var srv)) return;

            if (srv.TryGetProperty("BasePort", out var p) && p.TryGetUInt16(out ushort pp))
                port = pp;
            if (srv.TryGetProperty("MaxRooms", out var r) && r.TryGetInt32(out int rr))
                rooms = rr;
            if (srv.TryGetProperty("Seed", out var s))
            {
                string sv = s.GetString();
                if (sv != null && ulong.TryParse(sv, out ulong su)) seed = su;
            }
            if (srv.TryGetProperty("ReplayDir", out var rd) && rd.ValueKind == JsonValueKind.String)
                replayDir = rd.GetString();
            if (srv.TryGetProperty("LogDesync", out var ld) && ld.ValueKind == JsonValueKind.True)
                logDesync = true;
            if (srv.TryGetProperty("MetricsPort", out var mp) && mp.TryGetUInt16(out ushort mpp))
                metricsPort = mpp;
        }
        catch
        {
            // Best-effort: ignore parse errors.
        }
    }
}
