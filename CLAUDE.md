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

以上为无头 CI（`.github/workflows/build.yml`，Windows 与 Linux）的完整顺序，CI 之后另用 `scripts/compare_platforms.py` 归档两个平台的比较结果；不包含 Unity 包测试。原生构建需要 Python 3.10+；Windows x64 需要 MSVC C++ 工具，Linux x64 需要 C++17 编译器、CMake、Ninja 和 Git。.NET 测试使用 xUnit，位于 `tests/AiNative.Tests`；Unity 测试另在公共包及各示例工程中，由 Unity Test Runner 执行。运行单个 .NET 测试（先完成锁定恢复；物理测试还需原生库）：

```sh
dotnet test tests/AiNative.Tests --no-restore --filter "FullyQualifiedName~HostingTests.TypedAndProtocolViewsShareQueueTickAndLifetime"
```

无头 Runner（GridGame 不需要原生库）：

```sh
dotnet run --project examples/AiNative.Examples.Runner -- games
dotnet run --project examples/AiNative.Examples.Runner -- run --scenario examples/data/grid.scenario.json --seed 42 --commands examples/data/grid.commands.json --snapshot-out grid.snapshot.json --replay-out grid.replay.json
dotnet run --project examples/AiNative.Examples.Runner -- replay --input grid.replay.json
```

输出目标必须不存在，且父目录必须存在。CLI 在 stdout 输出一份 JSON；退出码 0 表示完成，1 表示宿主或模拟故障，2 表示输入错误，3 表示回放分歧。

Unity 使用 Unity 6000.3.23f1 并需有效授权；GridGame / PhysicsSandbox 的模拟宿主验收范围是 Windows Editor，不能据此宣称 Player / IL2CPP 已通过：

```sh
python scripts/unity_grid.py prepare
python scripts/unity_grid.py verify --output TestResults/grid-play-new
python scripts/unity_physics.py prepare   # 先构建 Windows x64 原生桥接
python scripts/unity_physics.py verify --output TestResults/physics-play-new
```

`verify` 会先执行 `prepare`，输出目录必须不存在。开发工程通过 manifest 的相对 `file:` 路径直接引用仓库 `packages/`；包内 C# 修改由 Unity Auto Refresh 编译，不必重新 prepare。`prepare` 只更新纯 C# DLL、原生库（物理示例）及场景数据，这些输入变化后必须重跑。旧 `Packages/com.ainative.*` 内嵌副本会遮蔽源码引用，脚本会拒绝；先保留其中的手动修改再移走，不要编辑内嵌副本。`verify` 在隔离副本中运行两种 Domain Reload 的 PlayMode/EditMode 检查并与 CLI 逐 Tick 对照；验证副本位置记录在输出的 `workspace.txt`。

Unity CLI 命令显式指定 `--project-path examples/unity/<工程>`。UnityCore 使用 UniTask 2.5.11；相关工程通过 manifest 配置 OpenUPM 注册表（`https://package.openupm.com`，作用域 `com.cysharp.unitask`），不要改成开发者机器的绝对包路径。

### Assets 与 Shader 验证

独立的 `examples/unity/AssetsDemo` 不依赖 Jolt，使用 URP 17.3.0、SBP 2.6.2。下列命令在仓库根目录运行，证据目录均须不存在；脚本复制工程及公共包隔离执行，不修改开发工程。需要 PATH 上可用的 Unity CLI 和 Windows Editor；`--players` 还需要 Windows IL2CPP 模块及兼容的 Visual C++ 工具链。

```sh
# 默认 SBP / Forward，双 Domain Reload 的完整 EditMode / PlayMode，不构建 Player
python scripts/verify_unity_assets.py TestResults/assets-editor-new --baseline-fixture

# SBP 与 Builtin × Forward 与 Forward+，双重载；每组另验 Mono Development 与 IL2CPP Player
python scripts/verify_unity_assets.py TestResults/assets-matrix-new --backend both --renderers both --baseline-fixture --players

# Shader 自动收集专项，Editor 双重载，要求实际绘制测试通过且不跳过
python scripts/verify_shader_collection.py TestResults/shader-collection-new

# 两后端 × 保留/裁掉/恢复；严格匹配 Windows Mono / D3D11 / Forward Player
python scripts/verify_shader_variant_demo.py TestResults/variant-matrix-new --backend both
```

`--baseline-fixture` 从 Git HEAD 读取已提交的 ResourceGroups，仅替换隔离副本中的配置。Windows 隔离工程使用目标盘根的短路径，实际位置见输出 `workspace.txt`。Shader 专项需要真实图形绘制，不等于无图形验收，也不能扩展为 IL2CPP / Forward+ 的覆盖证明。脚本还检查完整进程日志中的 JobTempAlloc / TLS 临时分配警告，不能只看测试 XML。

AssetsDemo 的交互入口见 [示例 README](examples/unity/AssetsDemo/README.md)；Editor AssetDatabase 直读不需要构建，但不能替代 Bundle 卸载或 Player 验证，Player 始终使用 Bundle。资源验证的历史范围见 [Assets 记录](docs/unitycore-assets.md)，不与两个模拟宿主的验收混为一谈。

## 架构要点

- **分层与依赖方向**（单向，框架不引用 Example）：`AiNative.Core`（泛型 `SimulationRuntime<TCommand,TObservation,TState>`、Tick、SplitMix64 随机、故障）← `AiNative.Hosting`（`IGameModule` / `IHostedGame`、JSON 协议、快照、回放、比较、表现缓冲）← `AiNative.Headless`（文件、环境与 CLI 编排的可复用库）← `examples/AiNative.Examples.Runner`（显式注册游戏和物理工厂的唯一装配入口）。物理是可选依赖：`AiNative.Physics`（接口）与 `AiNative.Physics.Jolt`（DllImport、`BodyRegistry`、`PhysicsSnapshotCodec`、恢复编排）。
- **示例游戏**各分两层：纯规则项目（`examples/AiNative.GridGame`、`AiNative.PhysicsSandbox`）和协议适配项目（`*.Hosting`）。GridGame 手动推进（ticksPerSecond=0）；PhysicsSandbox 以 60 Hz 推进，场景必须提供 `configuration.workerThreads`，游戏版本为 2。
- **目标框架**：Core 与可移植库为 netstandard2.1 / C# 9，以便在 Unity 中使用；Headless、Runner 和测试为 net10.0。不能为使用新语法而升级可移植库。
- **时间模型**：`Submit` 只入队；`Advance` 依次消费全部待处理输入，然后推进一个 Tick（即使没有输入也推进）。Core 不访问墙上时间，由宿主负责采样与追赶。运行时禁止重入，包括在 Tick 回调中提交或推进。业务拒绝不能改变状态，也不能消耗随机数；意外异常会使实例进入故障状态。
- **物理所有权**：实体映射、BodyID generation、快照元数据和恢复编排都在 C# 层；原生层（`native/jolt/`）只负责资源、事件收集和 Jolt 内部状态。原生回调不进入 C#。恢复时先构建候选实例，校验成功后再替换。Jolt/joltc 的提交和 ABI 版本是固定的，升级时必须重新审查结构布局与补丁。
- **Unity 包**：`packages/com.ainative.unity`（`PlayHost` / `PlaySceneHost`）独占推进模拟时钟。`packages/com.ainative.unitycore` 是应用级服务（模块生命周期、日志、表现帧回调与 TimeMachine 定时器，依赖 UniTask），其中 `AiNative.UnityCore.Core` 是唯一允许的应用级单例，只在主线程上运行，不能用来推进模拟。生命周期契约见 [docs/unitycore-lifecycle.md](docs/unitycore-lifecycle.md)。
- **Assets 所有权**：`packages/com.ainative.unitycore.assets` 单向依赖 UnityCore，不依赖模拟内核、Hosting 或 Jolt，也不推进 Tick。启动前显式注册一个 `AssetsModule`，调用及取消都在主线程。消费者各持可释放 lease，共享底层请求；外部 Instantiate 必须自行保留 prefab lease。正常关闭等待在途请求与实际对象销毁，取消等待不等于中断 Unity 原生操作。真实 Bundle 最后持有者释放时 `Unload(true)`；AssetDatabase 模式不销毁源资产。禁止 Resources 回退、全局卸载、隐式常驻缓存与阻塞等待。见 [资源契约](docs/unitycore-assets.md) 与 [包 README](packages/com.ainative.unitycore.assets/README.md)。
- **资源构建与发布**：一次受管 Bundle 构建中，普通源 GUID 只能有一个物理归属；同文件子资源整体归属，显式归属优先，共享依赖按原始引用组集合提取，场景外部依赖进入普通包。公开地址与物理包名分离，移动或改名后更新调用并重建，不保留旧地址别名。默认固定 SBP 2.6.2，仅 Editor 使用其 API；Builtin 是另一个构建后端，不是 Built-in 渲染管线，失败时禁止静默切换。以本轮完整 WriteResults 与文件到 Bundle 映射验证实际归属，不能以计划或旧报告代替。输出目录必须全新，成功才发布及递增版本；部署只把 catalog / Bundle 放入 StreamingAssets，裁剪证据保留在外部快照。SBP 可用标准 BuildPipeline；Builtin Player 使用部署辅助入口或匹配 Manifest，保留 Bundle-only 类型证据。见 [SBP](docs/unitycore-assets-sbp.md) 与 [部署契约](docs/unitycore-assets-deployment.md)。
- **Shader 收集与剔除**：自动合并只合并材质扫描、Editor 实际绘制录制及人工 Required Variants，不枚举全部变体，也不自动启用剔除、预热或部署。生成集合必须加入资源组、替换旧引用并重建。`EnableProcessing` 只影响受管 Bundle 构建且关闭该轮缓存；Exclusions 执行剔除，与 Required 冲突直接失败。SVC 无 PassName，Editor 表现不能证明所有平台/质量分支覆盖，缺失结论需严格匹配 Player。见 [自动收集](docs/assets-shader-auto-collection.md) 与 [变体演示](docs/assets-shader-variant-demo.md)。
- **协议与证据**：v2 文件协议（场景、命令、快照、回放）对输入做严格校验；摘要采用规范 JSON 的 SHA-256。构建或环境身份变化后，旧材料在恢复时会被明确拒绝，不会被静默迁移，需要通过 Runner 重新生成。`docs/evidence/` 和 `docs/contracts/phase-2/` 是历史证据，不要修改。

## 工作约束（摘自 AGENTS.md 与规范）

- 纯 C# 内核不依赖 Unity、文件系统和网络。不使用反射扫描、Service Locator、DI 容器、自动注册或全局事件总线。新增抽象、依赖或跨模块接口时，要写明用途和必要性。
- 不要扩大防御范围：只在恢复、转换外部错误、隔离失败或保证资源释放的边界使用 `try/catch`。必需依赖在创建时就保证非空，不再逐层判空，也不用 `?.` 或 `!` 掩盖缺失的依赖。预期失败通过已有的结果类型返回。
- 修改保持聚焦，不做无关重构或整库格式化。改变边界或契约时同步更新文档和受影响的验证。文档类任务只检查内容、链接和格式，不运行引擎测试，也不安装依赖。
- 交付时只报告实际执行过的检查。不要把 Unity、Player/IL2CPP 或尚未运行的 CI 验收说成已经完成。
