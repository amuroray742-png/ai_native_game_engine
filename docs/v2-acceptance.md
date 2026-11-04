# 通用框架 v2 验收

本记录只声明实际执行的检查。当前实现、协议与复现边界见[v2契约](framework-v2.md)。旧355测试/45次CLI的逐项行为映射见[迁移索引](v2-migration-index.md)。

## 验收入口与范围

| 范围 | 可执行入口/证据 |
| --- | --- |
| 模块边界 | HostingTests.FrameworkAssembliesDoNotDependOnExamplesOrUnity；独立Core/Hosting/Physics项目构建；grid-without-native进程 |
| 输入、Tick、随机与故障 | CoreTests；GridGame原GameplayTests/GridGameStateTests/ScenarioTests；fixed逐Tick独立状态向量 |
| 物理连续性 | PhysicsTests.CompleteStateRestoresFutureBodiesContactsAndAllocator；自由下落、碰撞、休眠、增删、冲量；physics-*跨进程续跑 |
| 生命周期与损坏状态 | PhysicsTests.WorldsAreIsolatedAndDisposalReturnsResourcesToBaseline/FailedCandidateAndInvalidNativeArgumentsLeaveOldWorldUsable；native-probe逐字节截断与变异 |
| 原生身份与保存边界 | wrong-native-version；ManyChangesInOneTickCannotReuseAnUndeliveredContactIdentity；Remove后保存拒绝 |
| 协议与表现 | HostingTests、CliTests、PresentationTests；输入/环境/摘要损坏、分歧定位、顺序/实时消费、旧回调失效 |
| 证据链 | scripts/verify_v2.py，80次真实CLI；CI Windows/Linux分别构建原生库、运行测试与脚本，另记录平台间compare |

本地复现命令见[README](../README.md#验证)。输出目录须不存在；process-index.json逐项保存实际命令/退出码/code，inputs、snapshot、replay、stdout/stderr、environment.json与native-probe结果同目录。TRX单列；原生构建材料含源码pin、编译器、flags、补丁hash、compile_commands与实际binary hash。

## 本轮记录

2026-11-03 Windows x64 初次完整验证：213项单元测试、80次CLI、1581个截断位置和1582个单字节变异通过；TestResults/v2-windows-final、TestResults/v2-unit。随后按独立审查修复装配/诊断和补充迁移回归；2026-11-04最终本地单元222项通过（TestResults/v2-unit-final），80次CLI与原生probe重跑通过（TestResults/v2-windows-reviewed）。独立Reviewer已复核这些结果，未发现剩余代码阻断项。

最终代码提交 `f9fba2c68c8daed949a1b53f06d229d1b9a181bb` 的[双平台 CI 41689668666](https://github.com/rayss1/ai_native_game_engine/actions/runs/41689668666)全部通过。两个平台均从固定源码构建真实原生库，执行锁定依赖恢复、构建、单元测试、只读格式检查与真实进程验收；并已下载上传制品核验，不仅检查作业状态。

| 环境 | 单元测试 | CLI进程 | 原生独立进程探针 | 制品ID |
| --- | --- | --- | --- | --- |
| Windows x64 / .NET 10.0.14 | 222通过，无失败/跳过 | 80通过 | 1581截断 + 1582单字节变异，worlds=0 | 13257786363 |
| Linux x64 / .NET 10.0.14 | 222通过，无失败/跳过 | 80通过 | 1581截断 + 1582单字节变异，worlds=0 | 13256532273 |

下载副本：`TestResults/v2-ci-41689668666/`，核验摘要 `verified-summary.json`。逐项核对TRX、80条命令实际/预期退出码、stdout code、原生探针及native-build.json中的binaryHash与实际文件一致。

独立平台比较制品 `13255588185` 中 GridGame 与 PhysicsSandbox 均为 `HostTracesMatched`。这是该次Windows/Linux构建的同起点同输入证据比较结果；严格恢复门禁仍要求同构建同环境，不扩大兼容承诺。

远端制品保留策略见workflow（平台制品7天），过期后按README与固定提交重新生成。Unity编辑器/Player/IL2CPP、资源/UI/音频框架未纳入本轮。

## 独立审查与修复

独立Reviewer进行了Core、物理、共享Hosting、Headless、表现与脚本审查，真实进程复现并复核下列修复：

1. 截断物理状态恢复期间持锁异常导致候选清理死锁：结构预读+固定Jolt恢复RAII补丁。
2. 同Tick 256次BodyID generation绕回导致碰撞结束归错实体：未消费ID避让。
3. Remove后、下一次Step前错误允许保存旧contact cache：明确拒绝保存。
4. 离线诊断body集合/motion与blob对象元数据不一致：共享物理校验比对，并有回归。
5. 原生版本错误诊断及实际加载位置：稳定HostFailure/NativeLibraryIncompatible，延迟绝对路径resolver确保身份一致。
6. 执行故障保留可信前缀、当前操作索引，输出失败保持已发布制品。

独立Reviewer已复核最终本地222项测试、80次CLI与原生探针，确认所有代码阻断项收口。随后双平台CI与制品核验完成，见上表；本轮六项改造的实现和验收均已收口。
