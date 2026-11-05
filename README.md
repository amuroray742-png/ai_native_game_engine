# AI Native 游戏框架

可复用的纯 C# 玩法内核与工具链。GridGame（手动回合制）和 PhysicsSandbox（60 Hz 真实 Jolt 刚体）作为独立 Example，共用输入、快照、回放、比较和 CLI。

通用框架 v2 与当前 joltc ABI 4 分层适配已通过[Windows/Linux 双平台验收](https://github.com/rayss1/ai_native_game_engine/actions/runs/41837315399)。GridGame 的 Unity 入口是独立工程里的 Play 场景。PhysicsSandbox 的 Unity 接入、Player 与 IL2CPP 留给后续阶段。[设计](docs/ai-native-game-engine-design.md) · [v2 契约](docs/framework-v2.md) · [Play 宿主与验收](docs/unity-play-host.md)

物理后端现已迁移至固定版本 **joltc + 项目扩展**，支持显式0..16工作线程、完整状态恢复与同线程配置确定性回放。PhysicsSandbox场景使用游戏版本2和必填`configuration.workerThreads`（示例为2）。当前 ABI 4 将实体映射、快照元数据及恢复编排迁到 C#，原生层保留资源清理、事件收集和内部状态能力；旧物理记录需要重新生成。当前验证见[物理分层迁移](docs/managed-physics-migration.md)，此前记录见[joltc迁移](docs/joltc-migration.md)。

第三方依赖统一存放在 [`third_party/`](third_party/README.md)：Jolt/joltc源码由原生构建脚本获取，NuGet包由`dotnet restore --locked-mode`恢复到`third_party/nuget/`。

## 运行

需要 global.json 指定的 .NET 10 SDK。GridGame 不需要原生库：

```sh
dotnet restore --locked-mode
dotnet run --project examples/AiNative.Examples.Runner -- games
dotnet run --project examples/AiNative.Examples.Runner -- describe --game grid-game
dotnet run --project examples/AiNative.Examples.Runner -- run --scenario examples/data/grid.scenario.json --seed 42 --commands examples/data/grid.commands.json --snapshot-out grid.snapshot.json --replay-out grid.replay.json
dotnet run --project examples/AiNative.Examples.Runner -- replay --input grid.replay.json
dotnet run --project examples/AiNative.Examples.Runner -- inspect --snapshot grid.snapshot.json
```

物理示例先构建原生桥接。Windows x64 需要 MSVC C++ 工具（支持 VS 安装的 CMake/Ninja）；Linux x64 需要 C++17 编译器、CMake、Ninja、Git。Python 3.10+ 脚本会取得并检查固定 Jolt 与 joltc 源码提交，不安装全局软件：

```sh
python scripts/build_native.py
dotnet build
dotnet run --project examples/AiNative.Examples.Runner --no-build -- run --scenario examples/data/physics.scenario.json --seed 42 --commands examples/data/physics.commands.json --snapshot-out physics.snapshot.json --replay-out physics.replay.json
dotnet run --project examples/AiNative.Examples.Runner --no-build -- replay --input physics.replay.json
```

输出目标必须不存在且父目录存在。显式相对路径按当前工作目录解析。stdout 一份 JSON，stderr 诊断；退出码 0 完成、1 宿主/模拟故障、2 输入错误、3 回放分歧。无参数或 `--help` 显示帮助。

`run --snapshot FILE --commands FILE` 继续已有对局；`--expect-config SCENE` 可检查内嵌配置。恢复和 replay 要求同构建、同环境；`compare --expected A --actual B` 只比较两份同起点、同配置、同操作的证据，允许来源环境不同，不运行游戏、不加载 Jolt。

## 模块

| 位置 | 职责 |
| --- | --- |
| src/AiNative.Core | 泛型运行时、输入调度、Tick、随机、故障及生命周期 |
| src/AiNative.Hosting | 模块契约、内存协议、快照、回放、差异、两种表现缓冲 |
| src/AiNative.Headless | 可复用宿主库，文件、环境与 CLI 编排 |
| src/AiNative.Physics / Physics.Jolt | 可选物理接口、托管实体映射/快照/恢复及 netstandard2.1 DllImport 适配 |
| examples/AiNative.GridGame / GridGame.Hosting | 网格规则与专属协议 |
| examples/AiNative.PhysicsSandbox / PhysicsSandbox.Hosting | 实时物理规则与专属协议 |
| examples/AiNative.Examples.Runner | 无头示例可执行入口，显式注册游戏与物理工厂 |
| packages/com.ainative.unity | 场景生命周期、手动与固定步长推进、类型化 Tick 交付 |
| examples/unity/GridGame | 独立 Unity 工程，含 Play 场景、Sprite 棋盘、UGUI HUD 和 PlayMode 验收 |
| native/jolt | 原生资源聚合、BodyID 批量接口、内部状态验证及必要 joltc 扩展 |
| third_party | 下载的 Jolt/joltc 源码与 NuGet 包；版本锁定，下载内容不提交 Git |

Core 和可移植库保持 netstandard2.1 / C# 9；Headless、Runner、测试使用 .NET 10。框架没有 Example 引用，也没有反射插件发现或通用 ECS。

## 验证

**试玩 GridGame**。安装并授权 Unity 6000.3.23f1，在仓库根目录准备公共包、DLL 和场景数据，再打开 GridGame 工程。

```sh
python scripts/unity_grid.py prepare
unity open examples/unity/GridGame
```

在 Project 窗口双击 [Assets/Scenes/GridGame.unity](examples/unity/GridGame/Assets/Scenes/GridGame.unity)，点击工具栏 **Play**，在 **Game** 视图点击左下角的薄荷色棋子，再点右边一格移动，点珊瑚色棋子攻击。`End turn` 切换行动方，`New match` 重开。

使用 `python scripts/unity_grid.py verify --output TestResults/grid-play` 执行 PlayMode 和 CLI 对照。脚本使用临时工程副本，分别验证开启和关闭 Domain Reload 的情况，输出目录必须是新的。[验收方式与证据](docs/unity-play-host.md)

```sh
python scripts/build_native.py
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore --logger trx --results-directory TestResults/unit
dotnet format --verify-no-changes --no-restore
python scripts/verify_v2.py --output TestResults/v2
```

验收目录必须不存在。脚本保存输入、快照、回放、命令、stdout/stderr、环境清单及独立进程的原生损坏状态检查。CI 分别在 Windows/Linux 构建原生库并运行同一套验证，平台间比较单独归档。[验收索引](docs/v2-acceptance.md)区分实际执行结果与待验证范围。

旧材料不自动迁移：依据 examples/data 重写场景与操作文件，再通过 v2 Runner 重新生成快照/回放。v1 材料与阶段历史仍在 docs/contracts/phase-2 和阶段记录中；旧脚本仅报告入口已退役。
