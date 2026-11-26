# AI Native 游戏框架

可复用的纯 C# 玩法内核与工具链。GridGame（手动回合制）和 PhysicsSandbox（60 Hz 真实 Jolt 刚体）作为独立 Example，共用输入、快照、回放、比较和 CLI。

GridGame 和 PhysicsSandbox 各有一个独立 Unity 工程，均已在 Windows x64 Editor 与 CLI 逐 Tick 对照。PhysicsSandbox 的十组比较覆盖均匀推进、补步、暂停输入和两处读档续跑。两个游戏的 HUD 和表现资源都通过 UnityCore UI 与 Assets 装配，已在 Windows IL2CPP Player 运行验证。[示例迁移与验收](docs/v2-acceptance.md) · [GridGame 宿主](docs/unity-play-host.md) · [PhysicsSandbox 宿主](docs/phase-3-physics-sandbox-unity-plan.md) · [架构设计](docs/ai-native-game-engine-design.md) · [v2 契约](docs/framework-v2.md)

物理后端使用固定版本的 **joltc + 项目扩展**，支持 0–16 个工作线程、完整状态恢复和同线程配置的确定性回放。PhysicsSandbox 场景使用游戏版本 2，必填 `configuration.workerThreads`（示例取 2）。ABI 4 迁移时，C# 接管了实体映射、快照元数据与恢复编排，原生层负责资源清理、事件收集和内部状态。旧 ABI 的物理记录需要重新生成。该迁移提交已有独立的 Windows/Linux 无头 CI 记录。[物理分层迁移与 CI 制品](docs/managed-physics-migration.md) · [joltc 迁移历史](docs/joltc-migration.md)

第三方依赖统一存放在 [`third_party/`](third_party/README.md)：Jolt/joltc源码由原生构建脚本获取，NuGet包由`dotnet restore --locked-mode`恢复到`third_party/nuget/`。

能力状态、分模块历史验收、未覆盖范围及复现入口统一见 [当前验收总索引](docs/acceptance-index.md)。索引不代表当前 HEAD 已重新通过全部验证。

联机扩展已开始 S1 基础实施：[服务器端架构设计](docs/server-architecture.md)规定 Gateway、AuthSession、LobbyCoordinator、GameWorker 四类独立进程；首期采用六进程验收拓扑、Actor 状态隔离、每房间独占线程及双人 GridGame 跨网关重连。[实施记录](docs/server-implementation.md)列出已完成的 Actor／分帧基础及测试；角色服务与联机对局尚未实现，未进行服务器运行验收。

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
| packages/com.ainative.unitycore | Unity 应用模块生命周期、日志与表现回调，PhysicsSandbox 直接引用仓库中的源码及测试 |
| packages/com.ainative.unitycore.assets | 本地资源句柄、作用域、共享 AssetBundle、实例池、场景及 Editor 分包/校验工具 |
| packages/com.ainative.unitycore.ui | uGUI 窗口所有权、页面/模态导航、缓存、输入焦点与过渡；[包入口](packages/com.ainative.unitycore.ui/README.md) |
| packages/com.ainative.unitycore.network | Unity 应用 HTTP 请求、TCP 有序二进制消息、显式重连与关闭清理。[接口与验收入口](packages/com.ainative.unitycore.network/README.md) |
| examples/unity/UIDemo | 独立 UI 交互示例、真实 Bundle 集成与双 Domain Reload 验收 |
| examples/unity/AssetsDemo | 独立资源示例及 Windows Player 冒烟入口，不依赖 Jolt |
| examples/unity/GridGame | 独立 Unity 工程，含 Play 场景、Sprite 棋盘、UGUI HUD 和 PlayMode 验收 |
| examples/unity/PhysicsSandbox | 独立 Windows Editor 工程，提供 Jolt 沙盒、实时输入、存读档和逐 Tick 对照 |
| native/jolt | 原生资源聚合、BodyID 批量接口、内部状态验证及必要 joltc 扩展 |
| third_party | 下载的 Jolt/joltc 源码与 NuGet 包；版本锁定，下载内容不提交 Git |

Core 和可移植库保持 netstandard2.1 / C# 9；Headless、Runner、测试使用 .NET 10。框架没有 Example 引用，也没有反射插件发现或通用 ECS。

UnityCore 声明 [UniTask 2.5.11](https://github.com/Cysharp/UniTask/releases/tag/2.5.11) 依赖，并引用 `UniTask` 程序集，供 Unity PlayerLoop 异步等待使用。使用 UnityCore 的项目需在 `Packages/manifest.json` 配置 OpenUPM 注册表 `https://package.openupm.com`，作用域为 `com.cysharp.unitask`；[PhysicsSandbox 配置](examples/unity/PhysicsSandbox/Packages/manifest.json)已包含此项。两个示例通过 manifest 中相对 `file:` 路径直接引用仓库 `packages/` 源码，修改包内 C# 后由 Unity 自动刷新编译，无需 prepare 同步。`prepare` 仅更新纯 C# DLL、原生库和场景数据；修改这些构建输入后仍需执行。工程没有开发者机器的包绝对路径。该依赖仅用于 Unity 侧，纯 C# 模拟内核保持独立；新增依赖不改变 v2 模拟与文件协议。[UnityCore 接入、接口迁移与验收](docs/unitycore-lifecycle.md)

PhysicsSandbox 安装了 Unity CLI 的 `com.unity.pipeline` 插件，固定版本为 `0.10.0-exp.1`（实验版）。打开项目后，可在仓库根目录运行 `unity status --project-path examples/unity/PhysicsSandbox --format json` 检查连接，运行 `unity command editor status --project-path examples/unity/PhysicsSandbox --format json` 读取编辑器状态。CLI 命令显式指定项目路径，以免操作其他已打开的 Unity 工程。

从旧版升级时，先备份并移走示例工程 `Packages/com.ainative.unity`、`Packages/com.ainative.unitycore` 中实际存在的旧内嵌副本（检查其中是否有未合并的手动修改）。这些忽略目录不会随 Git 更新消失，而且会遮蔽 manifest 的本地引用。今后统一编辑仓库根目录 `packages/`；开启 Unity Preferences 中的 Auto Refresh。验收副本由脚本复制为独立目录结构，开发工程不再复制公共包。

## 验证

**UnityCore UI**。在新证据目录运行 `python scripts/verify_unity_ui.py TestResults/ui-editor-<唯一后缀>`，自动复制独立 UIDemo 与其本地源码包，重新构建 Bundle 并执行双 Domain Reload 精确 UI/Demo 矩阵。可见 screen 截图独立验证，自动矩阵 Passed 不替代视觉验收。[启动说明](examples/unity/UIDemo/README.md) · [契约与验收](docs/unitycore-ui.md)

Windows x64 Player 另运行 `python scripts/verify_unity_ui_players.py TestResults/ui-players-<唯一后缀>`，分别构建与启动 Mono、IL2CPP Release Player，校验实际后端、Bundle、交互路由、资源清理与屏幕截图。需要 Windows IL2CPP/Visual C++ 工具链及 Pillow；完整范围见 [Player 入口](examples/unity/UIDemo/README.md#windows-player-验收入口)。

原生鼠标键盘与系统窗口关闭使用 [交互验收入口](examples/unity/UIDemo/README.md#原生输入与窗口关闭验收入口)：`verify_unity_ui_native.py prepare` 构建隔离 Player，`run --backend Mono|Il2Cpp` 等待外部操作并验证只读输入/退出记录。已验证用户鼠标、CUA键盘及Alt+F4；关闭按钮与抓图工具限制见[实际记录](docs/unitycore-ui.md#windows-player-原生输入与系统窗口关闭2026-11-16)。

加载结果待交付、退出动画中的窗口关闭另用 `verify_unity_ui_shutdown.py <构建证据目录> --backend Mono|Il2Cpp --scenario loading|exiting`。退出场景等待操作人点击Back后再点标题栏×；受控加载边界、修复和证据见[加载/动画关闭验收](docs/unitycore-ui.md#加载与退出动画中关闭2026-11-16)。

追加 `--screenshots` 可记录真实原生输入后的Player客户端画面。两后端的客户端截图与用户实际点击标题栏×退出已在[后续专项](docs/unitycore-ui.md#原生交互截图与标题栏关闭2026-11-16)完成；截图不含OS标题栏，Windows抓图/自动按钮点击工具故障仍单独保留。

**试玩 GridGame**。安装并授权 Unity 6000.3.23f1，在仓库根目录准备公共包、DLL 和场景数据，再打开 GridGame 工程。

```sh
python scripts/unity_grid.py prepare
unity run examples/unity/GridGame -- -executeMethod AiNative.Examples.GridGame.Play.Editor.GridUiBuilder.BuildContent
unity open examples/unity/GridGame
```

在 Project 窗口双击 [Assets/Scenes/GridGame.unity](examples/unity/GridGame/Assets/Scenes/GridGame.unity)，点击工具栏 **Play**，在 **Game** 视图点击左下角的薄荷色棋子，再点右边一格移动，点珊瑚色棋子攻击。`End turn` 切换行动方，`New match` 重开。

使用 `python scripts/unity_grid.py verify --output TestResults/grid-play` 执行 PlayMode 和 CLI 对照。脚本使用临时工程副本，分别验证开启和关闭 Domain Reload 的情况，输出目录必须是新的。[验收方式与证据](docs/unity-play-host.md)

**试玩 PhysicsSandbox**。在 Windows x64 上准备已锁定的 Jolt DLL 和沙盒工程，再打开场景按 Play。

```sh
python scripts/build_native.py
python scripts/unity_physics.py prepare
unity run examples/unity/PhysicsSandbox -- -executeMethod AiNative.Examples.PhysicsPlay.Editor.SandboxContent.BuildContent
unity open examples/unity/PhysicsSandbox
```

打开 [PhysicsSandbox 场景](examples/unity/PhysicsSandbox/Assets/Scenes/PhysicsSandbox.unity)。用 **Spawn sphere / Spawn box** 生成刚体，在 **Body ID** 输入目标编号后施加冲量或删除。**Pause** 后的点击会排队，**Resume** 后在下一步处理。**Save / Load** 使用本机单个存档槽，读档后暂停在保存的 Tick。

运行 `python scripts/unity_physics.py verify --output TestResults/physics-play` 可在临时工程中复现两种 Domain Reload 设置的 PlayMode、退出 Play 和独立 CLI 验收。使用新的输出目录。[通过条件与原始证据](docs/phase-3-physics-sandbox-unity-plan.md)

**无头回归**按下列顺序运行。

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
