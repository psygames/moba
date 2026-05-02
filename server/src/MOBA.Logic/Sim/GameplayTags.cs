// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Sim;

/// <summary>
/// Gameplay tag bits (PRD §4.2.1). Stored as ulong on each Hero/Minion.
/// All checks must go through HasTag/AddTag/RemoveTag (no raw bit twiddling
/// in callers) so the tag enum can grow to 64 bits without churn.
/// </summary>
public static class GameplayTags
{
    public const ulong None        = 0UL;
    public const ulong Stunned     = 1UL << 0;
    public const ulong Silenced    = 1UL << 1;
    public const ulong Rooted      = 1UL << 2;
    public const ulong Invincible  = 1UL << 3;
    public const ulong Invisible   = 1UL << 4;
    public const ulong Slowed      = 1UL << 5;
    public const ulong Charging    = 1UL << 6; // skill in pre-cast
    public const ulong CannotMove  = Stunned | Rooted | Charging;
    public const ulong CannotCast  = Stunned | Silenced;
}
