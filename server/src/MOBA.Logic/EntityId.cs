// SPDX-License-Identifier: MIT
namespace MOBA.Logic;

/// <summary>Strongly-typed entity identifier. 0 = invalid.</summary>
public readonly struct EntityId : System.IEquatable<EntityId>
{
    public readonly uint Value;
    public EntityId(uint v) { Value = v; }
    public bool IsValid => Value != 0;
    public static readonly EntityId Invalid = default;
    public bool Equals(EntityId o) => Value == o.Value;
    public override bool Equals(object o) => o is EntityId e && Equals(e);
    public override int GetHashCode() => (int)Value;
    public override string ToString() => $"E{Value}";
    public static bool operator ==(EntityId a, EntityId b) => a.Value == b.Value;
    public static bool operator !=(EntityId a, EntityId b) => a.Value != b.Value;
}
