# 第二阶段整体验收入口与证据索引

P2-T6 的实现、本地及远端验收记录见[阶段计划](phase-2-reproducibility-plan.md)。本入口不改变[协议 v1](contracts/phase-2/contract.md)，不承诺跨运行时或跨平台读取同一快照。生产 Core/GridGame/Headless 本轮没有修改；新增 Python 脚本仅用于验证，使用标准库，不增加生产依赖。

## 重复执行

需要仓库指定的 .NET SDK 和 Python 3.9 或更新版本。先构建，再运行测试和真实进程验收；以下路径均按仓库根目录执行：

```sh
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore --logger trx --results-directory TestResults/unit
dotnet format --verify-no-changes --no-restore
python3 scripts/verify_phase2.py --output TestResults/phase-2
```

脚本位于 [verify_phase2.py](../scripts/verify_phase2.py)。输出目录必须不存在，每次复核使用新目录；拒绝覆盖已有证据。默认调用 Debug 构建的宿主 DLL，可用 `--host <绝对或相对路径>` 指定其他已构建宿主。脚本从输出目录启动进程，不依赖当前工作目录能找到 demo.json。单次宿主调用超时为 30 秒。

成功返回 0，验收断言、进程或输入输出失败返回 1；无效脚本参数、缺少宿主或输出目录已存在返回 2。宿主自己的 `0/1/2/3` 退出码另存入报告，不能把预期的回放分歧退出码 3 当成脚本失败。

## 固定案例和保存材料

40 次真实 CLI 调用覆盖：原演示；固定输入重复 10 次；快照 inspect；完整回放；原六步演示从 0–6 步每个空队列边界保存、删除原场景、恢复并逐点比较，再回放剩余操作；连续入队后逐条推进的拒绝案例；命令分歧、事件分歧和损坏记录。

- 原场景与独立期望来自 [T1 固定样例](contracts/phase-2/examples/README.md)。脚本对照固定 run/divergence 响应，忽略只供人阅读的 message 和本轮新增的发布路径，不使用执行器重建固定期望。
- [拒绝场景](../tests/fixtures/phase-2/rejections.scenario.json)在 (1,0) 放置障碍；[命令文件](../tests/fixtures/phase-2/rejections.commands.json)先空推进，再连续提交六条命令，随后推进并再次空推进。期望三个拒绝依次为 Blocked、NoActionPoints、NotYourTurn，另三个成功；拒绝无事件、不改变玩法或随机摘要，空推进不增加计数。
- `summary.json` 保存基准提交、工作区状态、Python/SDK/宿主环境、构建 hash、每次调用参数与实际/期望退出码。它记录本次使用的二进制身份；仅运行脚本不会重新构建，必须遵循上面的先构建顺序。
- 每次调用保存 `<case>.stdout.txt` 与 `<case>.stderr.txt`，包括失败/超时输出；JSON 结果中包含实际检查点与字段差异。保存生成的命令、快照和回放，便于用同一构建复查。已删除的临时原场景可从固定样例恢复，恢复所需配置本身也在快照中。
- 输出到被 Git 忽略的 `TestResults/`，不提交当前机器绑定的回放或二进制。脚本输出失败时查看 summary 的 error、最后一次调用及对应 stdout/stderr，不用成功的旧报告替代失败报告。

## V1–V12 证据映射

下列 C# 测试均由完整 `dotnet test` 执行；脚本补充真实进程及可保留材料，不把进程内测试冒充跨进程证据。

| 验收项 | 固定检查入口与断言 | 可查看的证据 |
| --- | --- | --- |
| V1 全部保存边界 | [SnapshotFileTests](../tests/AiNative.Tests/SnapshotFileTests.cs) 的 SaveLoad_EveryDemoBoundary；脚本直接对照 continuous 的对应边界、前缀及后续每个检查点 | TRX；continuous、boundary-0–6 的 save/resume/replay 输出和快照 |
| V2 随机连续性 | [SessionStateTests](../tests/AiNative.Tests/SessionStateTests.cs) 的 Restore_AfterRandomConsumption：0/UInt64.MaxValue 种子，消费三次后恢复、继续五次并验证实例隔离；[CoreTests](../tests/AiNative.Tests/CoreTests.cs) 固定向量 | TRX。GridGame 固定伤害不消费随机，不把 CLI 的 seed 保持当成随机消费证据 |
| V3 可保存边界/拒绝 | SnapshotFileTests 全部边界及 Save_NonemptyOrFaultedSession；[CliTests](../tests/AiNative.Tests/CliTests.cs) 的队列/故障不发布断言 | TRX；boundary-0/2/6 对应新局/AP耗尽/已结束 |
| V4 非法恢复不污染旧实例 | [GridGameStateTests](../tests/AiNative.Tests/GridGameStateTests.cs) 的 Restore_InvalidState；[SnapshotCodecTests](../tests/AiNative.Tests/SnapshotCodecTests.cs) 的 Decode_InvalidIdentityOrState | TRX：状态、随机数、下一提交序号保持 |
| V5 版本/身份/环境 | SnapshotCodecTests、[ReplayBoundaryTests](../tests/AiNative.Tests/ReplayBoundaryTests.cs) 的 CompatibilityAndContentErrors、ExpectedConfiguration | TRX：准确 InputError 代码，无初始执行检查点 |
| V6 规范化与覆盖范围 | [CanonicalStateTests](../tests/AiNative.Tests/CanonicalStateTests.cs) 固定字节/hash、排列/文化无关；SnapshotCodecTests 重排；[ReplayTests](../tests/AiNative.Tests/ReplayTests.cs) 的有效状态/随机变体 | TRX；[固定向量](contracts/phase-2/examples/hash-vectors.json) |
| V7 队列、空推进和业务拒绝 | SessionStateTests 调度计数；CliTests 固定结果；脚本六命令入队及三种拒绝 | rejections/rejections-replay 输出、固定输入与录制文件 |
| V8 只读与隔离 | GridGameStateTests 的 State_CopiesInputCollectionsAndExposesOnlyReadOnlyCollections；[GameplayTests](../tests/AiNative.Tests/GameplayTests.cs) 历史视图；SessionStateTests 导出/恢复 | TRX；不通过宿主修改权威状态 |
| V9 重复与进程连续性 | CliTests 重复10次；[CliProcessTests](../tests/AiNative.Tests/CliProcessTests.cs) 删除原输入后新进程恢复/回放；脚本每个保存边界及10次固定运行 | fixed/repeat-1–9、boundary-* 输出及录制文件 |
| V10 首个分歧/损坏区分 | ReplayTests 固定差异响应、命令/状态/事件变化及损坏变体；脚本分歧定位为 operationIndex=5、sequence=2、已匹配6点 | event-divergence、command-divergence、damaged 输出及输入回放 |
| V11 严格输入与宿主故障 | SnapshotCodecTests、SnapshotFileTests、[CommandFileTests](../tests/AiNative.Tests/CommandFileTests.cs)、CliTests、ReplayBoundaryTests | TRX：数值边界、字段/编码、部分发布、竞争目标、stdout/stderr 故障 |
| V12 原行为及持续验证 | 完整测试、只读 format、原演示；[CI 工作流](../.github/workflows/build.yml) 调用同一脚本并保留 TRX/产物 | 本地 TRX 与 legacy 输出；已通过的基线及 P2-T6 CI、制品核验见下节 |

## CI 证据边界

P2-T1–T5 的提交 `c87e5a82f12b873f950a597250345c60128a10ea` 已在 GitHub Ubuntu runner 运行原有完整检查成功：[PR #3 workflow run](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40880920093)。该结果包含 332 项测试、构建、格式和原演示，不包含本轮新增的独立验收脚本与上传步骤。

P2-T6 提交 `17cdf7b9faf651b6758f31caf65927b6e995a218` 已通过 [PR #4 CI](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991275807) 和 [push CI](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991239106)。PR 作业检出临时合并提交 `2f8c4b1b5745c452946f651e5844bab2c086138a`，环境为 Linux/x64、SDK 10.0.204、runtime 10.0.14；332 项测试全部通过，40 次 CLI 验收全部通过。

`Preserve validation evidence` 步骤成功。已下载 [phase-2-evidence-40991275807-1](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991275807/artifacts/12949133349)，核验 117 个文件中的 TRX、summary、40 次 stdout/stderr、快照与回放以及分歧材料。持久化的[核验元数据](evidence/phase-2/ci-40991275807.json)记录提交、构建身份、制品 ID/摘要和结果；其中 digest 是 GitHub 报告的归档摘要，文件内容已另行解析检查。

制品保留 7 天，预计在北京时间 2026-11-06 10:23:20 过期；过期后可按本页命令重新生成材料，不能假定原下载链接永久有效。第二阶段 V1–V12 已取得相应本地及远端证据，阶段验收完成；PR 合并仍是独立操作。Linux CI 与 macOS 本地均在各自相同环境内保存恢复，没有进行 Unity、跨运行时读取同一记录或跨平台快照互通验收。
