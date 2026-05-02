// SPDX-License-Identifier: MIT
// Pooled wrapper around Box2DSharp Body. The PhysicsWorldManager retains a
// pre-allocated array of these and hands them out / takes them back without
// allocation during the simulation. Direct Body access is intentionally
// internal so non-Physics call sites cannot see into Box2D.

using Box2DSharp.Dynamics;

namespace MOBA.Logic.Physics;

public sealed class PhysicsBody : IPoolable
{
    /// <summary>Underlying Box2D body. Null when the wrapper is sitting in the pool.</summary>
    internal Body Body;
    /// <summary>Owning entity. Mirrors Body.UserData for fast reverse lookup.</summary>
    public EntityId Entity;

    [NoGC]
    public TSVector2 Position => Body == null ? default : Body.GetPosition();
    [NoGC]
    public TSVector2 LinearVelocity
    {
        get => Body == null ? default : Body.LinearVelocity;
        set { if (Body != null) Body.SetLinearVelocity(value); }
    }
    [NoGC]
    public Fix64 Angle => Body == null ? default : Body.GetAngle();
    [NoGC]
    public bool IsAwake
    {
        get => Body != null && Body.IsAwake;
        set { if (Body != null) Body.IsAwake = value; }
    }

    [NoGC]
    public void Reset()
    {
        Body = null;
        Entity = EntityId.Invalid;
    }

    /// <summary>Hard teleport — used by respawn etc. Clears velocity.</summary>
    [NoGC]
    public void Teleport(TSVector2 pos)
    {
        if (Body == null) return;
        Body.SetTransformFast(pos);
        Body.SetLinearVelocity(default);
    }
}
