# AI Native 游戏引擎

面向 AI Agent 的独立玩法内核与工具链：让游戏状态可观察、操作可控制、问题可复现、改动可验证。

**当前状态：第二阶段 P2-T1–T6 已完成实现、独立审查、本地及远端验收。** 已有纯 C# 内核、外部 JSON 场景、网格回合制规则、固定演示、完整状态导出恢复，以及严格快照、规范化摘要、环境检查和单文件发布。`run` / `inspect` 支持外部命令、结构化结果及同一支持环境下的跨进程恢复。`run --replay-out` 可录制回放，`replay` 逐检查点验证并报告首个字段分歧。P2-T6 新增可重复验收脚本与证据归档；远端 CI 与证据上传已验证，Unity 适配尚未实施，不宣称跨运行时确定性。

## 快速运行

需要 .NET SDK 10.0.202（允许同 feature band 的后续补丁）。在仓库根目录执行：

```sh
dotnet restore --locked-mode
dotnet run --project src/AiNative.Headless
```

默认从程序输出目录加载随构建复制的 [demo.json](src/AiNative.Headless/demo.json)，按固定六步命令完成一局，预期输出：

```text
Demo passed: Winner=A, Turn=3, A.Health=2, B.Health=0, AP=1
```

可以传入另一个场景文件，但它仍执行同一演示脚本与最终断言，不是通用对局播放器：

```sh
dotnet run --project src/AiNative.Headless -- src/AiNative.Headless/demo.json
```

退出码：`0` 为演示通过，`1` 为执行或验收失败，`2` 为参数或配置错误。只有相对的显式场景路径按当前工作目录解析，默认场景不依赖当前工作目录。

## 批处理 CLI

```sh
dotnet run --project src/AiNative.Headless -- run --scenario src/AiNative.Headless/demo.json --seed 42 --commands docs/contracts/phase-2/examples/commands.json --snapshot-out final.json --replay-out recorded.json
dotnet run --project src/AiNative.Headless -- replay --input recorded.json
dotnet run --project src/AiNative.Headless -- inspect --snapshot final.json
dotnet run --project src/AiNative.Headless -- run --snapshot final.json --commands docs/contracts/phase-2/examples/empty-commands.json
```

输出目标必须不存在，父目录必须存在；显式路径按当前工作目录解析。新子命令 stdout 输出一份 JSON，stderr 用于诊断；退出码 `0` 为完成，`2` 为输入错误，`1` 为模拟或宿主故障，`3` 为回放分歧。业务拒绝继续执行并出现在检查点中。恢复使用快照内嵌配置；`--expect-config` 可核对配置身份。快照与回放仅支持相同运行时、操作系统、架构与构建内容标识。

`--help` 查看帮助。回放匹配返回 `ReplayMatched`；损坏记录返回输入错误，合法期望与实际执行不符返回 `CheckpointMismatch`，包含首个操作位置、期望/实际摘要及字段差异。完整字段及错误语义见[协议 v1](docs/contracts/phase-2/contract.md)。

## 项目与验证

| 项目 | 职责 | 目标 |
| --- | --- | --- |
| AiNative.Core | FIFO 调度、计数、故障隔离、随机状态导出恢复 | .NET Standard 2.1 / C# 9 |
| AiNative.GridGame | 场景校验、玩法规则、只读视图及完整状态导出恢复 | .NET Standard 2.1 / C# 9 |
| AiNative.Headless | JSON 输入、快照、run/inspect/replay、装配与演示 | .NET 10 |
| AiNative.Tests | 核心、玩法、配置及进程行为测试 | .NET 10 / xUnit |

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes --no-restore
```

本地已通过 332 项测试及 40 次真实 CLI 验收。[P2-T6 CI](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991275807) 已通过，上传制品已下载核验，结果同为 332 项测试及 40 次 CLI 验收通过。

构建后可运行下列命令保留输入、快照、回放、stdout/stderr 和检查摘要。需要 Python 3.9+ 标准库；输出目录必须不存在，重复执行请选择新目录。

```sh
python3 scripts/verify_phase2.py --output TestResults/phase-2
```

具体范围、V1–V12 对应测试及产物说明见[第二阶段验收入口与证据索引](docs/phase-2-acceptance.md)。

## 文档

- [设计思路](docs/ai-native-game-engine-design.md)：架构、Agent 工作流与整体路线。
- [第一阶段实施计划与验收记录](docs/phase-1-headless-gameplay-plan.md)：任务、接口、配置格式、运行证据和独立审查。
- [第二阶段实施计划](docs/phase-2-reproducibility-plan.md)：快照、回放、结构化 CLI 契约、任务拆分与验收标准，以及 P2-T2–T6 的本地验收记录。
- [第二阶段公共 API 与协议 v1](docs/contracts/phase-2/contract.md)：P2-T1 已固化的签名、CLI 参数、字段表、样例及固定哈希向量。
- [架构与代码规范](docs/engineering-guidelines.md)：依赖边界、C# 风格、简洁结构及可读性要求。
- [Agent 仓库约定](AGENTS.md)：开发与审查入口。

渲染、物理与资源管线由未来的现有引擎适配提供；多人联网、体素和原生性能模块仅在需求或测量证明必要后再设计。
