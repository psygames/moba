// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Pathfinding;

/// <summary>
/// Static walkability grid in row-major order. Cell (0,0) is south-west; +x east, +y north.
/// World position of cell centre = Origin + (x + 0.5, y + 0.5) * CellSize.
/// </summary>
public sealed class GridMap
{
    public readonly int Width;
    public readonly int Height;
    public readonly Fix64 CellSize;
    public readonly TSVector2 Origin;
    private readonly byte[] _cells; // 0 = walkable, non-zero = blocked

    public GridMap(int width, int height, Fix64 cellSize, TSVector2 origin, byte[] cells)
    {
        if (width <= 0 || height <= 0) throw new System.ArgumentException("dims");
        if (cells == null || cells.Length != width * height) throw new System.ArgumentException("cells");
        Width = width; Height = height; CellSize = cellSize; Origin = origin; _cells = cells;
    }

    public int CellCount => Width * Height;
    public byte[] RawCells => _cells;

    [NoGC]
    public int Index(int x, int y) => y * Width + x;

    [NoGC]
    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    [NoGC]
    public bool IsWalkable(int x, int y) => InBounds(x, y) && _cells[y * Width + x] == 0;

    [NoGC]
    public TSVector2 CellCenter(int x, int y)
    {
        Fix64 half = CellSize / (Fix64)2;
        return new TSVector2(Origin.X + (Fix64)x * CellSize + half,
                             Origin.Y + (Fix64)y * CellSize + half);
    }

    [NoGC]
    public void WorldToCell(TSVector2 p, out int x, out int y)
    {
        Fix64 fx = (p.X - Origin.X) / CellSize;
        Fix64 fy = (p.Y - Origin.Y) / CellSize;
        x = (int)fx;
        y = (int)fy;
    }
}
