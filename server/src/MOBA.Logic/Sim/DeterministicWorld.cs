// SPDX-License-Identifier: MIT
using System;
using System.Buffers;
using System.Buffers.Binary;
using MOBA.Logic.Physics;
using MOBA.Shared.Math;
using MOBA.Shared.Protocol;

namespace MOBA.Logic.Sim;

/// <summary>
/// Authoritative world. Hosts:
///   * 10 player circles in a 50×50 box (M3 baseline — physics only).
///   * Optional M4 game systems (lanes / minions / towers / hero auto-attack)
///     enabled by setting <see cref="EnableGameplay"/> = true before the first Tick.
///
/// Tick is GC-free in steady state; Hash reuses a per-instance buffer.
/// </summary>
public sealed class DeterministicWorld
{
    public const int PlayerCount = 10;
    public const int TicksPerSecond = 15;

    public readonly PhysicsWorldManager Physics;
    public readonly EntityId[] Players = new EntityId[PlayerCount];
    public uint Frame;

    // ---- M4 state (only ticked when EnableGameplay) -------------------------------
    public bool EnableGameplay;
    public readonly Hero[] Heroes = new Hero[PlayerCount];
    public readonly Minion[] Minions = new Minion[GameSystems.MaxMinions];
    public readonly Tower[] Towers = new Tower[GameSystems.TowerCount];
    public readonly JungleMonster[] JungleMonsters = new JungleMonster[JungleSystem.MaxMonsters];
    public int AliveMinionCount;
    public int WavesSpawned;
    private readonly GameSystems.DamageEvent[] _damageScratch = new GameSystems.DamageEvent[1024];

    // ---- M5 state ------------------------------------------------------------------
    public readonly BuffInstance[,] Buffs = new BuffInstance[PlayerCount, BuffEngine.MaxBuffsPerHero];
    public readonly Projectile[] Projectiles = new Projectile[SkillSystem.MaxProjectiles];

    // ---- PRD §7.7 Ice Walls (冰墙) — up to 8 concurrent walls ----------------------
    public const int MaxWalls = 8;
    public readonly Wall[] Walls = new Wall[MaxWalls];
    // Pre-created Box2D static bodies for walls; always present (off-map when inactive).
    private readonly MOBA.Logic.Physics.PhysicsBody[] _wallBodies = new MOBA.Logic.Physics.PhysicsBody[MaxWalls];
    private static readonly TSVector2 _offMap = new TSVector2((Fix64)(-999), (Fix64)(-999));

    // ---- M5.4 vision (per-team 200x200 bitmap) -------------------------------------
    public readonly VisionGrid VisionBlue = new();
    public readonly VisionGrid VisionRed  = new();

    // ---- M5.5 crystals + game-over --------------------------------------------------
    public readonly Crystal[] Crystals = new Crystal[CrystalSystem.Count];
    public bool GameOver;
    public Team Winner;

    private readonly Fix64 _dt;
    private readonly Fix64 _maxSpeed = (Fix64)5; // m/s
    private readonly Fix64 _scale100 = (Fix64)100;
    private readonly ArrayBufferWriter<byte> _hashBuf = new(2048);

    public DeterministicWorld(ulong seed)
    {
        // Register built-in skill / buff content (idempotent).
        BuiltinContent.Register();

        // The seed is unused at this stage (no RNG-driven entities yet) but is part
        // of the contract so we can prove inputs alone determine the hash.
        Physics = new PhysicsWorldManager(maxEntityId: 64, warmStarting: false, continuous: false);
        Fix64 boxHalf = (Fix64)25;
        Fix64 wallThk = (Fix64)1;
        ushort wCat = 0x0001, wMask = 0xFFFF;
        Physics.CreateBox(new EntityId(1), new TSVector2(Fix64.Zero,  boxHalf + wallThk), new TSVector2(boxHalf + wallThk, wallThk), BodyType.Static, wCat, wMask);
        Physics.CreateBox(new EntityId(2), new TSVector2(Fix64.Zero, -(boxHalf + wallThk)), new TSVector2(boxHalf + wallThk, wallThk), BodyType.Static, wCat, wMask);
        Physics.CreateBox(new EntityId(3), new TSVector2( boxHalf + wallThk, Fix64.Zero), new TSVector2(wallThk, boxHalf + wallThk), BodyType.Static, wCat, wMask);
        Physics.CreateBox(new EntityId(4), new TSVector2(-(boxHalf + wallThk), Fix64.Zero), new TSVector2(wallThk, boxHalf + wallThk), BodyType.Static, wCat, wMask);

        // Players spaced on a deterministic 5×2 grid.
        ushort pCat = 0x0002, pMask = 0xFFFF;
        Fix64 radius = (Fix64)0.5f;
        for (int i = 0; i < PlayerCount; i++)
        {
            int gx = i % 5 - 2;
            int gy = i / 5 == 0 ? -3 : 3;
            var pos = new TSVector2((Fix64)gx * (Fix64)2, (Fix64)gy);
            var id = new EntityId(10u + (uint)i);
            Players[i] = id;
            Physics.CreateCircle(id, pos, radius, BodyType.Dynamic, pCat, pMask);
        }
        _dt = Fix64.FromRaw((1L << 32) / TicksPerSecond);

        // M4: hero & tower setup (no physics body for towers — logical only).
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            h.HeroDefId = (byte)(i % BuiltinContent.HeroCount);
            BuiltinContent.ApplyBaseStats(ref h);
            h.Cdr = (Fix64)0;
            h.Level = 1;
            h.Alive = true;
            h.Target = UnitRef.None;
            h.Gold = Items.StartingGold;
        }
        for (int side = 0; side < 2; side++)
        {
            TSVector2[] table = side == 0 ? Lanes.BlueTowers : Lanes.RedTowers;
            int baseIdx = side * Lanes.TowersPerSide;
            for (int i = 0; i < Lanes.TowersPerSide; i++)
            {
                ref var t = ref Towers[baseIdx + i];
                t.Pos = table[i];
                t.MaxHp = (Fix64)2500;
                t.Hp = t.MaxHp;
                t.Ad = (Fix64)170;
                t.Armor = (Fix64)40;
                t.AttackRange = (Fix64)8;
                t.Team = (Team)side;
                t.Lane = (byte)(i / 2);
                t.Tier = (byte)(i % 2);
                t.Alive = true;
                t.BornFrame = 0;
                t.Target = UnitRef.None;
            }
        }
        // M5.5 crystals.
        CrystalSystem.Init(Crystals);
        // PRD §4.3/§7.1 jungle camps.
        JungleSystem.Init(JungleMonsters);

        // PRD §7.7 冰墙: pre-allocate static wall bodies placed off-map until activated.
        // EntityIds 50–57 reserved for walls (none of these are used by players/boundary).
        ushort wallCat = 0x0001, wallMask = 0xFFFF;
        for (int w = 0; w < MaxWalls; w++)
        {
            _wallBodies[w] = Physics.CreateBox(
                new EntityId(50u + (uint)w),
                _offMap,
                new TSVector2((Fix64)2, (Fix64)0.25f),
                BodyType.Static, wallCat, wallMask);
        }
    }

    /// <summary>
    /// Apply 10 InputFrames (slot 0..9) for the current frame, then advance physics by one tick,
    /// then run M4 game systems if <see cref="EnableGameplay"/>.
    /// </summary>
    public void Tick(ReadOnlySpan<InputFrame> inputs)
    {
        if (inputs.Length != PlayerCount) throw new ArgumentException("need 10 inputs");
        // M5.5: once a winner is declared, the sim freezes.
        if (GameOver) { Frame++; return; }
        for (int i = 0; i < PlayerCount; i++)
        {
            var body = Physics.TryGet(Players[i]);
            if (body == null) continue;
            // Joystick (-100..100) → -1..1 → * maxSpeed.
            var jx = (Fix64)inputs[i].JoyX / _scale100;
            var jy = (Fix64)inputs[i].JoyY / _scale100;
            body.LinearVelocity = new TSVector2(jx * _maxSpeed, jy * _maxSpeed);
        }
        Physics.Step(_dt);

        if (EnableGameplay)
        {
            for (int i = 0; i < PlayerCount; i++)
            {
                var body = Physics.TryGet(Players[i]);
                if (body != null) Heroes[i].Pos = body.Position;
            }
            if (Frame > 0 && (Frame % GameSystems.WaveIntervalFrames) == 0)
            {
                AliveMinionCount = GameSystems.SpawnWave(Minions, AliveMinionCount, Frame, WavesSpawned);
                WavesSpawned++;
            }
            int dmgCount = 0;
            Span<GameSystems.DamageEvent> q = _damageScratch;
            // Decode skill input bits into casts — done before AI/combat to give
            // the player a frame of priority on tied-frame ordering.
            DecodeSkillInputs(inputs, q, ref dmgCount);
            // Sync newly activated walls to their physics bodies (SetTransformFast to aim pos).
            // We detect "newly activated" by checking BornFrame == Frame (set in SpawnWall case).
            for (int w = 0; w < MaxWalls; w++)
            {
                ref var wall = ref Walls[w];
                if (wall.Alive && wall.BornFrame == Frame)
                    _wallBodies[w].Body.SetTransformFast(wall.Pos);
            }
            // Apply BuyItem requests embedded by the server in InputFrame.BuyItemId.
            for (int i = 0; i < PlayerCount; i++)
            {
                ushort bid = inputs[i].BuyItemId;
                if (bid == 0) continue;
                ItemSystem.TryBuy(Heroes, i, (ushort)(bid - 1));
            }
            ItemSystem.TickGold(Heroes, Frame);
            GameSystems.TickHeroes(Heroes, Minions, Towers, Frame, q, ref dmgCount, Buffs, JungleMonsters);
            GameSystems.TickMinions(Minions, Towers, Heroes, _dt, Frame, q, ref dmgCount);
            GameSystems.TickTowers(Towers, Minions, Heroes, Frame, q, ref dmgCount);
            BuffEngine.Tick(Heroes, Buffs, Frame, q, ref dmgCount);
            SkillSystem.TickProjectiles(Projectiles, Heroes, Minions, Towers, _dt, Frame, q, ref dmgCount, Buffs);
            JungleSystem.Tick(JungleMonsters, Heroes, Frame, q, ref dmgCount);
            GameSystems.ResolveDamage(q, dmgCount, Minions, Towers, Heroes, Frame, out AliveMinionCount, Buffs, JungleMonsters);
            // Expire walls and move their physics bodies off-map.
            for (int w = 0; w < MaxWalls; w++)
            {
                ref var wall = ref Walls[w];
                if (!wall.Alive) continue;
                if (Frame >= wall.ExpireFrame)
                {
                    wall.Alive = false;
                    _wallBodies[w].Body.SetTransformFast(_offMap);
                }
            }
            // M5.5: enemy minions in melee range damage the crystal; crystals retaliate.
            int dmgC2 = 0;
            CrystalSystem.Tick(Crystals, Heroes, Minions, Frame, q, ref dmgC2);
            // Minions adjacent to enemy crystal whittle it down.
            for (int mi = 0; mi < Minions.Length; mi++)
            {
                ref var m = ref Minions[mi];
                if (!m.Alive) continue;
                int targetTeam = m.Team == Team.Blue ? (int)Team.Red : (int)Team.Blue;
                ref var cr = ref Crystals[targetTeam];
                if (!cr.Alive) continue;
                Fix64 dx = cr.Pos.X - m.Pos.X, dy = cr.Pos.Y - m.Pos.Y;
                if (dx * dx + dy * dy > m.AttackRange * m.AttackRange) continue;
                if (Frame < m.AttackCdEndFrame) continue;
                CrystalSystem.ApplyDamage(Crystals, targetTeam, m.Ad);
                m.AttackCdEndFrame = Frame + 30;
            }
            // Resolve crystal's retaliation damage events.
            GameSystems.ResolveDamage(q, dmgC2, Minions, Towers, Heroes, Frame, out _);
            // Win check.
            if (!Crystals[(int)Team.Blue].Alive) { GameOver = true; Winner = Team.Red;  }
            else if (!Crystals[(int)Team.Red].Alive)  { GameOver = true; Winner = Team.Blue; }
            // M5.5: if Hero.Pos was rewritten by Respawn (differs from physics body), teleport body.
            for (int i = 0; i < PlayerCount; i++)
            {
                var body = Physics.TryGet(Players[i]);
                if (body == null) continue;
                ref var h = ref Heroes[i];
                if (!h.Alive) continue;
                Fix64 dx = h.Pos.X - body.Position.X, dy = h.Pos.Y - body.Position.Y;
                Fix64 d2 = dx * dx + dy * dy;
                if (d2 > (Fix64)1) // > 1m^2 means a teleport happened
                {
                    body.Teleport(h.Pos);
                }
            }
            // Vision recompute happens after positions / death state are settled.
            VisionSystem.Recompute(VisionBlue, Team.Blue, Heroes, Minions, Towers);
            VisionSystem.Recompute(VisionRed,  Team.Red,  Heroes, Minions, Towers);
        }
        Frame++;
    }

    /// <summary>xxHash64 of the post-step physics snapshot (+ gameplay state if enabled).</summary>
    public ulong Hash()
    {
        _hashBuf.Clear();
        Physics.WriteSnapshot(_hashBuf);
        if (EnableGameplay) WriteGameplayState(_hashBuf);
        return XxHash64Helper.Hash(_hashBuf.WrittenSpan);
    }

    /// <summary>Writes the full physics snapshot for resync (M3 reconnect).</summary>
    public void WriteSnapshot(ArrayBufferWriter<byte> w)
    {
        Physics.WriteSnapshot(w);
        if (EnableGameplay) WriteGameplayState(w);
    }

    public void ReadSnapshot(ReadOnlySpan<byte> r, uint frame)
    {
        Physics.ReadSnapshot(r);
        if (EnableGameplay)
        {
            int physBytes = Physics.ActiveCount * PhysicsWorldManager.SnapshotBytesPerBody;
            ReadGameplayState(r.Slice(physBytes));
        }
        Frame = frame;
    }

    /// <summary>Decodes <see cref="InputFrame.SkillBits"/> bits 0..3 into TryCast calls
    /// using <see cref="BuiltinContent.HeroSkills"/>. Aim direction comes from
    /// <see cref="InputFrame.AimAngleDeg"/> projected to CastRange.</summary>
    [NoGC]
    private void DecodeSkillInputs(ReadOnlySpan<InputFrame> inputs, Span<GameSystems.DamageEvent> q, ref int dmgCount)
    {
        for (int i = 0; i < PlayerCount; i++)
        {
            byte bits = inputs[i].SkillBits;
            if ((bits & 0x0F) == 0) continue;
            ref var h = ref Heroes[i];
            if (!h.Alive) continue;
            var (cosA, sinA) = TrigLut.Dir(inputs[i].AimAngleDeg);
            for (byte slot = 0; slot < 4; slot++)
            {
                if ((bits & (1 << slot)) == 0) continue;
                ushort defId = BuiltinContent.HeroSkills[h.HeroDefId, slot];
                ref var def = ref SkillEngine.Defs[defId];
                // Aim point at CastRange in cosA/sinA direction.
                Fix64 reach = def.CastRange > Fix64.Zero ? def.CastRange : (Fix64)1;
                TSVector2 aim = new TSVector2(h.Pos.X + cosA * reach, h.Pos.Y + sinA * reach);
                SkillSystem.TryCast(Heroes, Buffs, i, slot, defId, aim, Projectiles, Frame, q, ref dmgCount, Walls);
            }
        }
    }

    private void WriteGameplayState(IBufferWriter<byte> w)
    {
        const int per = 8 + 8 + 8 + 1; // hp, posX, posY, alive
        int total = (PlayerCount + GameSystems.TowerCount + GameSystems.MaxMinions) * per;
        var span = w.GetSpan(total);
        int off = 0;
        for (int i = 0; i < PlayerCount; i++)            off = WriteUnit(span, off, Heroes[i].Hp,  Heroes[i].Pos,  Heroes[i].Alive);
        for (int i = 0; i < GameSystems.TowerCount; i++) off = WriteUnit(span, off, Towers[i].Hp,  Towers[i].Pos,  Towers[i].Alive);
        for (int i = 0; i < GameSystems.MaxMinions; i++) off = WriteUnit(span, off, Minions[i].Hp, Minions[i].Pos, Minions[i].Alive);
        w.Advance(total);

        // M5: per-hero mana + tag mask + skill cd + buff slot HPs; projectile state.
        const int heroExtra = 8 /*Mp*/ + 8 /*Tags*/ + 4 * 4 /*SkillCd0..3*/;
        const int buffPer = 2 /*DefId*/ + 1 /*Stack*/ + 4 /*EndFrame*/;
        const int projPer = 8 + 8 + 8 + 8 + 1; // posX, posY, vx, vy, alive
        int extra = PlayerCount * heroExtra
                  + PlayerCount * BuffEngine.MaxBuffsPerHero * buffPer
                  + Projectiles.Length * projPer;
        var s2 = w.GetSpan(extra);
        int o = 0;
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            BinaryPrimitives.WriteInt64LittleEndian(s2.Slice(o, 8), h.Mp.RawValue); o += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(s2.Slice(o, 8), h.Tags); o += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(s2.Slice(o, 4), h.SkillCd0); o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(s2.Slice(o, 4), h.SkillCd1); o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(s2.Slice(o, 4), h.SkillCd2); o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(s2.Slice(o, 4), h.SkillCd3); o += 4;
        }
        for (int i = 0; i < PlayerCount; i++)
            for (int b = 0; b < BuffEngine.MaxBuffsPerHero; b++)
            {
                ref var bi = ref Buffs[i, b];
                BinaryPrimitives.WriteUInt16LittleEndian(s2.Slice(o, 2), bi.DefId); o += 2;
                s2[o++] = bi.Stack;
                BinaryPrimitives.WriteUInt32LittleEndian(s2.Slice(o, 4), bi.EndFrame); o += 4;
            }
        for (int i = 0; i < Projectiles.Length; i++)
        {
            ref var p = ref Projectiles[i];
            BinaryPrimitives.WriteInt64LittleEndian(s2.Slice(o, 8), p.Pos.X.RawValue); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(s2.Slice(o, 8), p.Pos.Y.RawValue); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(s2.Slice(o, 8), p.Velocity.X.RawValue); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(s2.Slice(o, 8), p.Velocity.Y.RawValue); o += 8;
            s2[o++] = (byte)(p.Alive ? 1 : 0);
        }
        w.Advance(extra);

        // M5.3: gold + 6-slot inventory per hero.
        const int invPer = 4 /*Gold*/ + 6 /*Inv0..5*/;
        int invTotal = PlayerCount * invPer;
        var s3 = w.GetSpan(invTotal);
        int oo = 0;
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            BinaryPrimitives.WriteUInt32LittleEndian(s3.Slice(oo, 4), h.Gold); oo += 4;
            s3[oo++] = h.Inv0; s3[oo++] = h.Inv1; s3[oo++] = h.Inv2;
            s3[oo++] = h.Inv3; s3[oo++] = h.Inv4; s3[oo++] = h.Inv5;
        }
        w.Advance(invTotal);

        // M5.4: per-team vision masks (5000 bytes each).
        var vb = w.GetSpan(Vision.MaskBytes);
        VisionBlue.Mask.CopyTo(vb);
        w.Advance(Vision.MaskBytes);
        var vr = w.GetSpan(Vision.MaskBytes);
        VisionRed.Mask.CopyTo(vr);
        w.Advance(Vision.MaskBytes);

        // M5.5: crystals (Hp + Alive per side) + GameOver flag.
        const int crystalBytes = CrystalSystem.Count * (8 + 1) + 2; // hp,alive each + (gameOver,winner)
        var sc = w.GetSpan(crystalBytes);
        int co = 0;
        for (int i = 0; i < CrystalSystem.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(sc.Slice(co, 8), Crystals[i].Hp.RawValue); co += 8;
            sc[co++] = (byte)(Crystals[i].Alive ? 1 : 0);
        }
        sc[co++] = (byte)(GameOver ? 1 : 0);
        sc[co++] = (byte)Winner;
        w.Advance(crystalBytes);

        // PRD §7.7 walls: Pos(8+8) + HalfLen(8) + ExpireFrame(4) + BornFrame(4) + Alive(1) = 33 bytes each.
        const int wallPer = 8 + 8 + 8 + 4 + 4 + 1;
        var sw = w.GetSpan(MaxWalls * wallPer);
        int wo = 0;
        for (int i = 0; i < MaxWalls; i++)
        {
            ref var wall = ref Walls[i];
            BinaryPrimitives.WriteInt64LittleEndian(sw.Slice(wo, 8), wall.Pos.X.RawValue); wo += 8;
            BinaryPrimitives.WriteInt64LittleEndian(sw.Slice(wo, 8), wall.Pos.Y.RawValue); wo += 8;
            BinaryPrimitives.WriteInt64LittleEndian(sw.Slice(wo, 8), wall.HalfLen.RawValue); wo += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(sw.Slice(wo, 4), wall.ExpireFrame); wo += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(sw.Slice(wo, 4), wall.BornFrame); wo += 4;
            sw[wo++] = (byte)(wall.Alive ? 1 : 0);
        }
        w.Advance(MaxWalls * wallPer);
    }

    /// <summary>Inverse of <see cref="WriteGameplayState"/>. Restores Hero/Tower/Minion HP+Pos+Alive,
    /// per-hero Mp/Tags/SkillCd, buffs, projectiles, gold+inventory, vision masks, crystals + GameOver.</summary>
    private void ReadGameplayState(ReadOnlySpan<byte> r)
    {
        int o = 0;
        // 1) Units (Heroes, Towers, Minions): hp[8] posX[8] posY[8] alive[1]
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            o = ReadUnit(r, o, out var hp, out var pos, out var alive);
            h.Hp = hp; h.Pos = pos; h.Alive = alive;
        }
        for (int i = 0; i < GameSystems.TowerCount; i++)
        {
            ref var t = ref Towers[i];
            o = ReadUnit(r, o, out var hp, out var pos, out var alive);
            t.Hp = hp; t.Pos = pos; t.Alive = alive;
        }
        for (int i = 0; i < GameSystems.MaxMinions; i++)
        {
            ref var m = ref Minions[i];
            o = ReadUnit(r, o, out var hp, out var pos, out var alive);
            m.Hp = hp; m.Pos = pos; m.Alive = alive;
        }
        // 2) Per-hero extras: Mp[8] Tags[8] SkillCd0..3 [4]
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            h.Mp = Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8))); o += 8;
            h.Tags = BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(o, 8)); o += 8;
            h.SkillCd0 = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            h.SkillCd1 = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            h.SkillCd2 = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            h.SkillCd3 = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
        }
        // 3) Buffs
        for (int i = 0; i < PlayerCount; i++)
            for (int b = 0; b < BuffEngine.MaxBuffsPerHero; b++)
            {
                ref var bi = ref Buffs[i, b];
                bi.DefId = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(o, 2)); o += 2;
                bi.Stack = r[o++];
                bi.EndFrame = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            }
        // 4) Projectiles
        for (int i = 0; i < Projectiles.Length; i++)
        {
            ref var p = ref Projectiles[i];
            p.Pos = new TSVector2(
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8))),
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o + 8, 8))));
            p.Velocity = new TSVector2(
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o + 16, 8))),
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o + 24, 8))));
            p.Alive = r[o + 32] != 0;
            o += 33;
        }
        // 5) Gold + inventory
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var h = ref Heroes[i];
            h.Gold = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            h.Inv0 = r[o++]; h.Inv1 = r[o++]; h.Inv2 = r[o++];
            h.Inv3 = r[o++]; h.Inv4 = r[o++]; h.Inv5 = r[o++];
        }
        // 6) Vision masks
        r.Slice(o, Vision.MaskBytes).CopyTo(VisionBlue.Mask); o += Vision.MaskBytes;
        r.Slice(o, Vision.MaskBytes).CopyTo(VisionRed.Mask);  o += Vision.MaskBytes;
        // 7) Crystals + GameOver/Winner
        for (int i = 0; i < CrystalSystem.Count; i++)
        {
            Crystals[i].Hp = Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8))); o += 8;
            Crystals[i].Alive = r[o++] != 0;
        }
        GameOver = r[o++] != 0;
        Winner = (Team)r[o++];
        // 8) Walls
        const int wallPer = 8 + 8 + 8 + 4 + 4 + 1;
        for (int i = 0; i < MaxWalls; i++)
        {
            ref var wall = ref Walls[i];
            wall.Pos = new TSVector2(
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8))),
                Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o + 8, 8))));
            o += 16;
            wall.HalfLen = Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8))); o += 8;
            wall.ExpireFrame = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            wall.BornFrame = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)); o += 4;
            wall.Alive = r[o++] != 0;
            // Restore physics body position.
            if (wall.Alive)
                _wallBodies[i].Body.SetTransformFast(wall.Pos);
            else
                _wallBodies[i].Body.SetTransformFast(_offMap);
        }
    }

    private static int ReadUnit(ReadOnlySpan<byte> span, int off, out Fix64 hp, out TSVector2 pos, out bool alive)
    {
        hp = Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(off + 0, 8)));
        pos = new TSVector2(
            Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(off + 8, 8))),
            Fix64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(off + 16, 8))));
        alive = span[off + 24] != 0;
        return off + 25;
    }

    private static int WriteUnit(Span<byte> span, int off, Fix64 hp, TSVector2 pos, bool alive)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 0,  8), hp.RawValue);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 8,  8), pos.X.RawValue);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(off + 16, 8), pos.Y.RawValue);
        span[off + 24] = (byte)(alive ? 1 : 0);
        return off + 25;
    }
}
