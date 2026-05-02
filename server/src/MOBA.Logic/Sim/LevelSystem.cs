// SPDX-License-Identifier: MIT
// PRD §7.3 — XP / Level-up / Kill economy.
using MOBA.Logic.Config;

namespace MOBA.Logic.Sim;

/// <summary>
/// Deterministic level-up and kill-reward logic (PRD §7.3).
/// All methods are [NoGC] — no heap allocations.
/// </summary>
public static class LevelSystem
{
    public const int MaxLevel = 18;

    // Fallback EXP table: s_expForLevel[level] = cumulative XP needed to reach that level.
    // Matches ConfigBaker data exactly. Index 0 unused, index 1 = 0 (start at level 1).
    private static readonly uint[] s_expForLevel = {
        0,     // [0] unused
        0,     // [1] level 1 (start here, needs 0)
        280,   // [2]
        380,   // [3]
        480,   // [4]
        580,   // [5]
        680,   // [6]
        780,   // [7]
        880,   // [8]
        1000,  // [9]
        1100,  // [10]
        1200,  // [11]
        1300,  // [12]
        1400,  // [13]
        1500,  // [14]
        1600,  // [15]
        1700,  // [16]
        1800,  // [17]
        2000,  // [18]
    };

    // PRD §7.3 kill gold/xp constants
    public const uint MeleeMinionGold   = 20;
    public const uint CasterMinionGold  = 18;
    public const uint SiegeMinionGold   = 40;   // PRD §7.3
    public const uint MeleeMinionXp     = 60;
    public const uint CasterMinionXp    = 50;
    public const uint SiegeMinionXp     = 90;   // PRD §7.3
    public const uint JungleMonsterGold = 60;   // PRD §4.3 jungle camp reward
    public const uint JungleMonsterXp   = 80;
    // PRD §7.3: tower kill = all team members +150g
    public const uint TowerKillGold     = 150;

    // ── Per-level stat growth fallback (PRD §7.7, used when ConfigManager not loaded) ──
    // [heroDefId] arrays: index 0=Swordsman, 1=Mage, 2=Marksman
    private static readonly int[] s_hpPerLv  = { 80, 60, 70 };
    private static readonly int[] s_adPerLv  = {  5,  0,  4 };
    private static readonly int[] s_apPerLv  = {  0,  8,  0 };
    // PRD §7.7: Marksman AS 0.85(+0.03/lv); others have no AS growth.
    private static readonly Fix64[] s_asPerLv = { Fix64.Zero, Fix64.Zero, (Fix64)0.03f };

    [NoGC]
    private static uint ExpForLevel(int level)
    {
        if (level <= 1) return 0;
        if (level > MaxLevel) return uint.MaxValue;
        if (ConfigManager.IsLoaded)
        {
            uint v = ConfigManager.ExpForLevel(level);
            if (v > 0) return v;
        }
        return s_expForLevel[level];
    }

    /// <summary>
    /// Award XP to a hero, then check for level-up.
    /// Call after AwardMinionKill / in any context that grants XP.
    /// </summary>
    [NoGC]
    public static void AwardXp(Hero[] heroes, int slot, uint xp)
    {
        if ((uint)slot >= (uint)heroes.Length) return;
        ref var h = ref heroes[slot];
        if (!h.Alive) return;
        h.Xp += xp;
        TryLevelUp(heroes, slot);
    }

    /// <summary>Award gold and XP for killing a minion (PRD §7.3).</summary>
    [NoGC]
    public static void AwardMinionKill(Hero[] heroes, byte killerSlot, MinionType minionType)
    {
        if (killerSlot >= heroes.Length) return;
        ref var killer = ref heroes[killerSlot];
        if (!killer.Alive) return;
        if (minionType == MinionType.Melee)
        {
            killer.Gold += MeleeMinionGold;
            AwardXp(heroes, killerSlot, MeleeMinionXp);
        }
        else if (minionType == MinionType.Caster)
        {
            killer.Gold += CasterMinionGold;
            AwardXp(heroes, killerSlot, CasterMinionXp);
        }
        else // Siege
        {
            killer.Gold += SiegeMinionGold;
            AwardXp(heroes, killerSlot, SiegeMinionXp);
        }
    }

    /// <summary>Award gold and XP for killing a jungle monster (PRD §4.3).</summary>
    [NoGC]
    public static void AwardJungleKill(Hero[] heroes, byte killerSlot)
    {
        if (killerSlot >= heroes.Length) return;
        ref var killer = ref heroes[killerSlot];
        if (!killer.Alive) return;
        killer.Gold += JungleMonsterGold;
        AwardXp(heroes, killerSlot, JungleMonsterXp);
    }

    /// <summary>Award gold to all living team members for a tower kill (PRD §7.3: 全队 +150g).</summary>
    [NoGC]
    public static void AwardTowerKill(Hero[] heroes, byte killerSlot)
    {
        if (killerSlot >= heroes.Length) return;
        Team team = killerSlot < 5 ? Team.Blue : Team.Red;
        for (int i = 0; i < heroes.Length; i++)
        {
            Team hTeam = i < 5 ? Team.Blue : Team.Red;
            if (hTeam == team && heroes[i].Alive)
                heroes[i].Gold += TowerKillGold;
        }
    }

    /// <summary>Check and apply level-ups for a hero until XP is insufficient or max level.</summary>
    [NoGC]
    public static void TryLevelUp(Hero[] heroes, int slot)
    {
        ref var h = ref heroes[slot];
        while (h.Level < MaxLevel)
        {
            uint needed = ExpForLevel(h.Level + 1);
            if (h.Xp < needed) break;
            h.Level++;
            ApplyLevelGrowth(ref h);
        }
    }

    [NoGC]
    private static void ApplyLevelGrowth(ref Hero h)
    {
        int defId = h.HeroDefId % BuiltinContent.HeroCount;
        if (ConfigManager.IsLoaded)
        {
            ref var cfg = ref ConfigManager.GetHero(defId);
            Fix64 hp = cfg.HpPerLv;
            h.MaxHp += hp;
            h.Hp    += hp;   // gain HP on level-up (standard MOBA behaviour)
            h.MaxMp += cfg.MpPerLv;
            h.Mp    += cfg.MpPerLv;
            h.Ad    += cfg.AdPerLv;
            h.Ap    += cfg.ApPerLv;
        }
        else
        {
            // Fallback (no config.bin loaded).
            Fix64 hpGrowth = (Fix64)s_hpPerLv[defId];
            h.MaxHp += hpGrowth;
            h.Hp    += hpGrowth;
            h.Ad    += (Fix64)s_adPerLv[defId];
            h.Ap    += (Fix64)s_apPerLv[defId];
        }
        // PRD §7.7: attack-speed growth is hardcoded (Marksman +0.03/lv).
        h.AttackSpeed += s_asPerLv[defId];
    }

    /// <summary>
    /// Pre-JIT all level-up code paths. Call once before GC-sensitive loops
    /// to avoid first-call JIT allocation.
    /// </summary>
    public static void WarmUp()
    {
        var heroes = new Hero[1];
        heroes[0].Alive = true;
        heroes[0].Level = 1;
        heroes[0].HeroDefId = 0;
        BuiltinContent.ApplyBaseStats(ref heroes[0]);
        // Award enough XP to trigger every level-up path (both branches of ApplyLevelGrowth).
        AwardXp(heroes, 0, 99999);
        // Warm siege-kill and jungle-kill paths.
        AwardMinionKill(heroes, 0, MinionType.Siege);
        AwardJungleKill(heroes, 0);
    }
}
