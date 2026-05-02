// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Pathfinding;

/// <summary>
/// 8-direction A* pathfinder over <see cref="GridMap"/>.
/// Open list: indexed binary min-heap. Closed list: byte bitmap. All buffers reused per call (Reset()).
/// Costs are integer (straight=10, diagonal=14) for full determinism. Octile heuristic.
/// "No corner cutting": diagonal step requires both adjacent ortho cells walkable.
/// </summary>
public sealed class GridPathfinder
{
    private const int CostStraight = 10;
    private const int CostDiagonal = 14;

    private readonly GridMap _map;
    private readonly int _w, _h, _n;

    private readonly int[] _gCost;     // best known cost from start, -1 = unseen
    private readonly int[] _fCost;     // g + h, used as heap key
    private readonly int[] _parent;    // parent cell index, -1 = none
    private readonly int[] _seq;       // insertion sequence for stable tie-break

    private readonly int[] _heap;      // node = cell index
    private readonly int[] _heapPos;   // cell index -> position in heap, -1 if absent
    private readonly byte[] _closed;   // 0 = open territory, 1 = closed

    private int _heapCount;
    private int _seqCounter;

    public GridPathfinder(GridMap map)
    {
        _map = map;
        _w = map.Width; _h = map.Height;
        _n = _w * _h;
        _gCost   = new int[_n];
        _fCost   = new int[_n];
        _parent  = new int[_n];
        _seq     = new int[_n];
        _heap    = new int[_n];
        _heapPos = new int[_n];
        _closed  = new byte[_n];
        // Initial reset so the very first call starts from a clean slate.
        for (int i = 0; i < _n; i++) { _gCost[i] = -1; _heapPos[i] = -1; }
    }

    public GridMap Map => _map;

    /// <summary>
    /// Finds a path from (sx,sy) to (ex,ey). On success returns the number of waypoints written
    /// to <paramref name="outPath"/> (cell indices, start..goal inclusive). Returns 0 on failure.
    /// </summary>
    public int FindPath(int sx, int sy, int ex, int ey, int[] outPath)
    {
        ResetTouched();

        if (!_map.IsWalkable(sx, sy) || !_map.IsWalkable(ex, ey)) return 0;

        int start = sy * _w + sx;
        int goal  = ey * _w + ex;

        if (start == goal)
        {
            if (outPath.Length >= 1) outPath[0] = start;
            return outPath.Length >= 1 ? 1 : 0;
        }

        _gCost[start] = 0;
        _fCost[start] = Heuristic(sx, sy, ex, ey);
        _parent[start] = -1;
        _seq[start] = _seqCounter++;
        HeapPush(start);

        while (_heapCount > 0)
        {
            int cur = HeapPop();
            if (cur == goal) return Reconstruct(cur, outPath);
            _closed[cur] = 1;

            int cx = cur % _w;
            int cy = cur / _w;

            // 8 neighbours, ordered for deterministic insertion: E, N, W, S, NE, NW, SW, SE
            TryNeighbour(cur, cx, cy,  1,  0, CostStraight, ex, ey);
            TryNeighbour(cur, cx, cy,  0,  1, CostStraight, ex, ey);
            TryNeighbour(cur, cx, cy, -1,  0, CostStraight, ex, ey);
            TryNeighbour(cur, cx, cy,  0, -1, CostStraight, ex, ey);
            TryDiagonal (cur, cx, cy,  1,  1, ex, ey);
            TryDiagonal (cur, cx, cy, -1,  1, ex, ey);
            TryDiagonal (cur, cx, cy, -1, -1, ex, ey);
            TryDiagonal (cur, cx, cy,  1, -1, ex, ey);
        }
        return 0;
    }

    [NoGC]
    private void TryNeighbour(int parent, int cx, int cy, int dx, int dy, int stepCost, int ex, int ey)
    {
        int nx = cx + dx;
        int ny = cy + dy;
        if ((uint)nx >= (uint)_w || (uint)ny >= (uint)_h) return;
        int ni = ny * _w + nx;
        if (_closed[ni] != 0) return;
        if (_map.RawCells[ni] != 0) return;
        Visit(parent, ni, nx, ny, stepCost, ex, ey);
    }

    [NoGC]
    private void TryDiagonal(int parent, int cx, int cy, int dx, int dy, int ex, int ey)
    {
        int nx = cx + dx;
        int ny = cy + dy;
        if ((uint)nx >= (uint)_w || (uint)ny >= (uint)_h) return;
        int ni = ny * _w + nx;
        if (_closed[ni] != 0) return;
        if (_map.RawCells[ni] != 0) return;
        // No corner cutting: both orthogonal adjacents must also be walkable.
        if (_map.RawCells[cy * _w + (cx + dx)] != 0) return;
        if (_map.RawCells[(cy + dy) * _w + cx] != 0) return;
        Visit(parent, ni, nx, ny, CostDiagonal, ex, ey);
    }

    [NoGC]
    private void Visit(int parent, int ni, int nx, int ny, int stepCost, int ex, int ey)
    {
        int tentative = _gCost[parent] + stepCost;
        int prev = _gCost[ni];
        if (prev != -1 && tentative >= prev) return;
        _gCost[ni] = tentative;
        _parent[ni] = parent;
        _fCost[ni] = tentative + Heuristic(nx, ny, ex, ey);
        _seq[ni] = _seqCounter++;
        if (_heapPos[ni] == -1) HeapPush(ni); else HeapDecreaseKey(_heapPos[ni]);
    }

    [NoGC]
    private static int Heuristic(int ax, int ay, int bx, int by)
    {
        int dx = ax > bx ? ax - bx : bx - ax;
        int dy = ay > by ? ay - by : by - ay;
        // Octile distance with the same 10/14 cost basis.
        return dx > dy
            ? CostStraight * (dx - dy) + CostDiagonal * dy
            : CostStraight * (dy - dx) + CostDiagonal * dx;
    }

    private int Reconstruct(int goal, int[] outPath)
    {
        // Walk parents to count length, then fill backwards.
        int len = 0;
        int cur = goal;
        while (cur != -1) { len++; cur = _parent[cur]; }
        if (len > outPath.Length) return 0;
        cur = goal;
        for (int i = len - 1; i >= 0; i--) { outPath[i] = cur; cur = _parent[cur]; }
        return len;
    }

    /// <summary>Resets only cells that participated in the last search (sparse reset).</summary>
    private void ResetTouched()
    {
        for (int i = 0; i < _heapCount; i++)
        {
            int idx = _heap[i];
            _gCost[idx] = -1;
            _heapPos[idx] = -1;
        }
        _heapCount = 0;
        // Closed cells need explicit clear since they were popped from the heap.
        // Walk g-cost dirty list via a quick pass: anywhere _gCost != -1 OR _closed != 0 is dirty.
        // For 200x200 that's 40k ints/bytes — fast enough; deterministic.
        for (int i = 0; i < _n; i++)
        {
            if (_closed[i] != 0) _closed[i] = 0;
            if (_gCost[i] != -1) _gCost[i] = -1;
        }
        _seqCounter = 0;
    }

    // ---- indexed binary min-heap (key = (_fCost, _seq)) ----------------------

    [NoGC]
    private void HeapPush(int idx)
    {
        int pos = _heapCount++;
        _heap[pos] = idx;
        _heapPos[idx] = pos;
        SiftUp(pos);
    }

    [NoGC]
    private int HeapPop()
    {
        int top = _heap[0];
        _heapPos[top] = -1;
        int last = --_heapCount;
        if (last > 0)
        {
            int moved = _heap[last];
            _heap[0] = moved;
            _heapPos[moved] = 0;
            SiftDown(0);
        }
        return top;
    }

    [NoGC]
    private void HeapDecreaseKey(int pos) => SiftUp(pos);

    [NoGC]
    private void SiftUp(int pos)
    {
        while (pos > 0)
        {
            int parent = (pos - 1) >> 1;
            if (Less(_heap[pos], _heap[parent]))
            {
                Swap(pos, parent);
                pos = parent;
            }
            else break;
        }
    }

    [NoGC]
    private void SiftDown(int pos)
    {
        int n = _heapCount;
        while (true)
        {
            int l = (pos << 1) + 1;
            if (l >= n) return;
            int r = l + 1;
            int best = l;
            if (r < n && Less(_heap[r], _heap[l])) best = r;
            if (Less(_heap[best], _heap[pos]))
            {
                Swap(pos, best);
                pos = best;
            }
            else return;
        }
    }

    [NoGC]
    private bool Less(int a, int b)
    {
        int fa = _fCost[a], fb = _fCost[b];
        if (fa != fb) return fa < fb;
        return _seq[a] < _seq[b]; // deterministic tie-break
    }

    [NoGC]
    private void Swap(int posA, int posB)
    {
        int a = _heap[posA];
        int b = _heap[posB];
        _heap[posA] = b; _heap[posB] = a;
        _heapPos[a] = posB; _heapPos[b] = posA;
    }
}
