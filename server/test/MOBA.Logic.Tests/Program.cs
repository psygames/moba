// SPDX-License-Identifier: MIT
// Test runner entry point. Picks one spike to run by CLI arg or env var, defaults to all.
using System;

namespace MOBA.Logic.Tests;

internal static partial class Program
{
    public static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        // CI-fast mode: skips 300s S0.4, KCP-dependent M3/MSV/MC suites.
        if (which is "ci") return RunCi(args);
        int rc = 0;
        if (which is "all" or "s0.1" or "s0_1" or "1")
        {
            rc |= S0_1_DeterminismSpike.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "s0.2" or "s0_2" or "2")
        {
            rc |= S0_2_PhysicsSpike.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "s0.3" or "s0_3" or "3")
        {
            rc |= S0_3_SnapshotSpike.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "s0.4" or "s0_4" or "4")
        {
            int? dur = null;
            if (args.Length > 1 && int.TryParse(args[1], out int d)) dur = d;
            rc |= S0_4_KcpJitterSpike.Execute(dur);
            Console.WriteLine();
        }
        if (which is "all" or "m1" or "m1.0")
        {
            rc |= M1_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m2" or "m2.0")
        {
            rc |= M2_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m3" or "m3.0")
        {
            int? dur = null;
            if (args.Length > 1 && int.TryParse(args[1], out int d)) dur = d;
            rc |= M3_Verify.Execute(dur);
            Console.WriteLine();
        }
        if (which is "all" or "m4" or "m4.0")
        {
            int? dur = null;
            if (args.Length > 1 && int.TryParse(args[1], out int d)) dur = d;
            rc |= M4_Verify.Execute(dur);
            Console.WriteLine();
        }
        if (which is "all" or "m5.1" or "m5_1")
        {
            rc |= M5_1_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.2" or "m5_2")
        {
            rc |= M5_2_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.3" or "m5_3")
        {
            rc |= M5_3_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.4" or "m5_4")
        {
            rc |= M5_4_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.5" or "m5_5")
        {
            rc |= M5_5_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.6" or "m5_6")
        {
            rc |= M5_6_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m5.7" or "m5_7")
        {
            rc |= M5_7_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "msv1" or "msv1.0" or "mserver1")
        {
            rc |= MServer1_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "msv2" or "msv2.0" or "mserver2")
        {
            rc |= MServer2_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "mc2" or "mc2.0" or "mclient2")
        {
            rc |= MClient2_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "msv3" or "msv3.0" or "mserver3")
        {
            rc |= MServer3_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "mc3" or "mc3.0" or "mclient3")
        {
            rc |= MClient3_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "mconfig" or "mc")
        {
            rc |= MConfig_Verify.Run(which);
            Console.WriteLine();
        }
        if (which is "all" or "m6" or "m6.0")
        {
            rc |= M6_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m7" or "m7.0")
        {
            rc |= M7_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m8" or "m8.0")
        {
            rc |= M8_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m9" or "m9.0")
        {
            rc |= M9_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m10" or "m10.0")
        {
            rc |= M10_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "m11" or "m11.0")
        {
            rc |= M11_Verify.Execute();
            Console.WriteLine();
        }
        if (which is "all" or "crossplat" or "cp")
        {
            rc |= MCrossPlat_Verify.Run();
            Console.WriteLine();
        }
        return rc;
    }
}

// ── CI-fast subset: skips S0.4 (300s KCP jitter), M3 (30s KCP), M-Server, M-Client. ──
// Activated by `-- ci` argument. Intended for GitHub Actions (< 3 min total).
internal static partial class Program
{
    private static int RunCi(string[] args)
    {
        int rc = 0;
        rc |= S0_1_DeterminismSpike.Execute(); Console.WriteLine();
        rc |= S0_2_PhysicsSpike.Execute();     Console.WriteLine();
        rc |= S0_3_SnapshotSpike.Execute();    Console.WriteLine();
        rc |= M1_Verify.Execute();             Console.WriteLine();
        rc |= M2_Verify.Execute();             Console.WriteLine();
        rc |= M4_Verify.Execute(durSec: null); Console.WriteLine();
        rc |= M5_1_Verify.Execute();           Console.WriteLine();
        rc |= M5_2_Verify.Execute();           Console.WriteLine();
        rc |= M5_3_Verify.Execute();           Console.WriteLine();
        rc |= M5_4_Verify.Execute();           Console.WriteLine();
        rc |= M5_5_Verify.Execute();           Console.WriteLine();
        rc |= M5_6_Verify.Execute();           Console.WriteLine();
        rc |= M5_7_Verify.Execute();           Console.WriteLine();
        rc |= MConfig_Verify.Run("mconfig");   Console.WriteLine();
        rc |= M6_Verify.Execute();             Console.WriteLine();
        rc |= M7_Verify.Execute();             Console.WriteLine();
        rc |= M8_Verify.Execute();             Console.WriteLine();
        rc |= M9_Verify.Execute();             Console.WriteLine();
        rc |= M10_Verify.Execute();            Console.WriteLine();
        rc |= M11_Verify.Execute();            Console.WriteLine();
        rc |= MCrossPlat_Verify.Run();         Console.WriteLine();
        return rc;
    }
}
