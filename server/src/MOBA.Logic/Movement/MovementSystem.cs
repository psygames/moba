// SPDX-License-Identifier: MIT
using MOBA.Logic.Pathfinding;
using MOBA.Logic.Physics;

namespace MOBA.Logic.Movement;

/// <summary>
/// Per-entity movement state: cached path of cell indices + cursor.
/// Pure data; reset via Pool when entity dies.
/// </summary>
public sealed class PathAgent : IPoolable
{
    public EntityId Entity;
    public int[] Path = new int[1024];
    public int PathLen;
    public int Cursor;
    public Fix64 Speed;

    [NoGC]
    public void Reset()
    {
        Entity = EntityId.Invalid;
        PathLen = 0;
        Cursor = 0;
        Speed = Fix64.Zero;
    }
}

/// <summary>
/// Per-tick driver: advances each agent's cursor and writes the deterministic LinearVelocity
/// onto its physics body. Pure logic, no allocations.
/// </summary>
public sealed class MovementSystem
{
    private readonly GridMap _map;
    private readonly PhysicsWorldManager _world;
    private readonly Fix64 _arriveDistSq;

    public MovementSystem(GridMap map, PhysicsWorldManager world)
    {
        _map = map;
        _world = world;
        Fix64 r = Fix64.FromRaw((long)(0.1 * (1L << 32))); // 0.1m arrive radius (PRD §3.2)
        _arriveDistSq = r * r;
    }

    [NoGC]
    public void Tick(PathAgent[] agents, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var a = agents[i];
            if (a.PathLen == 0 || a.Cursor >= a.PathLen) { Stop(a); continue; }

            var body = _world.TryGet(a.Entity);
            if (body == null) continue;

            int cellIdx = a.Path[a.Cursor];
            int cx = cellIdx % _map.Width;
            int cy = cellIdx / _map.Width;
            TSVector2 target = _map.CellCenter(cx, cy);
            TSVector2 cur    = body.Position;
            TSVector2 delta  = target - cur;
            Fix64 distSq = delta.X * delta.X + delta.Y * delta.Y;

            if (distSq <= _arriveDistSq)
            {
                a.Cursor++;
                if (a.Cursor >= a.PathLen) { Stop(a); continue; }
                cellIdx = a.Path[a.Cursor];
                cx = cellIdx % _map.Width; cy = cellIdx / _map.Width;
                target = _map.CellCenter(cx, cy);
                delta = target - cur;
                distSq = delta.X * delta.X + delta.Y * delta.Y;
                if (distSq <= _arriveDistSq) { Stop(a); continue; }
            }

            // Normalised direction * speed. Avoid Sqrt cost by using FVector2.Normalize().
            delta.Normalize();
            body.LinearVelocity = delta * a.Speed;
        }
    }

    [NoGC]
    private static void Stop(PathAgent a)
    {
        // Caller is responsible for zeroing the body's velocity if required.
        a.PathLen = 0;
        a.Cursor = 0;
    }
}
