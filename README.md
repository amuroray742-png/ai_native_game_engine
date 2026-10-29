# AI Native 游戏引擎

面向 AI Agent 的独立玩法内核与工具链：让游戏状态可观察、操作可控制、问题可复现、改动可验证。

**当前状态：第一阶段无头玩法闭环已验证。** 已实现纯 C# 内核、外部 JSON 场景、网格回合制规则和固定控制台演示。Unity 适配、通用 Agent CLI、快照与回放工具仍在规划中；不宣称已验证 Unity 或跨运行时确定性。

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

## 项目与验证

| 项目 | 职责 | 目标 |
| --- | --- | --- |
| AiNative.Core | FIFO 命令调度、故障状态、SplitMix64 随机源 | .NET Standard 2.1 / C# 9 |
| AiNative.GridGame | 场景校验、移动、攻击、回合、胜负与只读视图 | .NET Standard 2.1 / C# 9 |
| AiNative.Headless | JSON 输入、装配、演示和输出 | .NET 10 |
| AiNative.Tests | 核心、玩法、配置及进程行为测试 | .NET 10 / xUnit |

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes --no-restore
```

本地已通过 59 项测试、无警告构建、只读格式检查与控制台演示。基础 [GitHub Actions 工作流](.github/workflows/build.yml) 已配置相同检查，尚未在远端运行，不将配置存在视为 CI 已通过。

## 文档

- [设计思路](docs/ai-native-game-engine-design.md)：架构、Agent 工作流与整体路线。
- [第一阶段实施计划与验收记录](docs/phase-1-headless-gameplay-plan.md)：任务、接口、配置格式、运行证据和独立审查。
- [架构与代码规范](docs/engineering-guidelines.md)：依赖边界、C# 风格、简洁结构及可读性要求。
- [Agent 仓库约定](AGENTS.md)：开发与审查入口。

渲染、物理与资源管线由未来的现有引擎适配提供；多人联网、体素和原生性能模块仅在需求或测量证明必要后再设计。
