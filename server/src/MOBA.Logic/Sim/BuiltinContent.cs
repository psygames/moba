// SPDX-License-Identifier: MIT
// PRD §7.7 v3.0 — three example heroes with exact 12 skills.
//
// HeroDefId    Archetype     Slot 0 (Q)           Slot 1 (W)          Slot 2 (E)              Slot 3 (R)
// 0  Swordsman 近战 bruiser  冲锋 (dash+dmg+KB)   旋风斩 (AoE)        破甲 (passive ArmorShred) 斩首 (exec)
// 1  Mage      远程 caster   火球 (projectile)    冰墙 (wall)         闪现 (blink)             陨石 (delayed AOE+stun)
// 2  Marksman  远程 DPS      穿透箭 (pierce line)  位移射击 (retreat+shot) 狩猎之眼 (passive dmgMul) 狙击 (long range)
//
// @15 Hz: 1s=15f, 0.4s=6f, 0.8s=12f, 3s=45f, 4s=60f.

using System;

namespace MOBA.Logic.Sim;

public static class BuiltinContent
{
    public const int HeroCount = 3;
    public const int SkillsPerHero = 4;
    public const int TotalSkills = HeroCount * SkillsPerHero;

    /// <summary>SkillDefIds laid out as [heroDefId, slot]. Filled by Register().</summary>
    public static readonly ushort[,] HeroSkills = new ushort[HeroCount, SkillsPerHero];

    /// <summary>Per-hero base stats. Indexed by HeroDefId.</summary>
    public static readonly HeroBaseStats[] HeroStats = new HeroBaseStats[HeroCount];

    // Buff IDs (1-based runtime index).
    public static ushort BuffSlow30       { get; private set; }
    public static ushort BuffStun10       { get; private set; }
    public static ushort BuffShield       { get; private set; }
    public static ushort BuffArmorShred5  { get; private set; }

    public struct HeroBaseStats
    {
        public Fix64 MaxHp, MaxMp, Ad, Ap, Armor, MagicResist, AttackRange, AttackSpeed, MoveSpeed;
        // PRD §7.7 basic-attack definition (differs per archetype).
        // damage = BasicAttackBase + Ad*(BasicAttackAdScale1k/1000) + Ap*(BasicAttackApScale1k/1000)
        public int   BasicAttackBase;      // flat damage component (e.g. 30 for Mage)
        public int   BasicAttackAdScale1k; // AD multiplier × 1000  (1000 = 1.0×AD, 0 = no AD)
        public int   BasicAttackApScale1k; // AP multiplier × 1000  (300 = 0.3×AP, 0 = no AP)
        public DamageType BasicAttackDmgType; // Physical / Magic
    }

    private static bool _registered;

    // ── Config bridge ──────────────────────────────────────────────────────

    internal static void MarkLoadedFromConfig() => _registered = true;

    internal static void SetBuffIds(ushort slow30, ushort stun10, ushort shield)
    {
        BuffSlow30 = slow30;
        BuffStun10 = stun10;
        BuffShield = shield;
    }

    /// <summary>Idempotent. Safe to call from world ctor and tests.</summary>
    public static void Register()
    {
        if (_registered &&
            SkillEngine.DefCount >= TotalSkills &&
            BuffEngine.DefCount  >= 4 &&
            Items.DefCount       >= 10 &&
            HeroSkills[HeroCount - 1, SkillsPerHero - 1] < SkillEngine.DefCount)
            return;

        _registered = false;
        SkillEngine.Reset();
        BuffEngine.Reset();
        Items.Reset();
        Items.RegisterDefaults();

        // ---- Buffs -------------------------------------------------------------------
        BuffSlow30 = BuffEngine.Register(new BuffDef
        {
            Id = 1, Stack = BuffStackPolicy.Refresh, MaxStack = 1,
            DurationFrames = 30, TickIntervalFrames = 0,
            Modifier = BuffModifierKind.MoveSpeedMul, ModifierValue = (Fix64)0.7f,
            TagBits = GameplayTags.Slowed,
        });
        BuffStun10 = BuffEngine.Register(new BuffDef
        {
            Id = 2, Stack = BuffStackPolicy.Refresh, MaxStack = 1,
            DurationFrames = 15, // 1s @15Hz — PRD §7.7 陨石 1s stun
            TickIntervalFrames = 0, Modifier = BuffModifierKind.ApplyTag,
            ModifierValue = Fix64.Zero, TagBits = GameplayTags.Stunned,
        });
        BuffShield = BuffEngine.Register(new BuffDef
        {
            Id = 3, Stack = BuffStackPolicy.Refresh, MaxStack = 1,
            DurationFrames = 45, TickIntervalFrames = 15,
            Modifier = BuffModifierKind.HealOverTime, ModifierValue = (Fix64)20, TagBits = 0,
        });
        // PRD §7.7 破甲: -5 ARM per stack, up to 5 stacks, 4s = 60 frames.
        BuffArmorShred5 = BuffEngine.Register(new BuffDef
        {
            Id = 4, Stack = BuffStackPolicy.Stack, MaxStack = 5,
            DurationFrames = 60, TickIntervalFrames = 0,
            Modifier = BuffModifierKind.ArmorReduction, ModifierValue = (Fix64)5, TagBits = 0,
        });

        // ---- Hero base stats — PRD §7.7 exact values ---------------------------------
        // Swordsman (剑士) — PRD §7.7: melee 1.5m, AD physical basic attack
        HeroStats[0] = new HeroBaseStats {
            MaxHp = (Fix64)600, MaxMp = (Fix64)200,
            Ad = (Fix64)60, Ap = (Fix64)0,
            Armor = (Fix64)25, MagicResist = (Fix64)25,
            AttackRange = (Fix64)1.5f, AttackSpeed = (Fix64)0.7f, MoveSpeed = (Fix64)6.5f,
            BasicAttackBase = 0, BasicAttackAdScale1k = 1000, BasicAttackApScale1k = 0,
            BasicAttackDmgType = DamageType.Physical,
        };
        // Mage (法师) — PRD §7.7: ranged 5m, AP magic basic attack 30(+0.3AP)
        HeroStats[1] = new HeroBaseStats {
            MaxHp = (Fix64)480, MaxMp = (Fix64)400,
            Ad = (Fix64)45, Ap = (Fix64)50,
            Armor = (Fix64)18, MagicResist = (Fix64)25,
            AttackRange = (Fix64)5, AttackSpeed = (Fix64)0.6f, MoveSpeed = (Fix64)6.0f,
            BasicAttackBase = 30, BasicAttackAdScale1k = 0, BasicAttackApScale1k = 300,
            BasicAttackDmgType = DamageType.Magic,
        };
        // Marksman (射手) — PRD §7.7: ranged 6m, AD physical basic attack
        HeroStats[2] = new HeroBaseStats {
            MaxHp = (Fix64)520, MaxMp = (Fix64)250,
            Ad = (Fix64)55, Ap = (Fix64)0,
            Armor = (Fix64)20, MagicResist = (Fix64)25,
            AttackRange = (Fix64)6, AttackSpeed = (Fix64)0.85f, MoveSpeed = (Fix64)6.2f,
            BasicAttackBase = 0, BasicAttackAdScale1k = 1000, BasicAttackApScale1k = 0,
            BasicAttackDmgType = DamageType.Physical,
        };

        // ---- Skills (12 × PRD §7.7) --------------------------------------------------
        Span<EffectStep> s = stackalloc EffectStep[4];

        // ===================== Swordsman (HeroDefId=0) =====================

        // Q: 冲锋 — 0.3s pre-cast (5f @15Hz), dash 3m forward + 80+0.6AD Physical + Knockback 3m.
        s[0] = new EffectStep { Kind = EffectKind.Teleport };          // move to aim (3m cap handled by CastRange)
        s[1] = StepPhysDamage(80, 600);
        s[2] = new EffectStep { Kind = EffectKind.Knockback, Param = (Fix64)3 }; // push 3m
        {
            var def = SkillCfg(id: 1000, owner: 0, cdFrames: 90, mana: 50, range: 3,
                               cast: CastType.Direction, shape: HitShape.Circle, paramA: (Fix64)1.5f, paramB: 0);
            def.PreCastFrames = 5; // PRD §7.7: 0.3s × 15Hz = 4.5 → 5 frames
            HeroSkills[0, 0] = SkillEngine.Register(def, s.Slice(0, 3));
        }

        // W: 旋风斩 — 2m circle AOE 60+0.5AD Physical.
        s[0] = StepPhysDamage(60, 500);
        HeroSkills[0, 1] = SkillEngine.Register(SkillCfg(
            id: 1001, owner: 0, cdFrames: 120, mana: 60, range: 0,
            cast: CastType.Instant, shape: HitShape.Circle, paramA: 2, paramB: 0), s.Slice(0, 1));

        // E: 破甲 — PASSIVE (SkillFlags bit0=1). On each basic attack: apply ArmorShred buff.
        // Store buff id as Fix64 integer value so (int)step.Param recovers the id correctly.
        s[0] = new EffectStep { Kind = EffectKind.ArmorShred, Param = (Fix64)(int)BuffArmorShred5 };
        HeroSkills[0, 2] = SkillEngine.Register(SkillCfgPassive(id: 1002, owner: 0), s.Slice(0, 1));

        // R: 斩首 — 4m range target ≤30% HP: 200+1.0AD True.
        s[0] = new EffectStep { Kind = EffectKind.Damage, Target = EffectTarget.HitTargets, DmgType = DamageType.True,
                                Param = (Fix64)200, Param2 = 1000, Param3 = 0 };
        {
            var def = SkillCfg(id: 1003, owner: 0, cdFrames: 450, mana: 100, range: 4,
                               cast: CastType.Position, shape: HitShape.Circle, paramA: 4, paramB: 0);
            def.CastTargetHpMaxPct = (Fix64)0.30f;
            HeroSkills[0, 3] = SkillEngine.Register(def, s.Slice(0, 1));
        }

        // ===================== Mage (HeroDefId=1) =====================

        // Q: 火球 — 0.4s pre-cast (6 frames), projectile speed 12m/s, range 8m (life=10f @15Hz).
        //    On-hit: 2m AOE 80+0.7AP Magic (sibling Damage step read in SpawnProjectile case).
        s[0] = new EffectStep { Kind = EffectKind.SpawnProjectile, Param2 = 120, Param3 = 10 }; // speed=12m/s, life=10f
        s[1] = StepMagicDamage(80, 700);   // sibling damage step (read by SpawnProjectile handler)
        {
            var def = SkillCfg(id: 1100, owner: 1, cdFrames: 12, mana: 30, range: 8,
                               cast: CastType.Direction, shape: HitShape.Circle, paramA: 2, paramB: 0);
            def.PreCastFrames = 6;
            HeroSkills[1, 0] = SkillEngine.Register(def, s.Slice(0, 2));
        }

        // W: 冰墙 — 4m wall (HalfLen=2), 3s = 45 frames.
        s[0] = new EffectStep { Kind = EffectKind.SpawnWall, Param = (Fix64)2, Param2 = 45 };
        HeroSkills[1, 1] = SkillEngine.Register(SkillCfg(
            id: 1101, owner: 1, cdFrames: 270, mana: 80, range: 6,
            cast: CastType.Position, shape: HitShape.None, paramA: 0, paramB: 0), s.Slice(0, 1));

        // E: 闪现 — 3m blink teleport, 90s CD = 1350 frames.
        s[0] = new EffectStep { Kind = EffectKind.Teleport };
        HeroSkills[1, 2] = SkillEngine.Register(SkillCfg(
            id: 1102, owner: 1, cdFrames: 1350, mana: 60, range: 3,
            cast: CastType.Direction, shape: HitShape.None, paramA: 0, paramB: 0), s.Slice(0, 1));

        // R: 陨石 — 0.8s delay (12f), 3m AOE, 250+1.2AP Magic + 1s stun (BuffStun10).
        //    Stationary AoE projectile spawned by SpawnDelayedAoe; BuffOnHit carries the stun id.
        s[0] = new EffectStep
        {
            Kind = EffectKind.Damage, Target = EffectTarget.HitTargets, DmgType = DamageType.Magic,
            Param = (Fix64)250, Param2 = 0, Param3 = 1200,   // AP scale × 1000 = 1.2 AP
            BuffOnHit = BuffStun10,                            // apply 1s stun on hit
            DelayFrames = 12,
        };
        HeroSkills[1, 3] = SkillEngine.Register(SkillCfg(
            id: 1103, owner: 1, cdFrames: 600, mana: 130, range: 10,
            cast: CastType.Position, shape: HitShape.Circle, paramA: 3, paramB: 0), s.Slice(0, 1));

        // ===================== Marksman (HeroDefId=2) =====================

        // Q: 穿透箭 — pierce line projectile, 8m, speed 15m/s (life=8f), 60+0.7AD Physical.
        //    pierce flag = bit1 of Flags.
        s[0] = new EffectStep { Kind = EffectKind.SpawnProjectile, Param2 = 150, Param3 = 8, Flags = 2 }; // pierce
        s[1] = StepPhysDamage(60, 700);
        HeroSkills[2, 0] = SkillEngine.Register(SkillCfg(
            id: 1200, owner: 2, cdFrames: 9, mana: 30, range: 8,
            cast: CastType.Direction, shape: HitShape.Line, paramA: 8, paramB: (Fix64)0.8f), s.Slice(0, 2));

        // W: 位移射击 — retreat 2m (reverse Teleport) + shoot 80+0.8AD forward.
        s[0] = new EffectStep { Kind = EffectKind.Teleport, Flags = 4, Param = (Fix64)2 }; // bit2=reverse
        s[1] = new EffectStep { Kind = EffectKind.SpawnProjectile, Param2 = 120, Param3 = 6 };
        s[2] = StepPhysDamage(80, 800);
        HeroSkills[2, 1] = SkillEngine.Register(SkillCfg(
            id: 1201, owner: 2, cdFrames: 135, mana: 60, range: 6,
            cast: CastType.Direction, shape: HitShape.None, paramA: 0, paramB: 0), s.Slice(0, 3));

        // E: 狩猎之眼 — PASSIVE. On basic attack if target HP<50%: × 1.2 damage.
        s[0] = new EffectStep { Kind = EffectKind.ConditionalDamageMul, Param = (Fix64)1.2f, Param2 = 50 }; // Param2=threshold %
        HeroSkills[2, 2] = SkillEngine.Register(SkillCfgPassive(id: 1202, owner: 2), s.Slice(0, 1));

        // R: 狙击 — 1s channel (15 frames pre-cast), 12m line pierce, 300+1.5AD Physical.
        s[0] = new EffectStep { Kind = EffectKind.SpawnProjectile, Param2 = 200, Param3 = 9 }; // speed=20m/s, life=9f (~12m)
        s[1] = StepPhysDamage(300, 1500);
        {
            var def = SkillCfg(id: 1203, owner: 2, cdFrames: 600, mana: 100, range: 12,
                               cast: CastType.Direction, shape: HitShape.Line, paramA: 12, paramB: (Fix64)0.5f);
            def.PreCastFrames = 15;
            HeroSkills[2, 3] = SkillEngine.Register(def, s.Slice(0, 2));
        }

        _registered = true;
    }

    private static SkillDef SkillCfg(ushort id, byte owner, uint cdFrames, int mana, int range,
                                     CastType cast, HitShape shape, Fix64 paramA, Fix64 paramB)
        => new SkillDef
        {
            Id = id, OwnerHeroDefId = owner,
            CdFrames = cdFrames, ManaCost = (Fix64)mana, CastRange = (Fix64)range,
            Cast = cast, HitShape = shape, HitParamA = paramA, HitParamB = paramB,
        };

    private static SkillDef SkillCfgPassive(ushort id, byte owner)
        => new SkillDef
        {
            Id = id, OwnerHeroDefId = owner,
            CdFrames = 0, ManaCost = Fix64.Zero, CastRange = Fix64.Zero,
            Cast = CastType.Instant, HitShape = HitShape.None,
            SkillFlags = 1, // bit0 = IsPassive
        };

    private static EffectStep StepPhysDamage(int base_, int adScale1k)
        => new EffectStep
        {
            Kind = EffectKind.Damage, Target = EffectTarget.HitTargets, DmgType = DamageType.Physical,
            Param = (Fix64)base_, Param2 = adScale1k, Param3 = 0,
        };

    private static EffectStep StepMagicDamage(int base_, int apScale1k)
        => new EffectStep
        {
            Kind = EffectKind.Damage, Target = EffectTarget.HitTargets, DmgType = DamageType.Magic,
            Param = (Fix64)base_, Param2 = 0, Param3 = apScale1k,
        };

    /// <summary>Apply per-hero base stats keyed by HeroDefId. Idempotent if called repeatedly.</summary>
    [NoGC]
    public static void ApplyBaseStats(ref Hero h)
    {
        if (MOBA.Logic.Config.ConfigManager.IsLoaded)
        {
            ref var cfg = ref MOBA.Logic.Config.ConfigManager.GetHero(h.HeroDefId % HeroCount);
            h.MaxHp = cfg.MaxHp;  h.Hp = cfg.MaxHp;
            h.MaxMp = cfg.MaxMp;  h.Mp = cfg.MaxMp;
            h.Ad    = cfg.Ad;     h.Ap = cfg.Ap;
            h.Armor = cfg.Armor;  h.MagicResist = cfg.MagicResist;
            h.AttackRange = cfg.AttackRange;
            h.AttackSpeed = cfg.AttackSpeed;
            h.MoveSpeed   = cfg.MoveSpeed;
            return;
        }
        ref var s = ref HeroStats[h.HeroDefId % HeroCount];
        h.MaxHp = s.MaxHp;       h.Hp = s.MaxHp;
        h.MaxMp = s.MaxMp;       h.Mp = s.MaxMp;
        h.Ad = s.Ad;             h.Ap = s.Ap;
        h.Armor = s.Armor;       h.MagicResist = s.MagicResist;
        h.AttackRange = s.AttackRange;
        h.AttackSpeed = s.AttackSpeed;
        h.MoveSpeed = s.MoveSpeed;
    }
}

