// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Sim;

public enum BuffStackPolicy : byte { Independent = 0, Refresh = 1, Stack = 2 }
public enum BuffModifierKind : byte
{
    None = 0,
    AdAdd, ApAdd, ArmorAdd, MagicResistAdd, MoveSpeedMul, AttackSpeedMul, MaxHpAdd,
    DamageOverTime,    // payload = dmg per tick
    HealOverTime,
    ApplyTag,          // payload encoded as tag bits in Param
    /// <summary>PRD §7.7 破甲: reduces target's effective armor by ModifierValue × Stack when computing incoming damage.</summary>
    ArmorReduction,
}

/// <summary>16 bytes packed buff slot. Tick periodicity is fixed at 15Hz logic frames
/// (≈66.6 ms); for sub-frame timings expand later.</summary>
public struct BuffInstance
{
    public ushort DefId;       // index into BuffEngine table
    public byte Stack;         // 1..MaxStack
    public byte SourceSlot;    // attacker hero slot (0..9), 0xFF for non-hero source
    public uint EndFrame;      // absolute frame when buff expires
    public uint NextTickFrame; // absolute frame for next tick (DoT/HoT)
    public ulong TagBits;      // copy of def.TagBits (so we can clear on removal)
}

/// <summary>Static buff definition. Held in a pre-allocated table — no per-frame
/// allocations; Tick reads by index.</summary>
public struct BuffDef
{
    public ushort Id;
    public BuffStackPolicy Stack;
    public byte MaxStack;
    public uint DurationFrames;
    public uint TickIntervalFrames;     // 0 = no tick
    public BuffModifierKind Modifier;
    public Fix64 ModifierValue;         // attribute add or DoT damage per tick
    public ulong TagBits;               // tags applied while buff is active
}
