# 通用框架 v2 验收

UnityCore Assets 的独立资源包、异步关闭扩展与 Windows 资源验收记录见 [Assets 记录](unitycore-assets.md)。它不改变下述模拟内核和 v2 协议历史基线。

2026-11-13 UnityCore UI 基础生命周期修复与 Windows Editor 双 Domain Reload 验证见 [UI 记录](unitycore-ui.md#基础生命周期收敛2026-11-13)：两种设置各 20 项 PlayMode、19 项 EditMode，共 78 次执行通过。仅覆盖基础窗口关闭、重启、取消及强退解绑，不代表导航、缓存、Bundle、真实输入或 Player 已完成，也不改写下述历史基线。

同日后续 [UI Task 3 验收](unitycore-ui.md#task-3-页面栈模态与事务回滚2026-11-13)完成无动画页面栈、模态、事务回滚及基础输入/焦点；双 Domain Reload 各 43 项 PlayMode、19 项 EditMode，共 124 次执行通过并经独立复核。缓存、动画、Clear、Bundle、真实输入后端及 Player 仍未验收，前一轮 78 次记录保留为生命周期历史基线。

同日后续 [UI Task 4 验收](unitycore-ui.md#task-4-有界缓存lrutrim-与旧会话隔离2026-11-13)完成有界缓存、LRU、Trim 与旧会话句柄隔离，经独立代码审查后双 Domain Reload 各通过 60 项 PlayMode、19 项 EditMode，共 158 次执行，0 失败、0 跳过；包含新增 17 项缓存测试和原导航/生命周期回归。前两轮记录保留为历史基线；Clear、动画、安全区、完整迟到/外部销毁、Bundle、真实输入和 Player 仍未完成。

同日后续 [UI Task 5 验收](unitycore-ui.md#task-5-输入焦点安全区和内置过渡2026-11-13)完成输入路由、焦点、安全区及不受 timeScale 影响的内置过渡，经独立代码审查后双 Domain Reload 各通过 81 项 PlayMode、25 项 EditMode，共 212 次执行，0 失败、0 跳过。新增 21 项 PlayMode 与 6 项 EditMode；输入证据为 RaycastAll/ExecuteEvents，设备安全区、真实鼠标键盘、交互 Demo、Bundle、Player 与 CI 仍不在已验范围。前述记录保留为各阶段历史基线。

同日后续 [UI Task 6 验收](unitycore-ui.md#task-6-clear迟到任务外部销毁与诊断2026-11-13)完成 Clear、迟到任务隔离、外部销毁恢复与不可变诊断，经独立代码审查后双 Domain Reload 各通过 104 项 PlayMode、25 项 EditMode，共 258 次 UI 执行；另有 Core PlayMode 16 项、EditMode 24 项及 Assets 取消 12 项，共计 310 次执行，0 失败、0 跳过。迟到加载采用受控后端配合真实 Core/Assets/UI，组件诊断只记录资源身份、不推断最终渲染材质或批次。Task 7–8、真实 Bundle UI 集成、交互 Demo、设备、Player 与 CI 仍未验收。

同日后续 [UI Task 7 验收](unitycore-ui.md#task-7-独立-uidemo-与真实-bundle-集成2026-11-13)完成独立 UIDemo、真实 SBP Bundle 构建部署及 11 项集成测试；双 Domain Reload 各 UI PlayMode 104 项、EditMode 25 项、Demo 11 项，共 280 次执行通过，0 失败、0 跳过。可见 Windows Editor 另用操作系统注入的原生鼠标/键盘事件验证导航、模态返回、Clear 与 Single 切场景，并保存稳定页、双模态及双页过渡的 Game View screen 截图。Task 8、性能、设备、Player/IL2CPP 与远端 CI 仍未验收。

本记录只声明实际执行的检查。当前实现、协议与复现边界见[v2契约](framework-v2.md)。旧355测试/45次CLI的逐项行为映射见[迁移索引](v2-migration-index.md)。

2026-11-09 的宿主故障分类、编码失败终止、恢复 runtime 核对、退出日志和 Int32 负零修复，独立审查及本机271项 .NET、108次CLI和两个Unity宿主验收见[修复记录](review-fixes-20261109.md)。历史基线仍按下文保留。

后续 joltc 与多线程迁移的版本、契约和新增验收单列在[joltc迁移记录](joltc-migration.md)；本页222项测试/80次CLI记录保留为迁移前v2基线。

后续 C#/原生分层迁移的契约和实际检查见[物理分层迁移](managed-physics-migration.md)。

2026-11-09 的 UnityCore 有边界单例例外、生命周期与调度修复及 Windows Editor 验收单列在[UnityCore 记录](unitycore-lifecycle.md)。两种 Domain Reload 各通过 23 项 PlayMode、22 项 EditMode，并重新完成十组 PhysicsSandbox CLI 检查点比较；不改写下述历史基线，也不扩展模拟内核与 v2 文件协议的边界。

## 第三阶段 Windows Editor 验收

| 示例 | 本地验收范围 | 可复核证据 |
| --- | --- | --- |
| GridGame | 快慢表现与两种 Domain Reload 设置共四组，每组匹配 16 个检查点 | [Play 宿主记录](unity-play-host.md) · [机器摘要](evidence/phase-3/grid-play/summary.json) |
| PhysicsSandbox | 两种 Domain Reload 设置各通过 11 项 PlayMode 测试和 1 项退出 Play 测试。每种设置比较均匀推进、补步、暂停输入以及 Tick 90、91 的读档续跑，共十组匹配 | [实施与验收](phase-3-physics-sandbox-unity-plan.md) · [机器摘要](evidence/phase-3/physics-play/summary.json) · [原始制品](evidence/phase-3/physics-play/physics-play-evidence.zip) |

PhysicsSandbox 同轮本地无头回归通过 249 项 .NET 测试、108 次 CLI 进程验收及原生损坏探针。两份 Editor 记录来自本机 Windows x64。远端工作流运行无头检查，Player、IL2CPP 与人工试玩仍待单独验证。旧 GridGame 窗口的验证范围见[历史验收](phase-3-grid-editor.md)。

## 迁移前 v2 基线的验收入口与范围

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

## 迁移前 v2 基线的运行记录

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
