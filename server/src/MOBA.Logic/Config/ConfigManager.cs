// SPDX-License-Identifier: MIT
// §8 — Global config state. Populated by ConfigBinary.TryLoad().
// All tables are read-only after Load; writes are forbidden at runtime.

using System;
using MOBA.Logic.Sim;

namespace MOBA.Logic.Config;

/// <summary>
/// Holds all Luban-equivalent config tables.
/// Call <see cref="LoadFromBytes"/> once at startup before constructing any world.
/// </summary>
public sealed class ConfigManager
{
    // ── Singleton ─────────────────────────────────────────────────────────
    private static ConfigManager _instance;
    public  static ConfigManager Instance => _instance ??= new ConfigManager();

    /// <summary>True after a successful <see cref="LoadFromBytes"/> call.</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>Reset for test isolation.</summary>
    public static void Reset()
    {
        _instance  = null;
        IsLoaded   = false;
    }

    // ── Config tables ──────────────────────────────────────────────────────
    /// <summary>Hero config rows, indexed by hero Id (0..HeroCount-1).</summary>
    public CfgHero[] Heroes;

    /// <summary>Level-exp table (18 rows, Level 1..18).</summary>
    public CfgLevel[] Levels;

    /// <summary>Lane path definitions (3 lanes).</summary>
    public CfgLane[] Lanes;

    // ── Resolved buff runtime indices ──────────────────────────────────────
    /// <summary>BuffEngine.Defs index for the slow-30% buff.</summary>
    public ushort BuffSlow30Idx;
    /// <summary>BuffEngine.Defs index for the stun buff.</summary>
    public ushort BuffStun10Idx;
    /// <summary>BuffEngine.Defs index for the HoT/shield buff.</summary>
    public ushort BuffShieldIdx;

    // ── Load API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parse <paramref name="data"/> (config.bin bytes), re-register all engine
    /// tables (SkillEngine / BuffEngine / Items), and populate this instance.
    /// File I/O must be done by the caller (MOBA.Server / test host), not here.
    /// </summary>
    public static bool LoadFromBytes(ReadOnlySpan<byte> data)
    {
        var mgr = new ConfigManager();
        // SkillEngine and BuffEngine are re-populated inside TryLoad.
        SkillEngine.Reset();
        BuffEngine.Reset();
        Items.Reset();
        if (!ConfigBinary.TryLoad(data, mgr)) return false;

        // Mirror buff indices back into BuiltinContent so skill effects work.
        BuiltinContent.SetBuffIds(mgr.BuffSlow30Idx, mgr.BuffStun10Idx, mgr.BuffShieldIdx);

        // Mirror hero-skill slot mapping back into BuiltinContent.
        if (mgr.Heroes != null)
        {
            foreach (var h in mgr.Heroes)
            {
                int id = h.Id % BuiltinContent.HeroCount;
                BuiltinContent.HeroSkills[id, 0] = h.SkillQ;
                BuiltinContent.HeroSkills[id, 1] = h.SkillW;
                BuiltinContent.HeroSkills[id, 2] = h.SkillE;
                BuiltinContent.HeroSkills[id, 3] = h.SkillR;
            }
        }

        // Mark BuiltinContent as already registered so it won't overwrite engines.
        BuiltinContent.MarkLoadedFromConfig();

        _instance = mgr;
        IsLoaded  = true;
        return true;
    }

    // ── Helper accessors ───────────────────────────────────────────────────

    /// <summary>Get hero config by Id. Returns default CfgHero if not loaded.</summary>
    public static ref CfgHero GetHero(int heroId)
    {
        if (IsLoaded && _instance.Heroes != null &&
            heroId >= 0 && heroId < _instance.Heroes.Length)
            return ref _instance.Heroes[heroId];
        return ref _empty;
    }

    /// <summary>Exp required to reach <paramref name="level"/> (1-based).</summary>
    public static uint ExpForLevel(int level)
    {
        if (!IsLoaded || _instance.Levels == null) return 0;
        foreach (var lv in _instance.Levels)
            if (lv.Level == level) return lv.ExpRequired;
        return 0;
    }

    private static CfgHero _empty;
}
