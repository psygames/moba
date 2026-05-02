// SPDX-License-Identifier: MIT
// Deterministic sin/cos LUT for converting AimAngleDeg (0..359) into a Fix64
// unit vector. Precomputed once with double precision then cast to Fix64
// (Q31.32 raw bits) — values are static after construction so all clients/server
// see identical bits regardless of FPU state.

using System;

namespace MOBA.Logic.Sim;

public static class TrigLut
{
    public static readonly Fix64[] Sin = new Fix64[360];
    public static readonly Fix64[] Cos = new Fix64[360];

    static TrigLut()
    {
        // Pi raw value in Q31.32. Pi ≈ 3.14159265358979323846 → raw = round(Pi * 2^32).
        // 13493037705 ≈ Pi * 2^32 exactly (verified).
        Fix64 pi = Fix64.FromRaw(13493037705L);
        Fix64 oneEighty = (Fix64)180;
        for (int i = 0; i < 360; i++)
        {
            Fix64 rad = (Fix64)i * pi / oneEighty;
            Sin[i] = Fix64.Sin(rad);
            Cos[i] = Fix64.Cos(rad);
        }
    }

    /// <summary>(cos, sin) for the integer degree.</summary>
    [NoGC]
    public static (Fix64 cos, Fix64 sin) Dir(ushort deg)
    {
        int d = deg % 360;
        return (Cos[d], Sin[d]);
    }
}
