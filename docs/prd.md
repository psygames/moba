# 5v5 MOBA 帧同步项目开发需求文档（AI 专用 v3.1）

> **版本说明**：v3.1 在 v3.0 基础上补齐 M6/M7/M8 已实现规格（野怪、连杀奖励、攻速公式、CDR 上限、被动技能 ConfigBinary 序列化），修订若干与代码不符的数值，并更新 Milestone 完成状态。本文是 AI 实现的**唯一权威依据**，凡未在本文中规定的行为，AI 必须先提问、不得自行发挥。

---

## 0. 项目骨架与程序集划分

### 0.1 仓库目录
```
moba/                          # Unity 客户端工程（Unity 2022.3 LTS, URP）
  Assets/
    Scripts/
      Logic/                   # → asmdef: MOBA.Logic   （无 UnityEngine 依赖）
      View/                    # → asmdef: MOBA.View    （依赖 MOBA.Logic + UnityEngine）
      Net/                     # → asmdef: MOBA.Net     （客户端网络，依赖 MOBA.Logic）
      Bootstrap/               # → asmdef: MOBA.Bootstrap（场景/启动）
    ThirdParty/
      Box2DSharp/              # 源码内嵌，定点数版（见 2.1）
      FixedMath.Net/
      kcp2k/
      MemoryPack/              # 通过 UPM
server/                        # .NET 8 服务端解决方案
  MOBA.Server/                 # 进程入口（Program.cs）
  MOBA.Server.Room/            # 房间 / 帧广播
  MOBA.Server.Network/         # KCP 接入
  MOBA.Shared/                 # 与客户端 MOBA.Logic / MOBA.Net 共享的源码（symlink 或 SharedProject）
config/                        # Luban 配置表源（Excel/Json）和烘焙产物
docs/
  prd.md                       # 本文档
```

### 0.2 程序集隔离硬规则
| 程序集 | 允许使用 | 严格禁止 |
|---|---|---|
| `MOBA.Logic` / `MOBA.Shared` | `FixedMath.Net`、`Box2DSharp`(定点数版)、`MemoryPack`、`System.*`（不含 `System.Random`） | `UnityEngine.*`、`float`、`double`、`Vector3`、`Mathf`、`Time.*`、`System.Random`、`DateTime.Now`、热路径中的 LINQ |
| `MOBA.View` | 上述全部 + `UnityEngine.*` | 直接修改 `MOBA.Logic` 中的状态（只能通过事件订阅） |
| `MOBA.Net` | `MOBA.Logic` + `kcp2k` | 同上 |
| `MOBA.Server` | `MOBA.Logic` + `kcp2k` + `Microsoft.Extensions.*` | `UnityEngine.*` |

**强制 CI 检查**：用 Roslyn Analyzer（自研一个 `MobaDeterminismAnalyzer`）扫 `MOBA.Logic` 程序集，发现禁用 API 即构建失败。

### 0.3 命名空间
- `MOBA.Logic.Math`（定点数包装）、`MOBA.Logic.Physics`、`MOBA.Logic.Pathfinding`、`MOBA.Logic.Entity`、`MOBA.Logic.Skill`、`MOBA.Logic.World`
- `MOBA.Net.Protocol`、`MOBA.Net.Client`
- `MOBA.View.Render`、`MOBA.View.Input`、`MOBA.View.UI`
- `MOBA.Server.Room`、`MOBA.Server.Network`

---

## 1. 项目背景与 AI 角色设定

- **AI 角色**：资深 Unity 客户端主程 + C# 服务端架构师，精通帧同步（Lockstep）、定点数、纯 C# 物理。
- **项目目标**：Unity + .NET 8 实现 5v5 联机 MOBA 核心战斗 Demo（三路兵线、野区、视野、装备、复活、水晶判胜负）。
- **核心约束**：绝对的逻辑/表现分离；服务端权威帧同步；全程确定性；5v5 规模下逻辑帧零 GC。

---

## 2. 技术栈选型表（钉死版本）

### 2.1 第三方库（全部纯 C#、跨平台）
| 用途 | 库 | 版本 / 提交 | License | 引入方式 | 备注 |
|---|---|---|---|---|---|
| 定点数 | [FixedMath.Net](https://github.com/asik/FixedMath.Net) | master @ 提交锁定（首次 clone 后写入 `THIRDPARTY-COMMITS.md`） | Apache-2.0 | 源码内嵌 | Q31.32，含 sin/cos/sqrt/atan2 查表 |
| 物理 | [Zonciu/Box2DSharp-deterministic](https://github.com/Zonciu/Box2DSharp-deterministic) | master @ d7d4c74 | MIT | 源码内嵌 | 自补 World 快照 + GC-free 包装 |
| PRNG | 自实现 `XorShift128Plus` | — | 本项目 | `MOBA.Logic.Math.RNG` | 64-bit state×2，种子由服务端开局下发 |
| 网络（KCP） | [kcp2k](https://github.com/MirrorNetworking/kcp2k) | v1.42+ | MIT | UPM + NuGet | 客户端走 UPM，服务端走 NuGet |
| 序列化 | [MemoryPack](https://github.com/Cysharp/MemoryPack) | v1.21+ | MIT | UPM + NuGet | 用于快照、协议、配置烘焙 |
| 配置表 | [Luban](https://github.com/focus-creative-games/luban) | v3.x | MIT | 外部 CLI | Excel→MemoryPack 二进制 |
| 寻路 | 自研 A* on Grid | — | 本项目 | `MOBA.Logic.Pathfinding` | 一期不做 JPS |
| 单元测试 | NUnit + BenchmarkDotNet | 最新稳定 | MIT | NuGet | CI 中跑零 GC 压测 |

### 2.2 引擎与运行时
- Unity **2022.3 LTS**，URP，IL2CPP（发布）；Mono（开发态）
- 服务端 **.NET 8**，AOT 不强制，先 JIT
- 平台：一期 Windows + Android（客户端），Linux x64（服务端）

### 2.3 一律禁止
- ECS / DOTS / Burst（Burst 不保证跨平台 IL 一致性）
- 任何 Native 物理库（Unity Physics / PhysX / Bullet / Box2D 原生 C++ 移植）
- `System.Random`、`Mathf.Sin`、`float`/`double` 进入逻辑层
- 战斗循环中的 `new` 和 `Instantiate`

---

## 3. 5v5 核心性能与底层支撑模块

### 3.1 物理封装：`PhysicsWorldManager`
**职责**：在 Box2DSharp-deterministic 之上做一层 GC-free + 快照友好的封装。

**接口契约**（位于 `MOBA.Logic.Physics`）：
```csharp
public interface IPhysicsWorld {
    PhysicsBody CreateCircle(EntityId id, TSVector2 pos, Fix64 radius, BodyType type, ushort categoryBits, ushort maskBits);
    PhysicsBody CreateBox  (EntityId id, TSVector2 pos, TSVector2 half, BodyType type, ushort categoryBits, ushort maskBits);
    void DestroyBody(PhysicsBody body);                // 归还到对象池
    void Step(Fix64 dt);                               // 固定 dt = 1/15
    int  RangeQuery(TSVector2 c, Fix64 r, EntityId[] outBuf); // 返回填入数量，禁分配
    bool Raycast   (TSVector2 from, TSVector2 to, ushort mask, out RaycastHit hit);
    void WriteSnapshot(IBufferWriter<byte> w);         // 见 3.3
    void ReadSnapshot (ReadOnlySpan<byte> r);
}
```

**关键约束**
- **碰撞过滤**：用 `categoryBits`/`maskBits`，禁用 `IBeginContactListener` 的 lambda；统一通过帧末扫 `ContactList` 处理。
- **GC-free**：所有 `RangeQuery` / `Raycast` 调用方传入复用缓冲；包装层在初始化时预分配 `MaxBody=512` 个 `PhysicsBody` 槽位。
- **仅 2D**：MOBA 走 XY 平面，不需要 3D。Z 仅用于表现层（视觉高度）。

### 3.2 寻路：`GridPathfinder`
- 网格分辨率 **0.5 米**，地图 100×100 米 → 200×200 = 40000 格。
- 算法：A*，开放表用二叉堆 + 数组复用，闭表用 `byte[40000]` bitmap，每次寻路 `Reset()` 而不重 new。
- 对外接口：`int FindPath(TSVector2 from, TSVector2 to, TSVector2[] outWaypoints)`，返回路点数；超长路径 `> 64` 截断。
- 与物理结合：寻路只输出路径点；移动由 `MovementSystem` 在每逻辑帧把 `body.LinearVelocity` 设为 `(next - cur).Normalized * speed`，到达半径 `< 0.1m` 切下一路点。
- 性能预算：单次 200×200 寻路 < 2ms（M2 DoD）。

### 3.3 逻辑快照与断线重连
**`WorldSnapshot` 字段（MemoryPack 序列化）**
```
- ulong frame
- ulong rngState0, rngState1
- EntityState[] entities      // id, type, pos, vel, rot, hp, mp, level, exp, gold, buffs[], cooldowns[]
- BodyState[]   bodies        // id, pos, vel, angularVel, awake (Box2D Body 关键状态，contact 不存)
- TowerState[]  towers
- TeamState[2]  teams         // gold, score, fog 可见集 bitmap
- SkillCDState[] skills
```

**机制**
- 服务端**每 60 逻辑帧（4 秒）做一次全量快照**，常驻最近 8 份；其余靠输入帧增量。
- 客户端断线 ≤ 5s：服务端补发缺失输入帧。
- 断线 > 5s 或主动重连：服务端下发 (最近一次全量快照 + 之后的输入帧)；客户端 `ReadSnapshot` → 反序列化 → 让物理引擎 `Step(0)` 一次以重建 contact island → 然后追帧到当前。
- **追帧期间**：`ViewBus.Mute = true`，所有动画/特效/音效事件丢弃。

**确定性自检**：每帧物理 `Step` 完成后计算 `state hash = xxHash64(WorldSnapshot 序列化字节)`，每 30 帧上报服务端，服务端比对 10 客户端 hash 全相同即确定性达标，否则记为 desync 并 dump 快照。

---

## 4. 核心逻辑实体与系统

### 4.1 对象池
- 必须有池：`MinionLogic`、`HeroLogic`、`ProjectileLogic`、`BuffInstance`、`PhysicsBody`（包装 Box2D Body）、`PathRequest`、`SkillEvent`，以及 View 层 `GameObject`。
- 池实现：`Pool<T> where T : class, IPoolable, new()`；`new()` 仅在 `Pool.Prewarm()` 时发生。
- 战斗主循环 `Update` 调用栈中**禁止任何 `new`**（CI 用 Roslyn Analyzer 扫 `MOBA.Logic` 中带 `[NoGC]` 属性的方法，发现 `new` 报错；`StringBuilder` 等也禁用，错误信息走预定义 `string` 常量）。

### 4.2 战斗与技能系统

#### 4.2.1 标签系统（Gameplay Tags）
- `ulong` 位掩码，预定义：`IsStunned=1<<0, IsSilenced=1<<1, IsRooted=1<<2, IsInvincible=1<<3, IsInvisible=1<<4 ...`（最多 64 位）。
- 接口：`bool HasTag(ulong tag)`、`void AddTag(ulong)`、`void RemoveTag(ulong)`。

#### 4.2.2 Buff 管理器
- `BuffInstance { ushort defId; Fix64 remainSec; byte stack; ushort sourceEntityId; }`，每英雄最多 16 槽。
- Buff 配置（Luban 表 `buff.xlsx`）字段：id、最大叠加、刷新策略（独立 / 刷新 / 叠层）、属性修改器列表、Tick 周期、Tick 效果（伤害/治疗/驱散）、过期效果。

#### 4.2.3 技能数据驱动（统一模型）
**禁止用脚本/DSL**。每个技能 = `SkillDef` + 有序 `EffectStep[]`：
```
SkillDef { id, ownerType, cdMs, manaCost, castRangeM, castType(Instant|Direction|Position|Target),
           preCastMs, animKey, indicatorKey,
           hitShape(None|Circle{r}|Sector{r,deg}|Line{w,len}),
           steps: EffectStep[] }
EffectStep { delayMs, kind(Damage|Heal|ApplyBuff|Pull|Knockback|SpawnProjectile|SpawnUnit|Teleport),
             target(Self|Caster|HitTargets|Position),
             payload(根据 kind 解释，定点数参数) }
```
任何"超出此模型"的技能必须先扩展 `EffectStep.kind` 枚举与对应执行器，**不得**为单个技能写 if/else 分支。

#### 4.2.4 投射物
- `ProjectileLogic` 是物理 body（小圆，category=Projectile），自带 `lifeSec`、`speed`、`hitMask`、`onHit: SkillEffectStepRef`。
- 命中即触发 `EffectStep`，自身回池。

### 4.3 兵线与防御塔
- **三路发兵**：`SpawnerLogic` 在帧 `30s, 60s, 90s …`（用逻辑帧号判断，非真实时间）于 6 个出生点各刷 1 波 = 3 近战 + 3 远程；30 波后追加 1 攻城兵。
- **小兵 AI**：状态机（Move→Engage→Attack→Move），目标优先级：被攻击者 > 最近敌方小兵 > 最近敌方英雄 > 最近敌方防御塔。
- **防御塔**：固定位置 body，5 米仇恨半径，目标优先级：攻击友方英雄的敌方英雄 > 最近敌方小兵 > 最近敌方英雄。
- **野怪**：4 处野区刷新点，初始帧 60s 出现，被击杀后 90s 重刷。

---

## 5. 网络帧同步主循环

### 5.1 KCP 参数（客户端 / 服务端必须一致）
```
nodelay=1, interval=10, resend=2, nc=1, mtu=1200, sndwnd=128, rcvwnd=128
```

### 5.2 协议（MemoryPack 二进制）
所有协议消息以 1 字节 `MsgId` 开头：
| MsgId | 名称 | 方向 | 备注 |
|---|---|---|---|
| 0x01 | `C2S_JoinRoom` | C→S | `roomId, playerId, token` |
| 0x02 | `S2C_RoomStart` | S→C | `seed, mapId, players[10], serverFrameRate=15` |
| 0x10 | `C2S_Input` | C→S | `frame, joystick(sbyte x,y), skillBits, targetEntityId` |
| 0x11 | `S2C_FrameBatch` | S→C | `startFrame, InputFrame[]`（每帧 10 玩家全量，缺则填空指令）|
| 0x20 | `C2S_HashReport` | C→S | `frame, xxHash64` |
| 0x30 | `C2S_RequestResync` | C→S | 主动重连 |
| 0x31 | `S2C_Snapshot` | S→C | `WorldSnapshot + 后续 InputFrame[]` |
| 0x40 | `C2S_BuyItem` | C→S | `itemId`，服务端校验后纳入帧广播作为公共指令 |
| 0xF0 | `Ping` / `Pong` | 双向 | 心跳 1s |

`InputFrame`（10 字节固定）：
```
sbyte joyX; sbyte joyY;        // -100..100 表示摇杆
byte  skillBits;               // bit0..3 = 技能 1..4 释放，bit4 = 普攻，bit5 = 取消
byte  targetSlot;              // 0=无目标，1..32 为锁定实体槽号
ushort aimAngleDeg;            // 0..359 方向技能瞄准
byte  flags;                   // bit0=回城 bit1=升级技能1 ...
byte  pad;
```

### 5.3 服务端循环
- 15Hz Tick（Tick = 66.66ms）。
- 每 Tick：① 收齐 10 玩家当前帧输入（缺者补空指令并标记 Lag）；② 广播 `S2C_FrameBatch`；③ **服务端不跑物理**（一期），只做帧广播 + 快照存档（每 60 帧）+ hash 仲裁。
- 一名玩家连续 5s 无输入 → 标记 AFK 但不踢，继续填空指令。

### 5.4 客户端循环
- 逻辑帧 15 FPS：收到 `S2C_FrameBatch` → 入队 → 按帧号顺序 `World.Step(input)`。
- 表现帧 60+ FPS：从最近两个逻辑帧状态做线性插值；旋转用 nlerp。
- 追帧策略：队列累计 > 3 帧时，每渲染帧推进 2 个逻辑帧并 `ViewBus.Mute=true`。
- **不做客户端预测**（一期）。本地输入与服务端确认存在 1～2 帧延迟，可接受。

### 5.5 服务端容灾
- 单房间崩溃不影响其它房间（Room 各自独立任务 + try/catch + dump）。
- `dump/` 目录写入 desync 房间最近 8 份快照 + 全部输入帧，便于线下 replay。

---

## 6. 服务端规格

### 6.1 进程模型
- 单进程多房间，每房间 1 个 `Channel<InputEnvelope>` + 1 个后台 `Task`（不绑线程，靠 .NET ThreadPool）。
- 房间生命周期：`Created → Waiting(10 玩家集齐, 30s 超时解散) → Loading(全员上报 Ready, 30s 超时) → Battle → Ended(15 分钟硬上限) → Disposed`。

### 6.2 接入与匹配（一期 mock）
- 无登录服 / 匹配服。客户端用 `--room=<id> --player=<idx>` 命令行参数直连。
- 后续接入预留 `IRoomAllocator` 接口。

### 6.3 配置
`appsettings.json`：监听端口、最大房间数、KCP 参数、日志级别。

### 6.4 日志与可观测性
- `Microsoft.Extensions.Logging` + `Serilog`，按房间分文件。
- 指标：每秒处理帧数、平均 RTT、desync 次数、AFK 玩家数；走 Prometheus exporter（一期可选）。

---

## 7. 玩法规格

### 7.1 地图
- 尺寸：100×100 逻辑米（XY 平面），坐标原点在地图中心。
- 三路：上 / 中 / 下，路径点表 `map_path.xlsx`，每路 8 个 waypoint。
- 防御塔：每路 2 座 + 基地水晶 1 座，共 (2×3+1)×2 = 14 座。
- 野区：4 个三角野区，各刷 1 处野怪营地（小型怪 ×2）。
- 出生点：每队 5 个，位于基地周围。
- 寻路网格 0.5m，可走/不可走由 `map_grid.png`（200×200，黑=阻挡）烘焙。

### 7.2 属性公式（全部定点数）
| 属性 | 简称 | 说明 |
|---|---|---|
| 生命值 | HP | 满血上限 |
| 法力值 | MP | |
| 攻击力 | AD | 物理攻击 |
| 法术强度 | AP | 法术加成 |
| 物理护甲 | ARM | 减伤公式 `final = raw * 100 / (100 + ARM)` |
| 法术抗性 | MR | 同上 |
| 攻击速度 | AS | 每秒攻击次数（Fix64），攻击间隔（帧）= `floor(15 / AS)` |
| 移动速度 | MS | 米/秒 |
| 冷却缩减 | CDR | 0..40（**上限 40**），`cd_frames = base_frames × (100−CDR) / 100` |
| 暴击率/暴击伤害 | CR/CD | 一期不做 |

属性来源 = 基础（按等级表插值） + 装备 + Buff（加法 → 乘法 → 最终钳制）。

### 7.3 经济与等级
- 击杀小兵：近战 +20 金 +60 经验，远程 +18 金 +50 经验，攻城 +40 金 +90 经验。
- 击杀野怪：+60 金 +80 经验。
- 击杀英雄：基础 +300 金 + 连杀奖励（见下表）；死亡重置连杀计数。
- 击杀防御塔：全队各 +150 金。
- 等级 1～18，升级经验表 `level.xlsx`（起始 0 经验，2 级需 280 经验，每级递增约 100，18 级需 2000 累计）。
- 每 ~0.5s（每 8 逻辑帧）全员被动 +1 金（约 2 金/秒）。

**连杀奖励表**（累加在基础 300 金之上）：

| 连杀数 | 额外奖励 |
|---|---|
| 1 | +0 |
| 2 | +50 |
| 3 | +100 |
| 4 | +150 |
| 5 | +200 |
| 6 | +250 |
| ≥7 | +300（上限）|

### 7.4 装备与商店
- 一期 10 件装备（`item.xlsx`）：3 件起始、3 件大件武器、2 件法术、2 件防御。
- 商店：仅在己方泉水范围内可购买；通过 `0x40 C2S_BuyItem` 协议，服务端校验后纳入帧广播作为公共指令。

### 7.5 视野与战争迷雾
- 视野计算放**服务端**（防作弊）。
- 算法：每英雄 / 小兵 / 塔有视野半径 `sightR`，视野 = 自己半径内格子全可见；遮挡（草丛、墙）一期不做（地图无遮挡物，野区也直接可见）。
- 服务端对每队维护 `Visible: bitmap[200×200]`，在帧广播时把"对方实体是否在己方视野内"写入 `S2C_FrameBatch` 的可见集差量；不可见实体客户端冻结上一次表现。

### 7.6 复活与胜负
- 死亡复活时间：`baseSec(英雄等级×2 + 5) + gameMinute×2`，上限 60s。
- 复活在己方泉水。
- 胜负：任一基地水晶 HP=0 即对方获胜，写 `S2C_GameOver { winnerTeam }`，房间转 Ended。

### 7.7 三个示例英雄（v3.0 必须落地）
所有数值均为 1 级值，等级成长详见 `hero_level.xlsx`。

#### 英雄 A：剑士（战士，近战 AD）
- 属性：HP 600(+80/lv) MP 200 AD 60(+5/lv) ARM 25 MR 25 AS 0.7（无成长）MS 6.5
- 普攻：近战 1.5m，1.0×AD 物理伤害；攻击间隔 = `floor(15/0.7)` = 21 帧
- 技能 Q 「冲锋」：方向，0.3s 前摇（5帧），3m 直线突进，碰到敌人造成 80(+0.6AD) 物伤 + 击退 3m，CD=6s
- 技能 W 「旋风斩」：自身周围 2m 圆形 AOE，造成 60(+0.5AD) 物伤，CD=8s
- 技能 E 「破甲」：**被动**，普攻附带护甲削减 buff（每层 −5 ARM，最多 5 层，每层持续 4s/60帧）
- 技能 R 「斩首」：目标点 4m 范围，对 **HP < 30%** 的最低血量敌英雄造成 200(+1.0AD) 真实伤害；HP ≥ 30% 时技能无法施放（CD 不消耗）

#### 英雄 B：法师（中程 AP）
- 属性：HP 480(+60/lv) MP 400 AP 50(+8/lv) AD 45 ARM 18 MR 25 AS 0.6（无成长）MS 6.0
- 普攻：远程 5m，30(+0.3AP) 魔法伤害；攻击间隔 = `floor(15/0.6)` = 25 帧
- 技能 Q 「火球」：方向，0.4s 前摇（6帧），飞行 8m 速度 12m/s，命中首个目标爆炸 2m AOE 80(+0.7AP) 魔法伤害，CD=0.8s
- 技能 W 「冰墙」：目标点放置 4m 长不可通行墙体（物理 body），持续 3s，CD=18s
- 技能 E 「闪现」：方向，瞬移 3m，CD=90s
- 技能 R 「陨石」：目标点 3m AOE，0.8s（12帧）后落下 250(+1.2AP) 魔法伤害 + 眩晕 1s，CD=40s

#### 英雄 C：射手（远程 AD）
- 属性：HP 520(+70/lv) MP 250 AD 55(+4/lv) ARM 20 MR 25 AS 0.85(**+0.03/lv**) MS 6.2
- 普攻：远程 6m，1.0×AD 物理伤害；攻击间隔 = `floor(15/0.85)` = 17 帧
- 技能 Q 「穿透箭」：方向，直线 8m **穿透**所有敌人，60(+0.7AD) 物伤，CD=0.6s
- 技能 W 「位移射击」：方向，后撤 2m 同时向瞄准方向射出 6m 投射物 80(+0.8AD) 物伤，CD=9s
- 技能 E 「狩猎之眼」：**被动**，普攻目标 HP < 50% 时额外 ×1.2 伤害
- 技能 R 「狙击」：目标方向 12m 直线，1s 蓄力（15帧前摇），造成 300(+1.5AD) 物伤，CD=40s

> 上述 12 个技能全部能被 §4.2.3 的 `SkillDef + EffectStep[]` 模型表达；若 AI 在实现中发现某技能无法表达，必须停下来扩展 `EffectStep.kind` 并更新本节。

**被动技能编码约定**：`SkillDef.SkillFlags bit0 = 1` 标记为被动技能，`TryCast()` 拒绝主动释放；`ApplyPassivesOnHit()` 在每次普攻命中时遍历英雄所有被动 skill，触发 `EffectStep`。被动技能的 `EffectStep.Param` 存储 buff 索引时必须用 `(Fix64)(int)buffIdx`（整数值），不得用 `Fix64.FromRaw(buffIdx)`。

---

## 8. 技能 / Buff 数据驱动管线

1. 策划在 `config/*.xlsx` 中编辑（`hero.xlsx`、`skill.xlsx`、`buff.xlsx`、`item.xlsx`、`level.xlsx`、`map_path.xlsx`）。
2. 运行 `tools/luban_export.cmd` → 生成 C# 数据类（`Cfg.*`）+ MemoryPack 二进制 `config.bin`。
3. 客户端 / 服务端启动时 `ConfigManager.Load("config.bin")`，全局只读。
4. 战斗循环只读配置，禁止运行时修改；属性变化走 `BuffInstance` 派生。

### 8.1 ConfigBinary 格式（v2，当前生产版本）

`config.bin` 为自定义二进制格式，各 Section 顺序写入，每 Section 有 2 字节 `TypeId` + 4 字节 `Count` 头：

| Section TypeId | 内容 | 每条字节数 |
|---|---|---|
| 0x0001 | HeroDef | 114 |
| 0x0002 | SkillDef | **50**（含 `SkillFlags` 字节 + 1 pad） |
| 0x0003 | EffectStep | 28 |
| 0x0004 | BuffDef | 30 |
| 0x0005 | ItemDef | 40 |
| 0x0006 | LevelTable | 8 |
| 0x0007 | LanePaths | 4+8×16=132 |

**SkillDef 字节布局（50 字节）**：`u16 id | u8 ownerHeroDefId | u8 stepCount | u16 stepStart | u32 cdFrames | f64 manaCost | f64 castRange | u8 cast | u8 hitShape | u32 preCastFrames | f64 hitParamA | f64 hitParamB | u8 skillFlags | u8 pad`

> `SkillFlags bit0=IsPassive` 必须正确序列化，否则被动技能加载后失效（v3.1 已修复此 bug）。

---

## 9. 确定性自检与回放

### 9.1 State Hash
- 每逻辑帧末：`hash = xxHash64(WorldSnapshot.SerializeToBytes())`。
- 客户端每 30 帧把最近 30 个 hash 上报服务端；服务端按帧号比对 10 玩家 hash。
- 不一致即触发 desync 处理：dump 双方快照 + 输入序列，写日志，房间继续（不强制断开）。

### 9.2 Replay 文件格式（`.mreplay`）
```
Header { magic="MRPL", version=1, mapId, seed, players[10], startUnixSec, durationFrames }
InitialSnapshot { byte[] memoryPackBytes }
InputStream    { for each frame: InputFrame[10] }
```
- Replay 由服务端在房间结束时写出。
- 客户端 `--replay <file>` 模式：加载 InitialSnapshot → 关闭网络 → 顺序喂入 InputStream → 渲染。

### 9.3 CI 自动化
- GitHub Actions：在 Ubuntu + Windows 各跑一次 `MOBA.Logic.Tests` 项目，跑 100 局 mock replay，要求两平台、所有局 hash 全部一致。

---

## 10. 性能预算（5v5 实测目标）
| 项目 | 上限 | 测量方法 |
|---|---|---|
| 单逻辑帧总耗时（客户端） | < 8 ms | BenchmarkDotNet |
| 单逻辑帧总耗时（服务端） | < 2 ms | dotnet-trace |
| 单帧 GC 分配 | 0 字节 | `[NoAlloc]` 单元测试 + `GC.GetAllocatedBytesForCurrentThread` 比对 |
| 实体峰值 | 250（10 英雄 + 90 小兵 + 18 塔 + 8 野怪 + 投射物上限 80 + 余量） | 压测脚本 |
| RangeQuery 单帧次数 | < 200 | 压测脚本 |
| KCP 平均 RTT（局域网） | < 30 ms | Ping/Pong |

---

## 11. 阶段性开发任务（Milestones）

> AI 每次只关注**当前 Milestone**。每个 Milestone 必须满足 DoD 才能进入下一阶段。

### Milestone 0：技术验证 Spike（独立 demo，可丢弃）
**目的**：在投入大规模实现前，验证三大风险点。
- **S0.1 定点数 + PRNG 跨平台一致性**
  - 任务：`MOBA.Logic.Tests.DeterminismSpike`，跑 10 万次随机 sin/cos/sqrt/atan2 + xorshift128+，输出最终 hash。
  - DoD：Windows x64 / Linux x64 / Android arm64 三端 hash 完全一致。
- **S0.2 Box2DSharp-deterministic 一致性**
  - 任务：1000 个随机半径圆体在 50×50 盒子内自由碰撞 1000 步，输出 (Σpos.x, Σpos.y, Σvel.x, Σvel.y) 的 hash。
  - DoD：上述三端 hash 一致。
- **S0.3 World 快照往返**
  - 任务：S0.2 跑到第 500 步时 `WriteSnapshot` → 新建 World `ReadSnapshot` → 再跑 500 步 → 与原始第 1000 步 hash 比对。
  - DoD：hash 一致；接受首帧 contact 重建抖动（用第 501 帧后 hash 比对，跳过第 500→501 这一帧）。
- **S0.4 KCP 10 客户端抖动测试**
  - 任务：1 服务端 + 10 客户端在本机用 clumsy 注入 100ms±50ms 抖动 + 5% 丢包，跑 5 分钟。
  - DoD：所有客户端无 desync，平均输入延迟 < 200ms。

**M0 通过后才开始 M1**。任何 Spike 失败，回到本文档加修订并暂停。

### Milestone 1：5v5 性能基建
**任务**：
- `Pool<T>`、`PhysicsWorldManager` 包装、`Fix64` 扩展、`XorShift128Plus`、`MobaDeterminismAnalyzer`。
- 程序集 `MOBA.Logic`、`MOBA.Shared`、`MOBA.Net`、`MOBA.Server` 骨架建立并通过 Analyzer。

**DoD**：
- 对象池 `BenchmarkDotNet`：100 万次 `Get/Return` 总分配 = 0 字节。
- `PhysicsWorldManager`：500 圆体 Step 1 帧 < 2 ms。
- Analyzer：在 `MOBA.Logic` 中写 `var v = new Vector3()` 即编译失败。

### Milestone 2：寻路与物理驱动
**任务**：`GridPathfinder`、`MovementSystem`、地图烘焙工具（`map_grid.png` → `byte[40000]`）。

**DoD**：
- 200×200 网格随机起终点 1000 次寻路，平均 < 2 ms / 次，max < 8 ms。
- 100 实体同时寻路 + 物理推进，单逻辑帧 < 8 ms。
- 单元测试覆盖：路径起终点同格、起点不可达、终点不可达、对角穿墙四种 corner case。

### Milestone 3：KCP 多玩家同步主循环
**任务**：协议层、`Room`、客户端 `NetClient`、追帧逻辑、Snapshot 存档与重连。

**DoD**：
- 10 客户端 LAN 跑 5 分钟无 desync（hash 全一致）。
- 杀掉 1 客户端等 10s 再启动同一 player → 自动 ResyncSnapshot 成功，hash 与其他 9 客户端一致。
- clumsy 100ms±50ms + 5% 丢包下，房间继续运行 5 分钟无崩溃。

### Milestone 4：逻辑抽象与兵线系统
**任务**：`MinionLogic`、`SpawnerLogic`、`TowerLogic`、`HeroLogic`（占位移动+普攻）、`ProjectileLogic`、Buff 框架。

**DoD**：
- 三路兵线连续刷 10 分钟，Profiler 中 GC.Alloc 增长 = 0。
- 250 实体共存场景，客户端单逻辑帧 < 8 ms（Win10 Ryzen 5 5600 基准）。

### Milestone 5：碰撞与战斗闭环 ✅
**任务**：完整技能系统（`SkillDef`+`EffectStep`）、3 个示例英雄全部 12 技能、装备 / 商店、视野系统、复活、水晶判胜负、Replay 写出。

**DoD**：
- 一局完整对战可玩通关并写出 `.mreplay`。
- Replay 回放 hash 与原局每帧一致。
- §10 全部性能指标达标。

**已通过（M5.1–M5.7 + MServer1–3 + MClient2–3 + MConfig MC.1–5）**

---

### Milestone 6：野怪、攻城兵、连杀奖励 ✅
**任务**：`JungleSystem`、攻城兵刷新逻辑、连杀 gold bonus。

**DoD**：
- M6.1：第 30 波（waveIdx=30）每路每队各产出 1 辆攻城兵（共 6 辆）。
- M6.2：野怪在帧 900（60s）初始生成；击杀后帧 1350（90s）重刷；零 GC。
- M6.3：连杀金奖 = `(streak-1)×50`，上限 +300；死亡重置 streak。
- M6.4：1000 帧带野怪循环 GC 增量 = 0 字节。

**已通过**

---

### Milestone 7：三英雄精确规格验收 ✅
**任务**：确保三英雄基础属性、普攻公式、技能效果（陨石眩晕、冲锋前摇、仇恨优先级）严格符合 §7.7。

**DoD（9项全过）**：
- M7.1/2/3：剑士、法师、射手基础属性与 §7.7 完全一致。
- M7.4：法师普攻 = 30 + 0.3×AP，魔法伤害。
- M7.5：剑士普攻 = 1.0×AD，物理伤害。
- M7.6：陨石命中施加 1s（15帧）眩晕 buff。
- M7.7：冲锋 `PreCastFrames = 5`（0.3s×15Hz 取整）。
- M7.8：小兵优先攻击被敌方攻击的友方小兵，次选最近敌方英雄。
- M7.9：塔优先攻击攻击友方英雄的敌方英雄，次选最近敌方小兵。

**已通过**

---

### Milestone 8：攻速、CDR、被动技能验证 ✅
**任务**：修复攻速 CD 硬编码、破甲 Param 序列化 bug、射手攻速成长缺失；修复 ConfigBinary SkillFlags 未序列化；验收 §7.2/§7.7。

**DoD（5项全过）**：
- M8.1：三英雄攻击间隔 = `floor(15/AS)` 帧（剑士21帧、法师25帧、射手17帧）。
- M8.2：射手每升一级 AS +0.03（lv1→2: 0.85→0.88）；剑士 AS 无变化。
- M8.3：剑士普攻命中后正确叠加破甲 buff（最多 5 层）。
- M8.4：射手「狩猎之眼」对 HP<50% 目标伤害 ×1.2；HP≥50% 时无加成。
- M8.5：CDR=20 时 90 帧 CD → 72 帧；CDR=50（超上限钳制为 40）→ 54 帧。

**MCrossPlat baseline（v3.1 后）：`0xB37DF4E2997C240B`**

**已通过（CI exit=0）**

---

### Milestone 9：经济与进阶系统验证 ✅
**任务**：对 §7.3 / §7.4 中已实现但缺少独立 M 系列测试的系统进行验收：被动金币滴落、击塔全队奖励、XP→升级→属性成长完整链路、商店购买四种边界、装备 CDR 上限钳制。

**DoD（5项全过）**：
- M9.1：被动金币 — 运行 80 逻辑帧后存活英雄 gold 增加 10（每 8 帧 +1g，触发 10 次）。
- M9.2：击塔金奖 — `AwardTowerKill(heroes, killerSlot)` 使攻击方全队存活成员各 +150g，己方死者 / 敌方均不得。
- M9.3：XP→升级链路 — 剑士累积 280 XP 后升至 2 级，HP+80=680、AD+5=65；再给 1 XP 不触发升级（需 380）。
- M9.4：商店购买 — 泉水内购买成功（扣金 / 加装备 / 属性生效）；离泉水失败 `NotInFountain`；金不足失败 `NoGold`；背包满失败 `InventoryFull`；死亡失败 `Dead`。
- M9.5：装备 CDR 上限 — 购买前 CDR=35，`ApplyItem` 追加 +10 CDR 后 CDR=40（钳制，非 45）。

**已通过（CI exit=0）**

---

### Milestone 10：Buff 生命周期与技能条件验证
**任务**：对 §7.7 中已实现但尚无专项 M 系列测试的 Buff 效果和技能门控系统进行验收：HoT 分帧治疗、晕眩锁技能、执行斩首条件、冰墙生成到期、Buff Refresh 刷新策略。

**DoD（5 项全过）**：
- M10.1：HoT 治疗 — 对血量 -40 的英雄施加 `BuffShield`（每 15 帧 +20 HP，持续 45 帧）；第 15 帧 HP+20，第 30 帧 HP 补满；第 45 帧 Buff 到期不再触发第三次治疗。
- M10.2：晕眩锁技能 — 对英雄施加 `BuffStun10`；`BuffEngine.Apply` 后 `Tags & Stunned != 0`；`SkillSystem.TryCast` 返回 `false`；第 15 帧 Buff 到期后 `Tags & Stunned == 0`，TryCast 返回 `true`。
- M10.3：斩首执行条件 — 剑士 R 技 `CastTargetHpMaxPct=0.30`：目标 HP/MaxHp=35% 时 TryCast 返回 `false`；目标 HP/MaxHp=25% 时返回 `true`。
- M10.4：冰墙生成与到期 — 施放法师 W（冰墙）后 `Walls[i].Alive==true`、`ExpireFrame==45`；对 `Walls` 数组应用到期逻辑后（`frame >= ExpireFrame`），`Alive==false`。
- M10.5：Buff Refresh 刷新策略 — 第 0 帧施加 `BuffSlow30`（EndFrame=30）；第 20 帧再施加（EndFrame 刷新至 50）；第 31 帧 `Slowed` 标签仍存在；第 50 帧到期后标签消失。

**已通过（CI exit=0）**

---

### Milestone 11：投射物系统、瞬移技能与复活计时器验证
**任务**：对 §4.2.4 / §7.6 / §7.7 中已实现但尚无专项 M 系列测试的投射物生命周期、瞬移效果和复活计时器公式进行验收。

**DoD（5 项全过）**：
- M11.1：火球投射物 — TryCast(法师 Q) 后 Projectiles[0].Alive=true，Velocity.X>0（朝目标方向），ExpireFrame=frame+10（life=10帧）。
- M11.2：旋风斩 AOE 伤害 — 剑士 W，aim=施法者位置，2m 圆内敌方英雄收到 DamageEvent（Damage=90=60+0.5×60），圆外英雄（3m 处）不受影响。
- M11.3：闪现瞬移 — TryCast(法师 E，aim=(2,0)) 后施法者 Pos 精确等于 (2,0)（CastRange=3m，Manhattan距离2≤3，无截断）。
- M11.4：陨石延迟 AoE — TryCast(法师 R) 生成 AoeOnExpiry=true 投射物，ExpireFrame=12；第 11 帧 TickProjectiles 无伤害；第 12 帧触发，DamageEvent.Damage=310（250+1.2×AP(50)）。
- M11.5：复活计时器公式 — Respawn.FramesFor(lv=1,f=0)=105；Respawn.FramesFor(lv=5,f=900)=255（gameMinute=1）；Respawn.FramesFor(lv=18,f=9000)=900（上限60s）。

**已通过（CI exit=0）**

---

## 12. 文档维护规则
1. 任何时候 AI 发现本文档表达不足以驱动当前编码任务，**必须停下并提问**，由人类更新本文档版本号后再继续。
2. 本文档优先级 > 任何对话中的临时指示（除非临时指示明确说"修订 PRD"）。
3. 版本号规则：补丁式修订 v3.0.x，新增章节 v3.x，重大方向变更 v4.0。
