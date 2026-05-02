// SPDX-License-Identifier: MIT
// M5.4 — vision / fog of war.
//
// Per PRD §7.5: server maintains a 200×200 visibility bitmap per team.
// Each unit emits a circular vision area; the union over all alive friendly
// units produces the team's mask. No occluders in v1 (草丛/墙 deferred).
// Map covers [-50, 50] on both axes, so each cell is 0.5m × 0.5m.

using System;

namespace MOBA.Logic.Sim;

public static class Vision
{
    public const int GridSize  = 200;       // cells per axis
    public const int CellCount = GridSize * GridSize;
    public const int RowBytes  = GridSize / 8;
    public const int MaskBytes = CellCount / 8;        // 5000 bytes per team

    // World extents: [-WorldHalf, +WorldHalf] mapped to [0, GridSize).
    public static readonly Fix64 WorldHalf = (Fix64)50;
    public static readonly Fix64 CellSize  = (Fix64)100 / (Fix64)GridSize;   // 0.5m

    // Sight radii (metres).
    public static readonly Fix64 HeroSightR    = (Fix64)8;
    public static readonly Fix64 MinionSightR  = (Fix64)4;
    public static readonly Fix64 TowerSightR   = (Fix64)10;
}

/// <summary>200×200 1-bit grid (5000 bytes). One per team.</summary>
public sealed class VisionGrid
{
    public readonly byte[] Mask = new byte[Vision.MaskBytes];

    public void Clear() => Array.Clear(Mask, 0, Mask.Length);

    [NoGC]
    public bool Get(int gx, int gy)
    {
        if ((uint)gx >= Vision.GridSize || (uint)gy >= Vision.GridSize) return false;
        int idx = gy * Vision.GridSize + gx;
        return (Mask[idx >> 3] & (1 << (idx & 7))) != 0;
    }

    [NoGC]
    public void Set(int gx, int gy)
    {
        if ((uint)gx >= Vision.GridSize || (uint)gy >= Vision.GridSize) return;
        int idx = gy * Vision.GridSize + gx;
        Mask[idx >> 3] |= (byte)(1 << (idx & 7));
    }

    /// <summary>Stamp a filled circle centred at (cx, cy) world coords with radius r.</summary>
    [NoGC]
    public void StampCircle(Fix64 cx, Fix64 cy, Fix64 r)
    {
        // World → grid (rounded toward zero, then offset).
        Fix64 cs = Vision.CellSize;
        Fix64 wh = Vision.WorldHalf;
        // Bounding box in cells.
        int cellRadius = (int)((r / cs) + (Fix64)1);
        int gxC = (int)((cx + wh) / cs);
        int gyC = (int)((cy + wh) / cs);
        int gx0 = gxC - cellRadius; if (gx0 < 0) gx0 = 0;
        int gy0 = gyC - cellRadius; if (gy0 < 0) gy0 = 0;
        int gx1 = gxC + cellRadius; if (gx1 >= Vision.GridSize) gx1 = Vision.GridSize - 1;
        int gy1 = gyC + cellRadius; if (gy1 >= Vision.GridSize) gy1 = Vision.GridSize - 1;

        Fix64 r2 = r * r;
        for (int gy = gy0; gy <= gy1; gy++)
        {
            // Cell centre y in world coords.
            Fix64 wy = ((Fix64)gy + (Fix64)0.5f) * cs - wh;
            Fix64 dy = wy - cy;
            Fix64 dy2 = dy * dy;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                Fix64 wx = ((Fix64)gx + (Fix64)0.5f) * cs - wh;
                Fix64 dx = wx - cx;
                if (dx * dx + dy2 <= r2)
                {
                    int idx = gy * Vision.GridSize + gx;
                    Mask[idx >> 3] |= (byte)(1 << (idx & 7));
                }
            }
        }
    }
}

public static class VisionSystem
{
    /// <summary>Recompute one team's mask from scratch by unioning all alive friendly units' sight circles.</summary>
    [NoGC]
    public static void Recompute(VisionGrid grid, Team team,
        Hero[] heroes, Minion[] minions, Tower[] towers)
    {
        grid.Clear();
        // Heroes: blue=slot 0..4, red=slot 5..9.
        int hStart = team == Team.Blue ? 0 : 5;
        int hEnd   = team == Team.Blue ? 5 : 10;
        for (int i = hStart; i < hEnd; i++)
        {
            ref var h = ref heroes[i];
            if (!h.Alive) continue;
            grid.StampCircle(h.Pos.X, h.Pos.Y, Vision.HeroSightR);
        }
        for (int i = 0; i < minions.Length; i++)
        {
            ref var m = ref minions[i];
            if (!m.Alive || m.Team != team) continue;
            grid.StampCircle(m.Pos.X, m.Pos.Y, Vision.MinionSightR);
        }
        for (int i = 0; i < towers.Length; i++)
        {
            ref var t = ref towers[i];
            if (!t.Alive || t.Team != team) continue;
            grid.StampCircle(t.Pos.X, t.Pos.Y, Vision.TowerSightR);
        }
    }

    /// <summary>
    /// Compute XOR delta of <paramref name="current"/> vs <paramref name="previous"/> into <paramref name="dst"/>,
    /// returning the byte count actually used (always 5000 — full mask). Pure XOR; no RLE in v1.
    /// </summary>
    [NoGC]
    public static int WriteDiff(VisionGrid current, VisionGrid previous, Span<byte> dst)
    {
        for (int i = 0; i < Vision.MaskBytes; i++)
            dst[i] = (byte)(current.Mask[i] ^ previous.Mask[i]);
        return Vision.MaskBytes;
    }

    /// <summary>Population count over the whole mask (sanity helper, also non-allocating).</summary>
    [NoGC]
    public static int VisibleCellCount(VisionGrid grid)
    {
        int n = 0;
        for (int i = 0; i < Vision.MaskBytes; i++) n += BitCount(grid.Mask[i]);
        return n;
    }

    [NoGC]
    private static int BitCount(byte b)
    {
        b = (byte)(b - ((b >> 1) & 0x55));
        b = (byte)((b & 0x33) + ((b >> 2) & 0x33));
        return (b + (b >> 4)) & 0x0F;
    }
}
