// SPDX-License-Identifier: MIT
// IPhysicsWorld — PRD §3.1.
// Stable façade over Box2DSharp-deterministic. The implementation is
// PhysicsWorldManager. This separation lets game code mock physics in tests.

using System;
using System.Buffers;

namespace MOBA.Logic.Physics;

public interface IPhysicsWorld
{
    PhysicsBody CreateCircle(EntityId id, TSVector2 pos, Fix64 radius, BodyType type, ushort categoryBits, ushort maskBits);
    PhysicsBody CreateBox   (EntityId id, TSVector2 pos, TSVector2 half, BodyType type, ushort categoryBits, ushort maskBits);
    void DestroyBody(PhysicsBody body);
    /// <summary>Advance the simulation by one fixed lockstep tick.</summary>
    void Step(Fix64 dt);
    /// <summary>Find entities whose body centre lies within <paramref name="radius"/> of <paramref name="centre"/>.
    /// Caller-owned <paramref name="outBuf"/>; returns the number of entries written, capped at outBuf.Length.</summary>
    int  RangeQuery(TSVector2 centre, Fix64 radius, EntityId[] outBuf);
    bool Raycast   (TSVector2 from, TSVector2 to, ushort mask, out RaycastHit hit);
    void WriteSnapshot(IBufferWriter<byte> w);
    void ReadSnapshot (ReadOnlySpan<byte> r);
}
