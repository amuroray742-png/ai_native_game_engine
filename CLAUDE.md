# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

仓库约定以 [AGENTS.md](AGENTS.md) 为执行入口；详细规则在 [设计文档](docs/ai-native-game-engine-design.md)、[架构与代码规范](docs/engineering-guidelines.md)、[v2 契约](docs/framework-v2.md) 与 [v2 验收](docs/v2-acceptance.md)。修改前先读这些文档以及任务涉及模块的专属文档。文档与提交说明使用中文，代码标识符使用英文。

## 常用命令

需要 `global.json` 指定的 .NET 10 SDK（10.0.202）。NuGet 包恢复到 `third_party/nuget/`，必须使用锁定模式。`TreatWarningsAsErrors` 已全局开启。

```sh
python scripts/build_native.py          # 构建固定版本的 Jolt/joltc 原生桥接到 artifacts/native/<rid>/（物理相关构建和测试需要）
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore --logger trx --results-directory TestResults/unit
dotnet format --verify-no-changes --no-restore
python scripts/verify_v2.py --output TestResults/v2   # 真实进程验收；输出目录必须不存在
```

以上为 CI（`.github/workflows/build.yml`，Windows 与 Linux）的完整顺序，CI 之后另用 `scripts/compare_platforms.py` 归档两个平台的比较结果。测试使用 xUnit，全部位于 `tests/AiNative.Tests`。运行单个测试：

```sh
dotnet test tests/AiNative.Tests --filter "FullyQualifiedName~HostingTests.SomeTestName"
```

无头 Runner（GridGame 不需要原生库）：

```sh
dotnet run --project examples/AiNative.Examples.Runner -- games
dotnet run --project examples/AiNative.Examples.Runner -- run --scenario examples/data/grid.scenario.json --seed 42 --commands examples/data/grid.commands.json --snapshot-out grid.snapshot.json --replay-out grid.replay.json
dotnet run --project examples/AiNative.Examples.Runner -- replay --input grid.replay.json
```

输出目标必须不存在，且父目录必须存在。CLI 在 stdout 输出一份 JSON；退出码 0 表示完成，1 表示宿主或模拟故障，2 表示输入错误，3 表示回放分歧。

Unity 需要 Unity 6000.3.23f1，目前只在 Windows Editor 上验收过：

```sh
python scripts/unity_grid.py prepare|verify --output TestResults/<新目录>
python scripts/unity_physics.py prepare|verify --output TestResults/<新目录>
```

`prepare` 把 `packages/` 下的公共包（PhysicsSandbox 还会带上 UnityCore 的源码与测试）、DLL 和场景数据内嵌到 `examples/unity/*` 工程。`verify` 在仓库外的临时副本中分别开启和关闭 Domain Reload，运行 PlayMode/EditMode 测试，并与 CLI 逐 Tick 对照。直接编辑 `packages/` 中的源码，不要编辑内嵌副本。Unity CLI 命令需显式指定 `--project-path examples/unity/<工程>`。

## 架构要点

- **分层与依赖方向**（单向，框架不引用 Example）：`AiNative.Core`（泛型 `SimulationRuntime<TCommand,TObservation,TState>`、Tick、SplitMix64 随机、故障）← `AiNative.Hosting`（`IGameModule` / `IHostedGame`、JSON 协议、快照、回放、比较、表现缓冲）← `AiNative.Headless`（文件、环境与 CLI 编排的可复用库）← `examples/AiNative.Examples.Runner`（显式注册游戏和物理工厂的唯一装配入口）。物理是可选依赖：`AiNative.Physics`（接口）与 `AiNative.Physics.Jolt`（DllImport、`BodyRegistry`、`PhysicsSnapshotCodec`、恢复编排）。
- **示例游戏**各分两层：纯规则项目（`examples/AiNative.GridGame`、`AiNative.PhysicsSandbox`）和协议适配项目（`*.Hosting`）。GridGame 手动推进（ticksPerSecond=0）；PhysicsSandbox 以 60 Hz 推进，场景必须提供 `configuration.workerThreads`，游戏版本为 2。
- **目标框架**：Core 与可移植库为 netstandard2.1 / C# 9，以便在 Unity 中使用；Headless、Runner 和测试为 net10.0。不能为使用新语法而升级可移植库。
- **时间模型**：`Submit` 只入队；`Advance` 依次消费全部待处理输入，然后推进一个 Tick（即使没有输入也推进）。Core 不访问墙上时间，由宿主负责采样与追赶。运行时禁止重入，包括在 Tick 回调中提交或推进。业务拒绝不能改变状态，也不能消耗随机数；意外异常会使实例进入故障状态。
- **物理所有权**：实体映射、BodyID generation、快照元数据和恢复编排都在 C# 层；原生层（`native/jolt/`）只负责资源、事件收集和 Jolt 内部状态。原生回调不进入 C#。恢复时先构建候选实例，校验成功后再替换。Jolt/joltc 的提交和 ABI 版本是固定的，升级时必须重新审查结构布局与补丁。
- **Unity 包**：`packages/com.ainative.unity`（`PlayHost` / `PlaySceneHost`）独占推进模拟时钟。`packages/com.ainative.unitycore` 是应用级服务（模块生命周期、日志、表现帧回调与 TimeMachine 定时器，依赖 UniTask），其中 `AiNative.UnityCore.Core` 是唯一允许的应用级单例，只在主线程上运行，不能用来推进模拟。生命周期契约见 [docs/unitycore-lifecycle.md](docs/unitycore-lifecycle.md)。
- **协议与证据**：v2 文件协议（场景、命令、快照、回放）对输入做严格校验；摘要采用规范 JSON 的 SHA-256。构建或环境身份变化后，旧材料在恢复时会被明确拒绝，不会被静默迁移，需要通过 Runner 重新生成。`docs/evidence/` 和 `docs/contracts/phase-2/` 是历史证据，不要修改。

## 工作约束（摘自 AGENTS.md 与规范）

- 纯 C# 内核不依赖 Unity、文件系统和网络。不使用反射扫描、Service Locator、DI 容器、自动注册或全局事件总线。新增抽象、依赖或跨模块接口时，要写明用途和必要性。
- 不要扩大防御范围：只在恢复、转换外部错误、隔离失败或保证资源释放的边界使用 `try/catch`。必需依赖在创建时就保证非空，不再逐层判空，也不用 `?.` 或 `!` 掩盖缺失的依赖。预期失败通过已有的结果类型返回。
- 修改保持聚焦，不做无关重构或整库格式化。改变边界或契约时同步更新文档和受影响的验证。文档类任务只检查内容、链接和格式，不运行引擎测试，也不安装依赖。
- 交付时只报告实际执行过的检查。不要把 Unity、Player/IL2CPP 或尚未运行的 CI 验收说成已经完成。
