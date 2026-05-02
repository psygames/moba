// SPDX-License-Identifier: MIT
// M5.5 — base crystals (nexus), respawn refinement, GameOver.
//
// Per PRD §7.6:
//   - 死亡复活时间：baseSec(level*2 + 5) + gameMinute*2，上限 60s
//   - 复活在己方泉水
//   - 任一基地水晶 HP=0 即对方获胜，写 S2C_GameOver { winnerTeam }，房间转 Ended

using System;

namespace MOBA.Logic.Sim;

public struct Crystal
{
    public TSVector2 Pos;
    public Fix64 Hp, MaxHp;
    public Fix64 Armor;
    public Team Team;
    public bool Alive;
    public uint AttackCdEndFrame;
}

public static class CrystalSystem
{
    public const int Count = 2;
    public static readonly Fix64 CrystalMaxHp = (Fix64)5000;
    public static readonly Fix64 CrystalArmor = (Fix64)40;
    public static readonly Fix64 CrystalAttackRange = (Fix64)5;
    public const int CrystalAttackCdFrames = 30; // 2s @15Hz
    public static readonly Fix64 CrystalAd = (Fix64)200;

    [NoGC]
    public static void Init(Crystal[] crystals)
    {
        ref var b = ref crystals[(int)Team.Blue];
        b.Pos = Lanes.BlueTowers[1];
        b.MaxHp = CrystalMaxHp; b.Hp = CrystalMaxHp;
        b.Armor = CrystalArmor;
        b.Team = Team.Blue;
        b.Alive = true;
        b.AttackCdEndFrame = 0;

        ref var r = ref crystals[(int)Team.Red];
        r.Pos = Lanes.RedTowers[1];
        r.MaxHp = CrystalMaxHp; r.Hp = CrystalMaxHp;
        r.Armor = CrystalArmor;
        r.Team = Team.Red;
        r.Alive = true;
        r.AttackCdEndFrame = 0;
    }

    /// <summary>
    /// Each tick: enemy minions adjacent to a crystal whittle it down. Crystals also
    /// retaliate against the closest enemy hero/minion in range.
    /// </summary>
    [NoGC]
    public static void Tick(Crystal[] crystals, Hero[] heroes, Minion[] minions, uint frame,
        Span<GameSystems.DamageEvent> q, ref int count)
    {
        for (int c = 0; c < crystals.Length; c++)
        {
            ref var cr = ref crystals[c];
            if (!cr.Alive) continue;
            // Crystals do NOT take damage automatically every tick; damage is applied via
            // the standard DamageEvent path. We just need the crystal to attack enemies in range.
            if (frame < cr.AttackCdEndFrame) continue;

            // Pick closest enemy hero, then minion.
            Fix64 r2 = CrystalAttackRange * CrystalAttackRange;
            Fix64 best = r2;
            int picked = -1;
            UnitKind kind = UnitKind.None;
            for (int i = 0; i < heroes.Length; i++)
            {
                ref var h = ref heroes[i];
                if (!h.Alive) continue;
                Team hTeam = i < 5 ? Team.Blue : Team.Red;
                if (hTeam == cr.Team) continue;
                Fix64 dx = h.Pos.X - cr.Pos.X, dy = h.Pos.Y - cr.Pos.Y;
                Fix64 d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; picked = i; kind = UnitKind.Hero; }
            }
            if (picked < 0)
            {
                for (int i = 0; i < minions.Length; i++)
                {
                    ref var m = ref minions[i];
                    if (!m.Alive || m.Team == cr.Team) continue;
                    Fix64 dx = m.Pos.X - cr.Pos.X, dy = m.Pos.Y - cr.Pos.Y;
                    Fix64 d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; picked = i; kind = UnitKind.Minion; }
                }
            }
            if (picked < 0) continue;
            var tgt = new UnitRef
            {
                Kind = kind,
                Index = (ushort)picked,
                BornFrame = kind == UnitKind.Minion ? minions[picked].BornFrame : 0u,
            };
            GameSystems.EnqueueDamage(q, ref count, tgt, CrystalAd, frame);
            cr.AttackCdEndFrame = frame + (uint)CrystalAttackCdFrames;
        }
    }

    /// <summary>Apply pending damage to crystals (called from ResolveDamage extension).</summary>
    [NoGC]
    public static void ApplyDamage(Crystal[] crystals, int teamIdx, Fix64 raw)
    {
        ref var c = ref crystals[teamIdx];
        if (!c.Alive) return;
        Fix64 mul = (Fix64)100 / ((Fix64)100 + c.Armor);
        Fix64 dmg = raw * mul;
        if (dmg < (Fix64)1) dmg = (Fix64)1;
        c.Hp -= dmg;
        if (c.Hp <= Fix64.Zero) c.Alive = false;
    }
}

public static class Respawn
{
    public const int RespawnCapFrames = 900; // 60s @15Hz

    /// <summary>
    /// PRD §7.6: baseSec = level*2 + 5; total = baseSec + gameMinute*2; cap 60s.
    /// </summary>
    [NoGC]
    public static int FramesFor(byte level, uint frame)
    {
        int gameMinute = (int)(frame / (15u * 60u));
        int baseSec = level * 2 + 5;
        int totalSec = baseSec + gameMinute * 2;
        if (totalSec > 60) totalSec = 60;
        return totalSec * 15;
    }

    [NoGC]
    public static void RespawnAtFountain(ref Hero h, int slot)
    {
        Team team = slot < 5 ? Team.Blue : Team.Red;
        h.Pos = team == Team.Blue ? Lanes.BlueTowers[1] : Lanes.RedTowers[1];
        h.Hp = h.MaxHp;
        h.Mp = h.MaxMp;
        h.Alive = true;
        h.Tags = 0;
        h.AttackCdEndFrame = 0;
        h.Target = UnitRef.None;
    }
}
