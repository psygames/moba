// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Physics;

/// <summary>Mirrors Box2DSharp's body kinds; surfaced in MOBA.Logic so callers
/// don't need to import the vendored namespace.</summary>
public enum BodyType : byte
{
    Static    = 0,
    Kinematic = 1,
    Dynamic   = 2,
}

public struct RaycastHit
{
    public EntityId Entity;
    public TSVector2 Point;
    public TSVector2 Normal;
    public Fix64 Fraction;
}
