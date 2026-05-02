// SPDX-License-Identifier: MIT
// M1.3 — Analyzer negative test fixtures.
//
// This file is **excluded from normal compilation**. To verify the analyzer,
// build with `/p:MobaAnalyzerProbe=true` (see csproj). Each line below MUST
// produce a MOBA001 or MOBA002 diagnostic; if the build accidentally succeeds,
// the analyzer infrastructure is broken.

#if MOBA_ANALYZER_PROBE
#nullable disable
namespace MOBA.Logic.AnalyzerProbe;

using System;
using System.Collections.Generic;
using System.Linq;        // MOBA001: forbidden namespace

internal static class Probe
{
    // MOBA001: System.Random forbidden
    private static readonly System.Random Rng = new System.Random();

    // MOBA001: float forbidden
    public static float BadFloat() => 1.0f;

    // MOBA001: System.Math.Sin forbidden
    public static double BadMath() => System.Math.Sin(1.0);

    // MOBA001: DateTime forbidden
    public static System.DateTime Now() => System.DateTime.UtcNow;

    [NoGC] // MOBA002: allocation in [NoGC] method
    public static List<int> AllocList() => new List<int>();

    [NoGC] // MOBA002: array creation
    public static int[] AllocArray() => new int[4];

    [NoGC] // MOBA002: lambda
    public static Func<int, int> AllocLambda() => x => x + 1;
}
#endif
