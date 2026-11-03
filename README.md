# AI Native 游戏框架

可复用的纯 C# 玩法内核与工具链。GridGame（手动回合制）和 PhysicsSandbox（60 Hz 真实 Jolt 刚体）作为独立 Example，共用输入、快照、回放、比较和 CLI。

当前运行入口统一为 **v2**。旧阶段文档保留历史证据，旧格式明确拒绝；Unity 编辑器、Player、IL2CPP 尚待第三阶段验收。[设计](docs/ai-native-game-engine-design.md) · [v2 契约](docs/framework-v2.md) · [验收索引](docs/v2-acceptance.md)。

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

物理示例先构建原生桥接。Windows x64 需要 MSVC C++ 工具（支持 VS 安装的 CMake/Ninja）；Linux x64 需要 C++17 编译器、CMake、Ninja、Git。Python 3.9+ 脚本会取得并检查固定 Jolt 源码提交，不安装全局软件：

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
| src/AiNative.Physics / Physics.Jolt | 可选物理接口与 netstandard2.1 DllImport 适配 |
| examples/AiNative.GridGame / GridGame.Hosting | 网格规则与专属协议 |
| examples/AiNative.PhysicsSandbox / PhysicsSandbox.Hosting | 实时物理规则与专属协议 |
| examples/AiNative.Examples.Runner | 唯一示例可执行入口，显式注册游戏与物理工厂 |
| native/jolt | 固定 Jolt 5.6.0 C ABI 桥接、恢复安全补丁与结构验证 |

Core 和可移植库保持 netstandard2.1 / C# 9；Headless、Runner、测试使用 .NET 10。框架没有 Example 引用，也没有反射插件发现或通用 ECS。

## 验证

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
