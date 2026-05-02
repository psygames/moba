// SPDX-License-Identifier: MIT
// §8 — config.bin binary layout (hand-rolled, project-consistent style).
//
// Format:
//   [Header]   "MCFG" (4) + uint16 version=1 (2) + uint16 sectionCount (2)
//   [Sections] each: uint16 sectionId (2) + uint32 recordCount (4) + [records]
//
// Section IDs / record sizes (little-endian throughout):
//   0x0001  CfgHero      (see HeroSize)
//   0x0002  SkillDef     (see SkillDefSize)
//   0x0003  EffectStep   (see EffectStepSize)
//   0x0004  BuffDef      (see BuffDefSize)
//   0x0005  ItemDef      (see ItemDefSize)
//   0x0006  CfgLevel     (see LevelSize)
//   0x0007  CfgLane      (see LaneSize — fixed 8 waypoints)

using System;
using System.Buffers.Binary;
using MOBA.Logic.Sim;

namespace MOBA.Logic.Config;

public static class ConfigBinary
{
    // ── Magic / Version ────────────────────────────────────────────────────
    private static readonly byte[] Magic = { (byte)'M', (byte)'C', (byte)'F', (byte)'G' };
    private const ushort Version = 2;

    // ── Section IDs ────────────────────────────────────────────────────────
    private const ushort SecHeroes     = 0x0001;
    private const ushort SecSkillDefs  = 0x0002;
    private const ushort SecSteps      = 0x0003;
    private const ushort SecBuffDefs   = 0x0004;
    private const ushort SecItemDefs   = 0x0005;
    private const ushort SecLevels     = 0x0006;
    private const ushort SecLanes      = 0x0007;

    // ── Per-record byte sizes ──────────────────────────────────────────────
    // CfgHero: 1(id)+1(pad)+8(skills 4×u16)+9×8(base stats)+4×8(growth) = 1+1+8+72+32 = 114
    public const int HeroSize        = 114;
    // SkillDef: u16 id + u8 owner + u8 stepCount + u16 stepStart + u32 cdFrames + 8 mana + 8 range
    //           + u8 cast + u8 hitShape + u32 preCastFrames + 8 hitA + 8 hitB + u8 skillFlags + u8 pad + 8 castTargetHpMaxPct
    //           = 2+1+1+2+4+8+8+1+1+4+8+8+1+1+8 = 58
    public const int SkillDefSize    = 58;
    // EffectStep: u32 delay + u8 kind + u8 target + u8 dmgType + u8 flags + 8 param + i32 p2 + i32 p3 + u16 buffOnHit + u16 pad = 28
    public const int EffectStepSize  = 28;
    // BuffDef: u16 id + u8 stack + u8 maxStack + u32 dur + u32 tick + u8 mod + u8 pad + 8 modVal + u64 tags = 30
    public const int BuffDefSize     = 30;
    // ItemDef: u16 id + u8 slot + u8 pad + u32 cost + 7×8 stats + u8 addCdr + u8 pad = 66
    public const int ItemDefSize     = 66;
    // CfgLevel: u8 level + u8 pad + u16 pad + u32 expRequired = 8
    public const int LevelSize       = 8;
    // CfgLane: u8 id + u8 wptCount + u8[2] pad + 8×(8+8) waypoints = 4+128 = 132
    public const int LaneSize        = 132;

    private const int HeaderSize     = 4 + 2 + 2;       // magic + version + sectionCount
    private const int SectionHdrSize = 2 + 4;            // sectionId + recordCount

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Bake all config tables into a byte[] (equivalent to Luban output).</summary>
    public static byte[] Bake(
        CfgHero[]   heroes,
        CfgLevel[]  levels,
        CfgLane[]   lanes)
    {
        // Count totals using current SkillEngine / BuffEngine / Items tables
        // (caller must have called BuiltinContent.Register() first).
        int nHeroes   = heroes.Length;
        int nSkills   = SkillEngine.DefCount;
        int nSteps    = SkillEngine.StepCount;
        int nBuffs    = BuffEngine.DefCount;
        int nItems    = Items.DefCount;
        int nLevels   = levels.Length;
        int nLanes    = lanes.Length;

        int totalBytes = HeaderSize
            + (SectionHdrSize + nHeroes  * HeroSize)
            + (SectionHdrSize + nSkills  * SkillDefSize)
            + (SectionHdrSize + nSteps   * EffectStepSize)
            + (SectionHdrSize + nBuffs   * BuffDefSize)
            + (SectionHdrSize + nItems   * ItemDefSize)
            + (SectionHdrSize + nLevels  * LevelSize)
            + (SectionHdrSize + nLanes   * LaneSize);

        var buf = new byte[totalBytes];
        int off = 0;

        // Header
        buf[off++] = Magic[0]; buf[off++] = Magic[1];
        buf[off++] = Magic[2]; buf[off++] = Magic[3];
        WriteU16(buf, ref off, Version);
        WriteU16(buf, ref off, 7); // 7 sections

        // Sections
        WriteSection_Heroes    (buf, ref off, heroes);
        WriteSection_SkillDefs (buf, ref off, nSkills, nSteps);
        WriteSection_EffectSteps(buf, ref off, nSteps);
        WriteSection_BuffDefs  (buf, ref off, nBuffs);
        WriteSection_ItemDefs  (buf, ref off, nItems);
        WriteSection_Levels    (buf, ref off, levels);
        WriteSection_Lanes     (buf, ref off, lanes);

        return buf;
    }

    // ── Load (parse) ───────────────────────────────────────────────────────

    /// <summary>
    /// Parse config.bin bytes.
    /// Populates ConfigManager tables and re-registers SkillEngine / BuffEngine / Items.
    /// Returns false if magic/version mismatch.
    /// </summary>
    public static bool TryLoad(ReadOnlySpan<byte> data, ConfigManager mgr)
    {
        if (data.Length < HeaderSize) return false;
        int off = 0;
        if (data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3]) return false;
        off = 4;
        ushort ver = ReadU16(data, ref off);
        if (ver != Version) return false;
        ushort secCount = ReadU16(data, ref off);

        for (int s = 0; s < secCount; s++)
        {
            if (off + SectionHdrSize > data.Length) return false;
            ushort secId = ReadU16(data, ref off);
            uint   count = ReadU32(data, ref off);

            switch (secId)
            {
                case SecHeroes:
                    LoadHeroes(data, ref off, (int)count, mgr);
                    break;
                case SecSkillDefs:
                    LoadSkillDefs(data, ref off, (int)count);
                    break;
                case SecSteps:
                    LoadEffectSteps(data, ref off, (int)count);
                    break;
                case SecBuffDefs:
                    LoadBuffDefs(data, ref off, (int)count, mgr);
                    break;
                case SecItemDefs:
                    LoadItemDefs(data, ref off, (int)count);
                    break;
                case SecLevels:
                    LoadLevels(data, ref off, (int)count, mgr);
                    break;
                case SecLanes:
                    LoadLanes(data, ref off, (int)count, mgr);
                    break;
                default:
                    // Unknown section — skip (future compat)
                    off += (int)count; // best-effort skip; layout unknown
                    break;
            }
        }
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static void WriteSection_Heroes(byte[] buf, ref int off, CfgHero[] heroes)
    {
        WriteU16(buf, ref off, SecHeroes);
        WriteU32(buf, ref off, (uint)heroes.Length);
        foreach (ref var h in heroes.AsSpan())
        {
            buf[off++] = h.Id;
            buf[off++] = 0; // pad
            WriteU16(buf, ref off, h.SkillQ);
            WriteU16(buf, ref off, h.SkillW);
            WriteU16(buf, ref off, h.SkillE);
            WriteU16(buf, ref off, h.SkillR);
            WriteF64(buf, ref off, h.MaxHp);
            WriteF64(buf, ref off, h.MaxMp);
            WriteF64(buf, ref off, h.Ad);
            WriteF64(buf, ref off, h.Ap);
            WriteF64(buf, ref off, h.Armor);
            WriteF64(buf, ref off, h.MagicResist);
            WriteF64(buf, ref off, h.AttackRange);
            WriteF64(buf, ref off, h.AttackSpeed);
            WriteF64(buf, ref off, h.MoveSpeed);
            WriteF64(buf, ref off, h.HpPerLv);
            WriteF64(buf, ref off, h.MpPerLv);
            WriteF64(buf, ref off, h.AdPerLv);
            WriteF64(buf, ref off, h.ApPerLv);
        }
    }

    private static void WriteSection_SkillDefs(byte[] buf, ref int off, int count, int stepCount)
    {
        WriteU16(buf, ref off, SecSkillDefs);
        WriteU32(buf, ref off, (uint)count);
        for (int i = 0; i < count; i++)
        {
            ref var d = ref SkillEngine.Defs[i];
            WriteU16(buf, ref off, d.Id);
            buf[off++] = d.OwnerHeroDefId;
            buf[off++] = d.StepCount;
            WriteU16(buf, ref off, d.StepStart);
            WriteU32(buf, ref off, d.CdFrames);
            WriteF64(buf, ref off, d.ManaCost);
            WriteF64(buf, ref off, d.CastRange);
            buf[off++] = (byte)d.Cast;
            buf[off++] = (byte)d.HitShape;
            WriteU32(buf, ref off, d.PreCastFrames);
            WriteF64(buf, ref off, d.HitParamA);
            WriteF64(buf, ref off, d.HitParamB);
            buf[off++] = d.SkillFlags;
            buf[off++] = 0; // pad
            WriteF64(buf, ref off, d.CastTargetHpMaxPct);
        }
    }

    private static void WriteSection_EffectSteps(byte[] buf, ref int off, int count)
    {
        WriteU16(buf, ref off, SecSteps);
        WriteU32(buf, ref off, (uint)count);
        for (int i = 0; i < count; i++)
        {
            ref var s = ref SkillEngine.Steps[i];
            WriteU32(buf, ref off, s.DelayFrames);
            buf[off++] = (byte)s.Kind;
            buf[off++] = (byte)s.Target;
            buf[off++] = (byte)s.DmgType;
            buf[off++] = s.Flags;
            WriteF64(buf, ref off, s.Param);
            WriteI32(buf, ref off, s.Param2);
            WriteI32(buf, ref off, s.Param3);
            WriteU16(buf, ref off, s.BuffOnHit);
            WriteU16(buf, ref off, 0); // pad
        }
    }

    private static void WriteSection_BuffDefs(byte[] buf, ref int off, int count)
    {
        WriteU16(buf, ref off, SecBuffDefs);
        WriteU32(buf, ref off, (uint)count);
        for (int i = 0; i < count; i++)
        {
            ref var b = ref BuffEngine.Defs[i];
            WriteU16(buf, ref off, b.Id);
            buf[off++] = (byte)b.Stack;
            buf[off++] = b.MaxStack;
            WriteU32(buf, ref off, b.DurationFrames);
            WriteU32(buf, ref off, b.TickIntervalFrames);
            buf[off++] = (byte)b.Modifier;
            buf[off++] = 0; // pad
            WriteF64(buf, ref off, b.ModifierValue);
            WriteU64(buf, ref off, b.TagBits);
        }
    }

    private static void WriteSection_ItemDefs(byte[] buf, ref int off, int count)
    {
        WriteU16(buf, ref off, SecItemDefs);
        WriteU32(buf, ref off, (uint)count);
        for (int i = 0; i < count; i++)
        {
            ref var t = ref Items.Defs[i];
            WriteU16(buf, ref off, t.Id);
            buf[off++] = (byte)t.Slot;
            buf[off++] = 0; // pad
            WriteU32(buf, ref off, t.Cost);
            WriteF64(buf, ref off, t.AddAd);
            WriteF64(buf, ref off, t.AddAp);
            WriteF64(buf, ref off, t.AddArmor);
            WriteF64(buf, ref off, t.AddMr);
            WriteF64(buf, ref off, t.AddMaxHp);
            WriteF64(buf, ref off, t.AddMaxMp);
            WriteF64(buf, ref off, t.AddMoveSpeed);
            buf[off++] = t.AddCdr;
            buf[off++] = 0; // pad
        }
    }

    private static void WriteSection_Levels(byte[] buf, ref int off, CfgLevel[] levels)
    {
        WriteU16(buf, ref off, SecLevels);
        WriteU32(buf, ref off, (uint)levels.Length);
        foreach (ref var lv in levels.AsSpan())
        {
            buf[off++] = lv.Level;
            buf[off++] = 0;
            WriteU16(buf, ref off, 0); // pad
            WriteU32(buf, ref off, lv.ExpRequired);
        }
    }

    private static void WriteSection_Lanes(byte[] buf, ref int off, CfgLane[] lanes)
    {
        WriteU16(buf, ref off, SecLanes);
        WriteU32(buf, ref off, (uint)lanes.Length);
        foreach (ref var ln in lanes.AsSpan())
        {
            buf[off++] = ln.Id;
            buf[off++] = ln.WaypointCount;
            buf[off++] = 0; buf[off++] = 0; // pad
            // Always write 8 waypoint slots; unused slots are zeros
            for (int w = 0; w < 8; w++)
            {
                if (ln.Waypoints != null && w < ln.Waypoints.Length)
                {
                    WriteF64(buf, ref off, ln.Waypoints[w].X);
                    WriteF64(buf, ref off, ln.Waypoints[w].Y);
                }
                else
                {
                    WriteF64(buf, ref off, Fix64.Zero);
                    WriteF64(buf, ref off, Fix64.Zero);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Read helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static void LoadHeroes(ReadOnlySpan<byte> data, ref int off, int count, ConfigManager mgr)
    {
        var arr = new CfgHero[count];
        for (int i = 0; i < count; i++)
        {
            arr[i].Id      = data[off++];
            off++;          // pad
            arr[i].SkillQ  = ReadU16(data, ref off);
            arr[i].SkillW  = ReadU16(data, ref off);
            arr[i].SkillE  = ReadU16(data, ref off);
            arr[i].SkillR  = ReadU16(data, ref off);
            arr[i].MaxHp   = ReadF64(data, ref off);
            arr[i].MaxMp   = ReadF64(data, ref off);
            arr[i].Ad      = ReadF64(data, ref off);
            arr[i].Ap      = ReadF64(data, ref off);
            arr[i].Armor   = ReadF64(data, ref off);
            arr[i].MagicResist  = ReadF64(data, ref off);
            arr[i].AttackRange  = ReadF64(data, ref off);
            arr[i].AttackSpeed  = ReadF64(data, ref off);
            arr[i].MoveSpeed    = ReadF64(data, ref off);
            arr[i].HpPerLv = ReadF64(data, ref off);
            arr[i].MpPerLv = ReadF64(data, ref off);
            arr[i].AdPerLv = ReadF64(data, ref off);
            arr[i].ApPerLv = ReadF64(data, ref off);
        }
        mgr.Heroes = arr;
    }

    private static void LoadSkillDefs(ReadOnlySpan<byte> data, ref int off, int count)
    {
        // Reset and re-populate SkillEngine.Defs.
        // NOTE: Steps section must follow so StepStart refs are still valid.
        SkillEngine.DefCount = count;
        for (int i = 0; i < count; i++)
        {
            ref var d = ref SkillEngine.Defs[i];
            d.Id              = ReadU16(data, ref off);
            d.OwnerHeroDefId  = data[off++];
            d.StepCount       = data[off++];
            d.StepStart       = ReadU16(data, ref off);
            d.CdFrames        = ReadU32(data, ref off);
            d.ManaCost        = ReadF64(data, ref off);
            d.CastRange       = ReadF64(data, ref off);
            d.Cast            = (CastType)data[off++];
            d.HitShape        = (HitShape)data[off++];
            d.PreCastFrames   = ReadU32(data, ref off);
            d.HitParamA       = ReadF64(data, ref off);
            d.HitParamB       = ReadF64(data, ref off);
            d.SkillFlags      = data[off++];
            off++;                // pad
            d.CastTargetHpMaxPct = ReadF64(data, ref off);
        }
    }

    private static void LoadEffectSteps(ReadOnlySpan<byte> data, ref int off, int count)
    {
        SkillEngine.StepCount = count;
        for (int i = 0; i < count; i++)
        {
            ref var s = ref SkillEngine.Steps[i];
            s.DelayFrames = ReadU32(data, ref off);
            s.Kind        = (EffectKind)data[off++];
            s.Target      = (EffectTarget)data[off++];
            s.DmgType     = (DamageType)data[off++];
            s.Flags       = data[off++];
            s.Param       = ReadF64(data, ref off);
            s.Param2      = ReadI32(data, ref off);
            s.Param3      = ReadI32(data, ref off);
            s.BuffOnHit   = ReadU16(data, ref off);
            ReadU16(data, ref off); // pad
        }
    }

    private static void LoadBuffDefs(ReadOnlySpan<byte> data, ref int off, int count, ConfigManager mgr)
    {
        BuffEngine.DefCount = count;
        for (int i = 0; i < count; i++)
        {
            ref var b = ref BuffEngine.Defs[i];
            b.Id                 = ReadU16(data, ref off);
            b.Stack              = (BuffStackPolicy)data[off++];
            b.MaxStack           = data[off++];
            b.DurationFrames     = ReadU32(data, ref off);
            b.TickIntervalFrames = ReadU32(data, ref off);
            b.Modifier           = (BuffModifierKind)data[off++];
            off++;                // pad
            b.ModifierValue      = ReadF64(data, ref off);
            b.TagBits            = ReadU64(data, ref off);
        }
        // Resolve canonical buff ID → runtime index mappings.
        for (int i = 0; i < count; i++)
        {
            switch (BuffEngine.Defs[i].Id)
            {
                case 1: mgr.BuffSlow30Idx  = (ushort)i; break;
                case 2: mgr.BuffStun10Idx  = (ushort)i; break;
                case 3: mgr.BuffShieldIdx  = (ushort)i; break;
            }
        }
    }

    private static void LoadItemDefs(ReadOnlySpan<byte> data, ref int off, int count)
    {
        Items.DefCount = count;
        for (int i = 0; i < count; i++)
        {
            ref var t = ref Items.Defs[i];
            t.Id           = ReadU16(data, ref off);
            t.Slot         = (ItemSlot)data[off++];
            off++;          // pad
            t.Cost         = ReadU32(data, ref off);
            t.AddAd        = ReadF64(data, ref off);
            t.AddAp        = ReadF64(data, ref off);
            t.AddArmor     = ReadF64(data, ref off);
            t.AddMr        = ReadF64(data, ref off);
            t.AddMaxHp     = ReadF64(data, ref off);
            t.AddMaxMp     = ReadF64(data, ref off);
            t.AddMoveSpeed = ReadF64(data, ref off);
            t.AddCdr       = data[off++];
            off++;          // pad
        }
    }

    private static void LoadLevels(ReadOnlySpan<byte> data, ref int off, int count, ConfigManager mgr)
    {
        var arr = new CfgLevel[count];
        for (int i = 0; i < count; i++)
        {
            arr[i].Level       = data[off++];
            off++;              // pad
            off += 2;           // pad
            arr[i].ExpRequired = ReadU32(data, ref off);
        }
        mgr.Levels = arr;
    }

    private static void LoadLanes(ReadOnlySpan<byte> data, ref int off, int count, ConfigManager mgr)
    {
        var arr = new CfgLane[count];
        for (int i = 0; i < count; i++)
        {
            arr[i].Id            = data[off++];
            arr[i].WaypointCount = data[off++];
            off += 2;            // pad
            arr[i].Waypoints     = new CfgWaypoint[arr[i].WaypointCount];
            for (int w = 0; w < 8; w++) // always read 8 slots
            {
                var x = ReadF64(data, ref off);
                var y = ReadF64(data, ref off);
                if (w < arr[i].WaypointCount)
                    arr[i].Waypoints[w] = new CfgWaypoint { X = x, Y = y };
            }
        }
        mgr.Lanes = arr;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Primitive read / write
    // ═══════════════════════════════════════════════════════════════════════

    private static void WriteU16(byte[] b, ref int o, ushort v)
    { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v); o += 2; }

    private static void WriteU32(byte[] b, ref int o, uint v)
    { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v); o += 4; }

    private static void WriteI32(byte[] b, ref int o, int v)
    { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o, 4), v); o += 4; }

    private static void WriteU64(byte[] b, ref int o, ulong v)
    { BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v); o += 8; }

    private static void WriteF64(byte[] b, ref int o, Fix64 v)
    { BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(o, 8), v.RawValue); o += 8; }

    private static ushort ReadU16(ReadOnlySpan<byte> b, ref int o)
    { var v = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2)); o += 2; return v; }

    private static uint ReadU32(ReadOnlySpan<byte> b, ref int o)
    { var v = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4)); o += 4; return v; }

    private static int ReadI32(ReadOnlySpan<byte> b, ref int o)
    { var v = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4)); o += 4; return v; }

    private static ulong ReadU64(ReadOnlySpan<byte> b, ref int o)
    { var v = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o, 8)); o += 8; return v; }

    private static Fix64 ReadF64(ReadOnlySpan<byte> b, ref int o)
    { var v = Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o, 8))); o += 8; return v; }
}
