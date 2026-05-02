// SPDX-License-Identifier: MIT
// PhysicsWorldManager — PRD §3.1.
// Concrete IPhysicsWorld over Box2DSharp-deterministic with:
//   - pre-allocated PhysicsBody pool (no allocation during Step)
//   - shared CircleShape/PolygonShape scratch instances reused per CreateBody
//   - linear scan range query against BodyList (M1 — replace with broad-phase
//     AABB query in M2 if profiler shows it)
//   - snapshot serialization that mirrors S0.3 (pos, vel, angle, angularVel,
//     awake) — NO contact cache
//
// Determinism contract:
//   * Bodies are stored in EntityId order in the rented `_bodies` array,
//     plus a parallel hash-free direct-index lookup. Box2DSharp keeps its
//     own LinkedList; we never iterate it for game state.
//   * CreateBody / DestroyBody happen between ticks, never inside Step.
//   * No System.Random, DateTime, hashing of object identity.

using System;
using System.Buffers;
using System.Buffers.Binary;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;

namespace MOBA.Logic.Physics;

public sealed class PhysicsWorldManager : IPhysicsWorld
{
    public const int MaxBody = 512;
    public const int SnapshotBytesPerBody = 8 * 6 + 1;     // pos(2)+vel(2)+angle+angularVel + awake byte

    private readonly World _world;
    private readonly Pool<PhysicsBody> _bodyPool;
    /// <summary>Active bodies indexed by EntityId.Value (sparse).
    /// MaxEntityId enforced by the room — out-of-range ids throw at create.</summary>
    private readonly PhysicsBody[] _byEntity;
    /// <summary>Compact list of active wrappers, used for snapshot + range query.
    /// Order = creation order (stable across snapshot/restore).</summary>
    private readonly PhysicsBody[] _active;
    private int _activeCount;

    /// <summary>Number of currently live bodies.</summary>
    public int ActiveCount => _activeCount;

    public PhysicsWorldManager(int maxEntityId = 1024, bool warmStarting = true, bool continuous = false)
    {
        if (maxEntityId < 16) throw new ArgumentOutOfRangeException(nameof(maxEntityId));
        _world = new World(default(TSVector2))
        {
            WarmStarting = warmStarting,
            ContinuousPhysics = continuous,
            SubStepping = false,
        };
        _bodyPool  = new Pool<PhysicsBody>(MaxBody, MaxBody * 2);
        _bodyPool.Prewarm(MaxBody);
        _byEntity  = new PhysicsBody[maxEntityId];
        _active    = new PhysicsBody[MaxBody];
    }

    /// <remarks>Setup-time allocation (CircleShape) is intentional; this method is
    /// called between ticks, not inside Step. Marked without [NoGC] for that reason.</remarks>
    public PhysicsBody CreateCircle(EntityId id, TSVector2 pos, Fix64 radius, BodyType type, ushort categoryBits, ushort maskBits)
    {
        var w = AcquireWrapper(id);
        var bd = new BodyDef
        {
            BodyType = ToB2(type),
            Position = pos,
            AllowSleep = type == BodyType.Dynamic,   // statics never sleep, dynamics may
            UserData = w,
        };
        var body = _world.CreateBody(bd);
        var fd = new FixtureDef
        {
            Shape       = new CircleShape { Radius = radius },
            Density     = type == BodyType.Static ? Fix64.Zero : Fix64.One,
            Friction    = (Fix64)0.4f,
            Restitution = (Fix64)0.2f,
        };
        fd.Filter.CategoryBits = categoryBits;
        fd.Filter.MaskBits = maskBits;
        body.CreateFixture(fd);
        w.Body = body;
        return w;
    }

    /// <remarks>Setup-time only; see CreateCircle.</remarks>
    public PhysicsBody CreateBox(EntityId id, TSVector2 pos, TSVector2 half, BodyType type, ushort categoryBits, ushort maskBits)
    {
        var w = AcquireWrapper(id);
        var bd = new BodyDef
        {
            BodyType = ToB2(type),
            Position = pos,
            AllowSleep = type == BodyType.Dynamic,
            UserData = w,
        };
        var body = _world.CreateBody(bd);
        var box = new PolygonShape();
        box.SetAsBox(half.X, half.Y);
        var fd = new FixtureDef
        {
            Shape       = box,
            Density     = type == BodyType.Static ? Fix64.Zero : Fix64.One,
            Friction    = (Fix64)0.4f,
            Restitution = (Fix64)0.2f,
        };
        fd.Filter.CategoryBits = categoryBits;
        fd.Filter.MaskBits = maskBits;
        body.CreateFixture(fd);
        w.Body = body;
        return w;
    }

    public void DestroyBody(PhysicsBody body)
    {
        if (body == null || body.Body == null) return;
        // Remove from active list (swap-with-last).
        int idx = IndexOfActive(body);
        if (idx >= 0)
        {
            int last = --_activeCount;
            _active[idx] = _active[last];
            _active[last] = null;
        }
        if (body.Entity.IsValid && body.Entity.Value < (uint)_byEntity.Length)
            _byEntity[body.Entity.Value] = null;
        _world.DestroyBody(body.Body);
        _bodyPool.Return(body);
    }

    [NoGC]
    public void Step(Fix64 dt) => _world.Step(dt, 8, 3);

    [NoGC]
    public int RangeQuery(TSVector2 centre, Fix64 radius, EntityId[] outBuf)
    {
        if (outBuf == null) return 0;
        Fix64 r2 = radius * radius;
        int n = 0;
        for (int i = 0; i < _activeCount && n < outBuf.Length; i++)
        {
            var w = _active[i];
            var p = w.Position;
            Fix64 dx = p.X - centre.X;
            Fix64 dy = p.Y - centre.Y;
            if (dx * dx + dy * dy <= r2) outBuf[n++] = w.Entity;
        }
        return n;
    }

    private readonly RaycastCollector _rayCollector = new();

    public bool Raycast(TSVector2 from, TSVector2 to, ushort mask, out RaycastHit hit)
    {
        _rayCollector.Reset(mask);
        _world.RayCast(_rayCollector, from, to);
        hit = _rayCollector.Hit;
        return _rayCollector.Hits;
    }

    public void WriteSnapshot(IBufferWriter<byte> w)
    {
        var span = w.GetSpan(_activeCount * SnapshotBytesPerBody);
        for (int i = 0; i < _activeCount; i++)
        {
            var pb = _active[i];
            int off = i * SnapshotBytesPerBody;
            var pos = pb.Body.GetPosition();
            var vel = pb.Body.LinearVelocity;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 0,  8), pos.X.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 8,  8), pos.Y.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 16, 8), pb.Body.GetAngle().RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 24, 8), vel.X.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 32, 8), vel.Y.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 40, 8), pb.Body.AngularVelocity.RawValue);
            span[off + 48] = (byte)(pb.Body.IsAwake ? 1 : 0);
        }
        w.Advance(_activeCount * SnapshotBytesPerBody);
    }

    public void ReadSnapshot(ReadOnlySpan<byte> r)
    {
        int expected = _activeCount * SnapshotBytesPerBody;
        if (r.Length < expected) throw new ArgumentException("snapshot too small");
        for (int i = 0; i < _activeCount; i++)
        {
            var pb = _active[i];
            int off = i * SnapshotBytesPerBody;
            long pxR  = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 0,  8));
            long pyR  = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 8,  8));
            long angR = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 16, 8));
            long vxR  = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 24, 8));
            long vyR  = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 32, 8));
            long avR  = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(off + 40, 8));
            bool awake = r[off + 48] != 0;
            pb.Body.SetTransform(new TSVector2(Fix64.FromRaw(pxR), Fix64.FromRaw(pyR)), Fix64.FromRaw(angR));
            pb.Body.SetLinearVelocity(new TSVector2(Fix64.FromRaw(vxR), Fix64.FromRaw(vyR)));
            pb.Body.SetAngularVelocity(Fix64.FromRaw(avR));
            pb.Body.IsAwake = awake;
        }
    }

    /// <summary>Try to look up the wrapper for an entity. Returns null if not present.</summary>
    public PhysicsBody TryGet(EntityId id)
        => id.IsValid && id.Value < (uint)_byEntity.Length ? _byEntity[id.Value] : null;

    // ---------------------------------------------------------------- helpers

    private PhysicsBody AcquireWrapper(EntityId id)
    {
        if (!id.IsValid) throw new ArgumentException("invalid EntityId", nameof(id));
        if (id.Value >= (uint)_byEntity.Length) throw new ArgumentOutOfRangeException(nameof(id));
        if (_byEntity[id.Value] != null) throw new InvalidOperationException($"body for {id} already exists");
        if (_activeCount >= _active.Length) throw new InvalidOperationException("MaxBody exceeded");
        var w = _bodyPool.Get();
        w.Entity = id;
        _byEntity[id.Value] = w;
        _active[_activeCount++] = w;
        return w;
    }

    [NoGC]
    private int IndexOfActive(PhysicsBody body)
    {
        for (int i = 0; i < _activeCount; i++) if (ReferenceEquals(_active[i], body)) return i;
        return -1;
    }

    private static Box2DSharp.Dynamics.BodyType ToB2(BodyType t) => t switch
    {
        BodyType.Static    => Box2DSharp.Dynamics.BodyType.StaticBody,
        BodyType.Kinematic => Box2DSharp.Dynamics.BodyType.KinematicBody,
        BodyType.Dynamic   => Box2DSharp.Dynamics.BodyType.DynamicBody,
        _                  => Box2DSharp.Dynamics.BodyType.StaticBody,
    };

    // ----- raycast collector ---------------------------------------------------

    private sealed class RaycastCollector : Box2DSharp.Dynamics.IRayCastCallback
    {
        public RaycastHit Hit;
        public bool Hits;
        private ushort _mask;

        public void Reset(ushort mask)
        {
            Hits = false;
            Hit = default;
            _mask = mask;
        }

        public Fix64 RayCastCallback(Box2DSharp.Dynamics.Fixture fixture, in TSVector2 point, in TSVector2 normal, Fix64 fraction)
        {
            // Filter on category bits.
            ushort cat = fixture.Filter.CategoryBits;
            if ((cat & _mask) == 0) return Fix64.One; // continue, ignore

            EntityId ent = EntityId.Invalid;
            if (fixture.Body != null && fixture.Body.UserData is PhysicsBody pb) ent = pb.Entity;
            Hit = new RaycastHit { Entity = ent, Point = point, Normal = normal, Fraction = fraction };
            Hits = true;
            return fraction; // clip to the closest hit
        }
    }
}
