# MOBA 项目使用手册

> 版本：1.0 | 日期：2026-05-01 | 项目路径：`d:\workspace\moba`

---

## 目录

1. [项目结构概览](#1-项目结构概览)
2. [前置环境要求](#2-前置环境要求)
3. [后端服务器](#3-后端服务器)
   - 3.1 [构建](#31-构建)
   - 3.2 [运行与 CLI 参数](#32-运行与-cli-参数)
   - 3.3 [配置文件 appsettingsjson](#33-配置文件-appsettingsjson)
   - 3.4 [生产部署](#34-生产部署)
   - 3.5 [调试与日志](#35-调试与日志)
   - 3.6 [Replay 录制与回放](#36-replay-录制与回放)
   - 3.7 [Desync 检测与自动重同步](#37-desync-检测与自动重同步)
4. [前端客户端（Unity）](#4-前端客户端unity)
   - 4.1 [打开项目](#41-打开项目)
   - 4.2 [场景结构](#42-场景结构)
   - 4.3 [连接服务器](#43-连接服务器)
   - 4.4 [帧驱动逻辑接入](#44-帧驱动逻辑接入)
   - 4.5 [Unity 调试技巧](#45-unity-调试技巧)
5. [工具文档](#5-工具文档)
   - 5.1 [ConfigBaker — 生成 config.bin](#51-configbaker--生成-configbin)
   - 5.2 [测试运行器](#52-测试运行器)
6. [CI/CD 流水线](#6-cicd-流水线)
7. [美术资源管理](#7-美术资源管理)
8. [常见问题排查](#8-常见问题排查)

---

## 1 项目结构概览

```
moba/                        ← 仓库根目录
├── docs/                    ← 文档（PRD、本手册）
├── moba/                    ← Unity 客户端工程
│   └── Assets/
│       ├── Scenes/          ← 场景文件 (.unity)
│       ├── Settings/        ← URP 渲染管线资产
│       └── TutorialInfo/    ← 起步教学资源
├── server/                  ← .NET 8 服务器 & 逻辑库
│   ├── config/              ← 编译好的二进制配置 config.bin
│   ├── src/
│   │   ├── MOBA.Logic/      ← 纯逻辑库（确定性仿真、技能、Buff、配置）
│   │   ├── MOBA.Net/        ← KCP 网络客户端封装
│   │   ├── MOBA.Server/     ← 帧中继房间逻辑
│   │   ├── MOBA.ServerHost/ ← 可执行入口（含 appsettings.json）
│   │   └── MOBA.Shared/     ← 协议定义、数学工具
│   ├── test/
│   │   └── MOBA.Logic.Tests/← 所有里程碑验收测试
│   └── tools/
│       └── ConfigBaker/     ← 将英雄/技能/Buff 数据烘焙为 config.bin
└── .github/workflows/ci.yml ← GitHub Actions CI 配置
```

**关键架构原则（PRD §5.3）**：服务器**不做权威物理模拟**，只中继输入帧；所有确定性仿真在客户端本地 `DeterministicWorld` 中以 **15 Hz 锁步**运行。

---

## 2 前置环境要求

| 工具 | 最低版本 | 用途 |
|------|---------|------|
| .NET SDK | 8.0 | 服务器构建与运行 |
| Unity Editor | 2022.3 LTS（URP） | 客户端开发 |
| Git | 2.x | 版本管理 |
| PowerShell | 5.1（Windows）/ pwsh 7（跨平台） | 脚本执行 |

> **安装 .NET 8**：https://dotnet.microsoft.com/download/dotnet/8.0  
> **验证**：`dotnet --version` 输出应以 `8.0` 开头。

---

## 3 后端服务器

### 3.1 构建

```powershell
# 构建所有项目（在 server/ 目录下执行）
cd d:\workspace\moba\server
dotnet build MOBA.slnx -c Release
```

仅构建可执行入口：

```powershell
dotnet build src/MOBA.ServerHost/MOBA.ServerHost.csproj -c Release
```

构建产物位于：  
`server/src/MOBA.ServerHost/bin/Release/net8.0/`

---

### 3.2 运行与 CLI 参数

**开发模式（直接 run）**：

```powershell
cd d:\workspace\moba\server
dotnet run --project src/MOBA.ServerHost/MOBA.ServerHost.csproj -c Release
```

**生产模式（发布后执行）**：

```powershell
dotnet publish src/MOBA.ServerHost/MOBA.ServerHost.csproj -c Release -o ./publish
./publish/MOBA.ServerHost.exe
```

#### 完整 CLI 参数表

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `--port <n>` | ushort | `7777` | 基础 UDP 端口；第 N 个房间监听 `basePort+N` |
| `--rooms <n>` | int | `1` | 同时开启的房间数（上限 256） |
| `--seed <ulong>` | ulong | `0xDEADBEEFCAFEBABE` | 仿真 PRNG 种子 |
| `--replay <dir>` | string | 无 | 若指定，对局结束后在该目录写入 `.mreplay` 文件 |
| `--log-desync` | flag | 关 | 开启后，每帧哈希不一致时向 stderr 输出 `DESYNC` 行 |
| `--metrics-port <n>` | ushort | `0`（关） | Prometheus 格式 `/metrics` HTTP 端口 |

**示例**：开启 4 个房间，录制 Replay，开启 Desync 日志：

```powershell
dotnet run --project src/MOBA.ServerHost/MOBA.ServerHost.csproj -c Release -- `
    --port 7777 --rooms 4 --replay D:\replays --log-desync
```

---

### 3.3 配置文件 appsettings.json

文件路径：`server/src/MOBA.ServerHost/appsettings.json`  
**CLI 参数优先级高于此文件**。

```jsonc
{
  "Server": {
    "BasePort":     7777,      // 基础 UDP 端口
    "MaxRooms":     1,         // 房间数
    "Seed":         "16045690984833335230",  // 十进制 ulong 种子
    "ReplayDir":    null,      // replay 输出目录（null = 不录制）
    "LogDesync":    false,     // 是否打印 DESYNC 日志
    "MetricsPort":  0          // Prometheus 端口（0 = 关闭）
  },
  "Kcp": {
    "NodeDelay":         true,   // KCP 无延迟模式
    "Interval":          10,     // KCP 内部时钟间隔 (ms)
    "FastResend":        2,      // 快速重传阈值
    "CongestionWindow":  false,  // 关闭拥塞窗口（局域网/专线推荐）
    "Mtu":               1200,   // 最大传输单元
    "SendWindow":        128,    // 发送窗口（帧数）
    "ReceiveWindow":     128,    // 接收窗口（帧数）
    "Timeout":           15000   // 连接超时 (ms)
  }
}
```

> **生产调优提示**：高延迟外网对局可适当调大 `Timeout`（如 `30000`）、`SendWindow`/`ReceiveWindow`（如 `256`）。

---

### 3.4 生产部署

#### 单机部署（Linux systemd）

1. 在目标机器上安装 .NET 8 Runtime（不需要 SDK）：

   ```bash
   sudo apt-get install -y dotnet-runtime-8.0
   ```

2. 将 `publish/` 目录上传至服务器，例如 `/opt/moba-server/`。

3. 创建 systemd 服务文件 `/etc/systemd/system/moba-server.service`：

   ```ini
   [Unit]
   Description=MOBA Dedicated Server
   After=network.target

   [Service]
   WorkingDirectory=/opt/moba-server
   ExecStart=/opt/moba-server/MOBA.ServerHost --port 7777 --rooms 4 --replay /var/replays
   Restart=on-failure
   RestartSec=5
   User=mobaserver

   [Install]
   WantedBy=multi-user.target
   ```

4. 启动并设为开机自启：

   ```bash
   sudo systemctl enable --now moba-server
   sudo journalctl -u moba-server -f   # 实时日志
   ```

#### 防火墙放行

```bash
# 开放 4 个房间所需端口（基础端口起连续 N 个 UDP 端口）
sudo ufw allow 7777:7780/udp
# 如启用 Metrics HTTP
sudo ufw allow 9090/tcp
```

#### config.bin 部署

服务器需从工作目录或环境路径读取 `config.bin`（路径由 `ConfigManager.LoadFromFile()` 决定）。标准位置为与可执行文件同级目录，或通过启动脚本设定：

```bash
export MOBA_CONFIG=/etc/moba/config.bin
/opt/moba-server/MOBA.ServerHost
```

---

### 3.5 调试与日志

#### 标准输出格式

启动后控制台输出示例：

```
[ServerHost] room 0 listening on udp/7777
[ServerHost] room 1 listening on udp/7778
```

每个房间的运行日志前缀为 `[Room N]`，包含：
- 玩家连接/断开事件
- 每帧广播统计（当 `--log-desync` 开启时）
- Resync 触发记录

#### Desync 日志（`--log-desync`）

每帧若任意两个客户端上报的哈希不一致，输出：

```
DESYNC frame=1234 slotA=0 hashA=0xAABBCCDD slotB=3 hashB=0x11223344
```

排查 Desync 的步骤：
1. 保存双端 Replay（`--replay`）
2. 离线回放对比（见 §3.6）
3. 查看 `MCrossPlat_baseline.txt` 是否与当前哈希一致

#### 对局后诊断

`RoomHost` 暴露以下诊断属性（可在 Metrics 端口或运营工具中查询）：

| 属性 | 含义 |
|------|------|
| `DesyncDetected` | 是否发生 Desync |
| `DesyncFrame` | 首次 Desync 帧号 |
| `AutoResyncCount` | 自动重同步次数 |
| `ResyncThrottled` | 因节流跳过的 resync 次数 |
| `SpectatorCount` | 观战连接数 |
| `MatchEnded` | 对局是否结束 |

---

### 3.6 Replay 录制与回放

#### 录制

启动服务器时加 `--replay <dir>`，对局结束后自动写入：

```
<dir>/room-0.blue.mreplay   ← 蓝队获胜时文件名带 .blue
<dir>/room-0.red.mreplay
```

文件由 `ReplayWriter` 写出，格式（`src/MOBA.Logic/Replay/ReplayWriter.cs`）：
- 文件头：`MRPL` + 版本 + 种子 + 玩家槽位信息
- 每帧：帧号 + 10 路 `InputFrame` 压缩序列

#### 离线回放

```csharp
// 示例：读取 replay 并重播到任意帧
using var reader = new ReplayReader(File.OpenRead("room-0.blue.mreplay"));
var world = new DeterministicWorld { EnableGameplay = true };
while (reader.TryReadFrame(out uint frame, out InputFrame[] inputs))
{
    world.Tick(inputs);
    // 在此插入断点或哈希比对
}
```

---

### 3.7 Desync 检测与自动重同步

**检测机制**：每位玩家每帧上报本地 `World.Hash()` → 服务器对比所有哈希 → 不一致时标记 `DesyncDetected`。

**自动重同步**（`AutoResyncOnDesync = true`，默认开启）：服务器向哈希异常的客户端推送最近的快照（`S2C_Snapshot`），客户端收到后用 `World.ApplySnapshot()` 恢复状态，并从快照帧继续回放缺失输入。

**节流**：相同槽位两次 Resync 间隔不得小于 `ResyncThrottleMs`（默认 500 ms），防止网络抖动引起的 Resync 风暴。

---

## 4 前端客户端（Unity）

### 4.1 打开项目

1. 启动 **Unity Hub**，点击 **Add** → 选择 `d:\workspace\moba\moba` 目录。
2. 使用 **Unity 2022.3 LTS**（URP 模板）打开项目。
3. 首次打开会自动导入资产并编译 URP Shader，等待完成（约 1–3 分钟）。

> **注意**：`moba/Library/`、`moba/Temp/` 已加入 `.gitignore`，切换分支后可能需要重新导入。若 Unity 报「Library 损坏」，删除 `Library/` 目录后重新打开即可。

---

### 4.2 场景结构

| 场景 | 路径 | 说明 |
|------|------|------|
| `SampleScene` | `Assets/Scenes/SampleScene.unity` | 当前主开发场景（URP 示例） |

场景层级规划（待实现）：

```
SampleScene
├── Main Camera
├── GameManager          ← 管理 NetClient + DeterministicWorld
├── UICanvas             ← HUD、技能栏、血条
├── Map                  ← 地形、泳道、建筑
│   ├── Towers/
│   └── Crystals/
├── Heroes/              ← 10 个英雄 GameObject（按 slot 编号）
└── Lighting             ← URP 全局光照（SampleSceneProfile.asset）
```

---

### 4.3 连接服务器

在 Unity 端使用 `MOBA.Net.NetClient`（已编译为 `MOBA.Net.dll`，放置于 `Assets/Plugins/`）：

```csharp
using MOBA.Net;

public class GameManager : MonoBehaviour
{
    NetClient _net;

    void Start()
    {
        _net = new NetClient();
        // 连接到本地服务器（开发用）
        _net.Client.Connect("127.0.0.1", 7777);
    }

    void Update()
    {
        // 每 Unity 帧驱动 KCP 网络层（不是逻辑帧）
        _net.Tick();
    }
}
```

#### 关键事件

| 属性/方法 | 触发时机 |
|----------|---------|
| `_net.Connected` | KCP 握手完成 |
| `_net.RoomStarted` | 收到 `S2C_RoomStart`，可开始逻辑仿真 |
| `_net.RxFrames.TryDequeue(...)` | 每逻辑帧消费服务器广播的输入帧 |
| `_net.PendingSnapshot` | 收到服务器推送的快照（Resync 场景） |
| `_net.MatchEnded` | 收到 `S2C_GameOver` |

---

### 4.4 帧驱动逻辑接入

逻辑帧率固定为 **15 Hz**（`DeterministicWorld.TicksPerSecond = 15`）。  
推荐在 Unity 中使用 **FixedUpdate**（将 Fixed Timestep 改为 `1/15 ≈ 0.0667s`）或独立线程：

```csharp
// PlayerSettings → Time → Fixed Timestep = 0.0667

DeterministicWorld _world;
InputFrame[] _localInputs = new InputFrame[10];

void FixedUpdate()
{
    _net.Tick(); // 驱动网络层

    // 消费服务器广播的一帧
    if (_net.RxFrames.TryDequeue(out var (frame, inputs)))
    {
        _world.Tick(inputs);
        // 用 _world.Heroes[i].Pos 更新 GameObject 位置
        SyncTransforms();
    }
}
```

> **注意**：`DeterministicWorld.Tick` 使用 `Fix64`（定点数）运算，所有计算在逻辑层完成；渲染层只读取 `Hero.Pos` 等字段，用 `(float)pos.X` 转换为 Unity 坐标。

---

### 4.5 Unity 调试技巧

#### 使用 Console 打印逻辑状态

```csharp
Debug.Log($"Frame={_world.Frame} Hero0 HP={_world.Heroes[0].Hp}");
```

#### Inspector 实时查看

在 `MonoBehaviour` 中暴露字段供 Inspector 查看：

```csharp
[SerializeField] float _hero0HpDisplay;

void OnGUI() => _hero0HpDisplay = (float)_world.Heroes[0].Hp;
```

#### 哈希一致性验证

本地双实例测试（Editor + Build）时，每帧打印 `_world.Hash()` 对比：

```csharp
Debug.Log($"Frame={_world.Frame} Hash=0x{_world.Hash():X16}");
```

#### URP 渲染管线配置

渲染质量档位在 `Assets/Settings/URP-Balanced-Renderer.asset` 中配置；  
后期处理在 `Assets/Settings/SampleSceneProfile.asset` 中调整（Bloom、色调映射等）。

---

## 5 工具文档

### 5.1 ConfigBaker — 生成 config.bin

**功能**：将英雄属性、技能定义、Buff 表、道具、等级经验表等数据序列化为 `server/config/config.bin`，供服务器和客户端在启动时通过 `ConfigManager.LoadFromBytes()` 加载。

**源码**：`server/tools/ConfigBaker/Program.cs`

#### 运行

```powershell
cd d:\workspace\moba\server
# 输出到默认路径 server/config/config.bin
dotnet run --project tools/ConfigBaker/ConfigBaker.csproj -c Release

# 输出到自定义目录
dotnet run --project tools/ConfigBaker/ConfigBaker.csproj -c Release -- D:\my-config
```

成功后输出：

```
[ConfigBaker] Wrote 2856 bytes to D:\workspace\moba\server\config\config.bin
```

#### 何时需要重新烘焙

- 修改了英雄基础属性（HP、AD、技能 ID 等）
- 新增或修改技能定义（`BuiltinContent.cs`）
- 新增 Buff 或道具
- 调整等级经验表或泳道路点

烘焙完成后需要**提交 `config/config.bin`** 到版本库。

#### config.bin 格式

```
[Header]  "MCFG" (4字节) + Version=2 (u16) + SectionCount (u16)
[Sections] 每节：SectionId (u16) + RecordCount (u32) + [records]
```

| Section ID | 内容 | 记录大小 |
|------------|------|---------|
| `0x0001` | CfgHero（英雄基础属性） | 114 字节 |
| `0x0002` | SkillDef（技能定义） | 58 字节 |
| `0x0003` | EffectStep（技能效果步骤） | 28 字节 |
| `0x0004` | BuffDef（Buff 定义） | 30 字节 |
| `0x0005` | ItemDef（道具定义） | 66 字节 |
| `0x0006` | CfgLevel（等级经验表） | 8 字节 |
| `0x0007` | CfgLane（泳道路点） | 132 字节 |

全部字段采用**小端序**，定点数（Fix64）存为 `int64`（Q32.32）。

#### 扩展数据流程

若要在 Excel/Google Sheets 中维护数据（类 Luban 流程）：

1. 在表格中维护数据 → 导出为 CSV / JSON。
2. 修改 `ConfigBaker/Program.cs` 读取 CSV/JSON 代替硬编码。
3. 运行 `dotnet run --project tools/ConfigBaker` 生成新 `config.bin`。
4. 提交 `config.bin`（二进制 diff 可读性有限，建议同时提交数据源文件）。

---

### 5.2 测试运行器

测试入口：`server/test/MOBA.Logic.Tests/`

#### 运行命令

```powershell
cd d:\workspace\moba\server

# 运行所有测试（含 300s KCP 耐久测试，总耗时约 5 分钟）
dotnet run --project test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release -- all

# CI 快速模式（跳过 S0.4 KCP 抖动和网络测试，耗时约 30s）
dotnet run --project test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release -- ci

# 运行单个里程碑
dotnet run --project test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release -- m10

# 不重新构建（适合调试循环）
dotnet run --project test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release --no-build -- ci
```

#### 可用测试 ID

| 参数 | 测试内容 |
|------|---------|
| `s0.1` | Fix64 确定性（10 万次运算哈希） |
| `s0.2` | Box2DSharp 物理确定性（1000 圆体 × 1000 步） |
| `s0.3` | 快照序列化往返（Snapshot round-trip） |
| `s0.4` | KCP 抖动耐久（300s，需联网环境） |
| `m1` | 物理池 GC 零分配 |
| `m2` | 200×200 寻路 A* 性能 |
| `m3` | 10 客户端 KCP 同步（需本地网络） |
| `m4` | 完整 60s 游戏循环 GC 验证 |
| `m5.1`–`m5.7` | 技能、道具、视野、水晶、经济、Replay |
| `mc` / `mconfig` | 配置二进制往返（MC.1–MC.5） |
| `m6` | 攻城兵、野怪、击杀连杀金币 |
| `m7` | 三英雄精确属性（9项） |
| `m8` | 攻速、冷却缩减、被动技能 |
| `m9` | 经济与成长体系 |
| `m10` | Buff 生命周期与技能条件 |
| `mcrossplat` | 跨平台确定性（100局 Replay 哈希比对） |
| `ci` | 上述所有测试的快速子集（30s） |

#### 解读输出

```
M10 Verify
  OK    M10.1 HoT buff ticks +20 HP at f15/f30, expires at f45   ← 单项通过
  FAIL  M10.3 ExecuteCondition: blocked when target HP=35%        ← 单项失败
  PASS  M10 all assertions                                        ← 全部通过时出现

Command exited with code 0    ← 0=全部通过，非0=有失败
```

#### 跨平台哈希基线

`MCrossPlat_baseline.txt` 存储当前基准哈希值（`0x5BCEA2EB65358ECF`）。若修改了确定性仿真代码（如修改 `ConfigBinary` 格式、调整伤害公式），需重新生成基线：

```powershell
# 1. 运行跨平台测试，记录输出的 Combined 哈希
dotnet run --project test/MOBA.Logic.Tests -c Release -- mcrossplat

# 2. 将 Combined 值写入基线文件
Set-Content test/MOBA.Logic.Tests/MCrossPlat_baseline.txt "0x新哈希值"

# 3. 提交
```

---

## 6 CI/CD 流水线

配置文件：`.github/workflows/ci.yml`

#### 触发条件

- `main` 分支的每次 push
- 所有 Pull Request

#### 任务矩阵

| Job | 运行环境 | 内容 |
|-----|---------|------|
| `determinism-tests` | `ubuntu-latest` + `windows-latest` | 并行运行 CI 测试套件 |
| `compare-hashes` | `ubuntu-latest` | 对比两平台产物哈希，确保跨平台一致性 |

#### 本地模拟 CI

```powershell
cd d:\workspace\moba\server
dotnet restore test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj
dotnet build  test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release --no-restore
dotnet run    --project test/MOBA.Logic.Tests/MOBA.Logic.Tests.csproj -c Release --no-build -- ci
echo "EXIT: $LASTEXITCODE"   # 期望输出 EXIT: 0
```

#### 添加新测试里程碑到 CI

1. 创建 `server/test/MOBA.Logic.Tests/MXX_Verify.cs`。
2. 在 `Program.cs` 的 `RunCi()` 方法中注册（参照已有 M10 的注册方式）。
3. 本地运行 `-- ci` 确认通过后提交。

---

## 7 美术资源管理

> 本节描述已有的资产组织方式，以及接入确定性逻辑时的注意事项。

### 7.1 目录约定

```
moba/Assets/
├── Art/                     ← 美术资产根目录（建议按此规范建立）
│   ├── Characters/
│   │   ├── Swordsman/       ← 剑士：模型、动画、材质
│   │   ├── Mage/
│   │   └── Marksman/
│   ├── Environment/
│   │   ├── Map/             ← 地图地形、泳道、建筑
│   │   ├── Effects/         ← 技能特效 VFX
│   │   └── UI/              ← HUD 贴图、图标
│   └── Shared/
│       └── Materials/       ← 公用材质（URP Lit）
├── Prefabs/                 ← 预制体（英雄、小兵、塔）
├── Scenes/                  ← 场景文件（已有）
└── Settings/                ← URP 渲染管线资产（已有）
```

### 7.2 命名规范

| 类型 | 规则 | 示例 |
|------|------|------|
| 贴图 | `T_<对象>_<类型>` | `T_Swordsman_Albedo` |
| 材质 | `M_<对象>_<变体>` | `M_Swordsman_Base` |
| 模型（FBX） | `SM_<对象>` | `SM_Tower_Blue` |
| 特效粒子 | `VFX_<技能名>` | `VFX_IceWall_Spawn` |
| 动画 Clip | `Anim_<对象>_<动作>` | `Anim_Swordsman_Attack` |
| 预制体 | `Prefab_<对象>` | `Prefab_HeroSwordsman` |

### 7.3 URP 渲染设置

- **渲染器**：`Assets/Settings/URP-Balanced-Renderer.asset`（Deferred Rendering）
- **后期处理**：`Assets/Settings/SampleSceneProfile.asset`（Bloom + Tonemapping）
- **新材质**默认使用 `URP/Lit`，启用 GPU Instancing 以支持大量小兵。

### 7.4 技能特效与逻辑层解耦

**重要原则**：特效（VFX）、声音、动画均属于**表现层**，不得影响确定性逻辑。

接入方式：

```csharp
// 正确做法：监听逻辑事件，在表现层播放特效
void SyncTransforms()
{
    for (int i = 0; i < 10; i++)
    {
        var hero = _world.Heroes[i];
        _heroGOs[i].transform.position = new Vector3((float)hero.Pos.X, 0, (float)hero.Pos.Y);

        // 检测技能施放（通过 Projectile 数组或 DamageEvent）
        if (_world.Projectiles[i].Alive && !_prevProj[i])
            PlaySkillVFX(i);   // 纯表现，不影响逻辑
    }
}
```

### 7.5 图标与 UI 资产

技能图标命名：`Icon_Skill_<英雄>_<槽位>`（Q/W/E/R）

```
Icon_Skill_Swordsman_Q.png   ← 冲锋
Icon_Skill_Swordsman_W.png   ← 旋风斩
Icon_Skill_Swordsman_E.png   ← 破甲（被动）
Icon_Skill_Swordsman_R.png   ← 斩首
```

### 7.6 版本控制注意事项

- **二进制大文件**（FBX、贴图、音频）使用 **Git LFS**：

  ```bash
  git lfs track "*.png" "*.psd" "*.fbx" "*.wav" "*.mp3"
  git add .gitattributes
  ```

- Unity `.meta` 文件**必须提交**，否则 GUID 引用丢失。

- `Library/`、`Temp/`、`UserSettings/` 已在 `.gitignore` 中排除，**不要提交**。

- 场景文件（`.unity`）合并冲突时使用 **UnityYAMLMerge**：
  
  在 `.gitconfig` 中配置：
  ```ini
  [merge]
      tool = unityyamlmerge
  [mergetool "unityyamlmerge"]
      cmd = 'C:/Program Files/Unity/Hub/Editor/2022.3.x/Editor/Data/Tools/UnityYAMLMerge.exe' merge -p "$BASE" "$REMOTE" "$LOCAL" "$MERGED"
  ```

---

## 8 常见问题排查

### Q1：服务器启动后客户端无法连接

1. 确认防火墙放行了对应 UDP 端口：`netstat -an | findstr 7777`（Windows）。
2. 确认客户端连接地址正确（局域网用内网 IP，公网用外网 IP）。
3. 检查 `Timeout` 设置，KCP 默认 15000 ms，弱网环境适当调大。

### Q2：`config.bin` 加载失败 / `ConfigManager.IsLoaded = false`

1. 确认 `config.bin` 版本匹配（Header `Version` 字段，当前为 `2`）。
2. 重新运行 ConfigBaker 生成最新文件。
3. 确认文件路径正确（与可执行文件同级目录，或通过环境变量指定）。

### Q3：CI 测试 `MCrossPlat` 哈希不匹配

原因：修改了参与哈希计算的逻辑代码（伤害公式、配置序列化格式等）。

处理：
1. 确认修改是**有意为之**（而非意外引入的不确定性）。
2. 在两个平台（Linux + Windows）各运行一次 `-- mcrossplat`，确认哈希相同。
3. 更新 `MCrossPlat_baseline.txt`，提交。

### Q4：Unity 打开报错「The associated script cannot be loaded」

`MOBA.Logic.dll` 等程序集未放置到 `Assets/Plugins/` 目录，或 Unity 未识别 .NET 8 程序集。

处理：
1. 发布服务器逻辑库：`dotnet publish src/MOBA.Logic -c Release -f netstandard2.1`。
2. 将 DLL 拷贝到 `moba/Assets/Plugins/`。
3. 在 Plugin Inspector 中勾选目标平台。

### Q5：游戏内出现 Desync（画面不同步）

1. 开启 `--log-desync` 获取首次 Desync 帧号。
2. 录制 Replay（`--replay`），离线逐帧对比两端哈希。
3. 常见原因：
   - 表现层代码（Unity `Update`）意外修改了 `DeterministicWorld` 状态。
   - 使用了 `float`/`UnityEngine.Random` 而非 `Fix64`/`XorShift128Plus` 进行逻辑计算。
   - `ConfigBinary` 新增字段但未同步更新读写代码（参见 M10 修复案例）。

### Q6：`dotnet run -- ci` 返回非 0 退出码

查看具体 FAIL 行：

```powershell
dotnet run --project test/MOBA.Logic.Tests -c Release --no-build -- ci 2>&1 | Select-String "FAIL|Exception"
```

根据失败的测试 ID 单独调试：

```powershell
dotnet run --project test/MOBA.Logic.Tests -c Release --no-build -- m10
```

---

*本手册应与代码同步维护。每次修改 `ConfigBinary` 格式、新增里程碑测试或调整服务器 API 时，请同步更新对应章节。*
