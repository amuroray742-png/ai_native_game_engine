# P2-T1：复现协议 v1 与公共 API

> 历史记录：本文保留当时的设计与验证证据；当前运行入口及契约已迁移到 [v2](../../framework-v2.md)，本文旧命令不作为当前验收入口。
状态：契约已固化；第 1 节 Core 与 GridGame 管理 API 已在 P2-T2 实现，P2-T3 已实现快照文件、摘要及环境校验，P2-T4 已实现 run/inspect CLI，P2-T5 已实现回放记录与验证。编写日期：2026-10-29。本文是[第二阶段计划](../../phase-2-reproducibility-plan.md)第 3–7 节的具体字段与接口依据；变更本契约须同步计划。第 1 节签名已落地；快照文件协议已落地；run/inspect 和命令文件协议已落地；回放协议已在 P2-T5 落地，P2-T6 已完成本地验收、远端 CI 及上传证据核验。

第三阶段前的公共对局入口、共享库与表现契约见[宿主边界调整](../../host-boundary-and-presentation.md)。文件形状和玩法摘要仍为 v1，构建身份清单按第 4 节更新；增加 compare 命令，不放宽恢复兼容性。

## 1. 公共 API 与状态所有权

以下记录第二阶段建立的底层管理 API，继续保持行为。当前宿主使用 GridGameRuntime 统一持有会话与规则；文件协议实现位于新增 GridGame.Hosting，共享库使用 C# 9 和同步调用，不为文件字段创建 Core 抽象。

### Core

`SimulationSession<TCommand, TView>` 新增：

```csharp
public long LastSubmittedSequence { get; }
public long CompletedStepCount { get; }
public SessionState ExportState();
public static SimulationSession<TCommand, TView> Restore(
    IGameRules<TCommand, TView> rules, SessionState state);
```

新增不可变 `SessionState`：

```csharp
public SessionState(long lastSubmittedSequence, long completedStepCount,
    string randomAlgorithm, ulong randomState);
public long LastSubmittedSequence { get; }
public long CompletedStepCount { get; }
public string RandomAlgorithm { get; }
public ulong RandomState { get; }
```

SessionState 仅表达健康空队列边界，不提供 PendingCount 参数、队列修改器或 IsFaulted 恢复字段。构造时校验计数非负且相等、算法严格等于 `splitmix64-v1`；任何 UInt64 随机 state（包括 0）均合法。Restore 创建新会话，接管新规则实例且不调用 Execute、不消费随机数，不替换任何现有实例。随机 state 直接成为算法内部状态，保留现有 DeterministicRandom API，不新增公共随机状态 setter。

ExportState 要求健康、无在途调用且队列为空；失败抛 InvalidOperationException，不推进或清空队列。Submit、Advance、GetState、ExportState 统一拒绝重入；规则调用期间的重入异常若传播至外层，外层按既有故障语义标记实例。仍不支持多线程调用。计数 getter 和现有 RandomState/PendingCount 是诊断值，故障后可读但不能用来恢复。

正常非空 Advance 在规则结果验证后增加 CompletedStepCount，拒绝也计数；空推进不增加。Submit 序号溢出抛 OverflowException，入队前失败，不故障化仍健康的会话；规则内溢出按执行故障处理。IGameRules、StepResult 和现有业务结果不改变形状。状态所有权仍为调用契约，不作为恶意规则沙箱。

### GridGame

`GridGameRules` 新增：

```csharp
public GridGameState ExportState();
public static GridGameRules Restore(GridGameState state);
```

新增不可变 `GridGameState`：

```csharp
public GridGameState(ScenarioDefinition configuration,
    IReadOnlyList<UnitDefinition> units, Side activeSide,
    int actionPoints, long turnIndex, Side? winner);
public ScenarioDefinition Configuration { get; }
public IReadOnlyList<UnitDefinition> Units { get; }
public Side ActiveSide { get; }
public int ActionPoints { get; }
public long TurnIndex { get; }
public Side? Winner { get; }
```

Configuration 是原始场景的深度隔离副本，含初始生命值与位置；Units 是当前单位状态，按 ID 排序、集合和元素不可变。不得把当前血量写回 Configuration。复用现有 UnitDefinition 不增加同形单位类型。GridGameState 构造器复制输入、拒绝 null，领域一致性由 Restore 完整验证；构造成功不等于可恢复。

Restore 先调用配置领域校验，再执行计划第 4.2 节的运行状态校验；不重置当前方、AP、回合或 winner。ExportState 只读且隔离；只允许宿主在会话调用之间使用其持有的规则引用导出，不绕过 Session 执行规则。

跨模块的 `turnIndex - 1 <= completedStepCount` 检查由 GridGameRuntime.Restore 与共享快照校验完成。空队列快照捕获顺序为 Session.ExportState → rules.ExportState → 编码，全程在宿主同一同步调用内，不插入 Submit/Advance/外部回调。恢复顺序为文件校验 → GridGameRules.Restore → Session.Restore → 重算摘要 → 返回候选实例；拥有旧实例的调用方仅在全部成功后替换引用。

公开 API 的 null 参数抛 ArgumentNullException；状态参数非法或不支持的随机算法抛 ArgumentException。这些只在宿主的输入/恢复边界映射为 InputError，不能在运行规则外围把任意 ArgumentException 当作输入错误。没有新增通用异常基类或 Result 框架。

### Headless

Headless 外部公共入口继续为 `Program.Run(string[] args, TextWriter output, TextWriter error)`；文件发布和 CLI 编排保持 internal。共享输入、规范化、快照及宿主证据接口已公开于 GridGame.Hosting，具体签名见[公共入口](../../host-boundary-and-presentation.md#2-对局所有权与公共入口)。

## 2. CLI 参数

下列参数区分大小写，不支持缩写、`--key=value`、重复选项或未知选项。每个带值选项紧随一个 token；路径即使以 `--` 开头也作为该值读取。无参数及单个非保留 token 保持旧演示语义；文件名恰为 run/inspect/replay 时使用 `./run` 等显式路径。

```text
AiNative.Headless run --scenario <path> --seed <uint64> --commands <path>
    [--snapshot-out <path>] [--replay-out <path>]
AiNative.Headless run --snapshot <path> --commands <path>
    [--expect-config <scenario-path>] [--snapshot-out <path>] [--replay-out <path>]
AiNative.Headless inspect --snapshot <path> [--expect-config <scenario-path>]
AiNative.Headless replay --input <path> [--expect-config <scenario-path>]
AiNative.Headless compare --expected <replay-path> --actual <replay-path>
AiNative.Headless --help
AiNative.Headless <run|inspect|replay|compare> --help
```

help 独占调用，stdout 为人类可读帮助，退出 0；不属于 JSON 结果协议。所有新子命令的其余输出遵循第 5 节。run 的场景与快照严格二选一；场景必须带 seed，快照禁止 seed；commands 必填，空操作文件合法。expect-config 只验证配置内容身份，不能替换快照内配置。inspect 和 replay 不接受 seed 或输出路径选项。

路径按当前工作目录解析；输出目标必须互异且不存在，不创建缺失的父目录。先检查参数、读取所有输入、严格校验和队列平衡，再创建/恢复实例；可提前发现的输出路径问题也在执行前拒绝为 HostFailure。发布仍须防止竞争覆盖，先快照、后回放，结果列明已发布路径。stdout 结果最后输出，写出失败不撤销已发布文件。

P2-T4 已支持 run/inspect、命令文件与快照输出；P2-T5 已支持 `replay` 和 `--replay-out`。以下命令使用当前构建生成回放，不直接加载虚构环境的协议夹具。

完整示例（输出文件必须不存在）：

```sh
dotnet run --project src/AiNative.Headless -- run --scenario src/AiNative.Headless/demo.json --seed 42 --commands docs/contracts/phase-2/examples/commands.json --snapshot-out final.json --replay-out recorded.json
dotnet run --project src/AiNative.Headless -- inspect --snapshot final.json
dotnet run --project src/AiNative.Headless -- replay --input recorded.json
```

## 3. 文件字段定义

本节每行列出的字段全部必填且仅允许这些字段；只有明确标记的联合分支可以省略别的分支字段。文件都是 UTF-8 JSON 对象，输出无 BOM；输入拒绝注释、尾逗号、重复/未知属性和非法 null。字段顺序只在规范化字节中强制，普通文件顺序不限。

类型约定：`i32` 是 JSON 整数且必须在 Int32 范围；`i64`、`u64` 是无前导零十进制字符串，分别不超过 9223372036854775807、18446744073709551615，除另述外至少 0。禁止数值 token 代替字符串。`hash` 是 64 位小写十六进制字符串。`Side` 为 A/B，`Direction` 为 Up/Down/Left/Right。索引、长度、数量为非负 i32；文件数组超过 Int32 可表示长度则拒绝。

| 对象 | 字段（名称: 类型） |
| --- | --- |
| Position | x: i32, y: i32 |
| Unit | id: i32, side: Side, position: Position, health: i32 |
| Configuration | formatVersion: i32=1, width: i32, height: i32, obstacles: Position[], units: Unit[], actionPointsPerTurn: i32, attackDamage: i32 |
| Session | lastSubmittedSequence: i64, completedStepCount: i64, pendingCount: i32 |
| Random | algorithm: string=`splitmix64-v1`, state: u64 |
| Game | units: Unit[], activeSide: Side, actionPoints: i32, turnIndex: i64≥1, winner: Side 或 null |
| Environment | runtimeVersion: string, os: string, architecture: string, buildHash: hash |
| Snapshot | snapshotFormatVersion: i32=1, rulesId: string=`grid-game`, rulesVersion: i32=1, configFormatVersion: i32=1, configuration: Configuration, configHash: hash, session: Session, random: Random, game: Game, digestVersion: i32=1, stateHash: hash, environment: Environment |
| DigestState | digestVersion: i32=1, rulesId: string=`grid-game`, rulesVersion: i32=1, configHash: hash, random: Random, game: Game |
| CommandFile | commandFormatVersion: i32=1, operations: Operation[] |
| Replay | replayFormatVersion: i32=1, initialSnapshot: Snapshot, commandFile: CommandFile, initialCheckpoint: Checkpoint, checkpoints: Checkpoint[] |

Configuration 服从现有场景领域约束（正尺寸、正伤害/AP、双方各一个有效初始单位等）。Snapshot.session.pendingCount 必须为 0。Replay 中唯一环境来源是 initialSnapshot.environment。DigestState 不含 session。Configuration 的外部场景读取保持既有大小写兼容；新快照内部枚举必须使用规范名称。

### 操作、命令与事件联合类型

| 分支 | 精确字段 |
| --- | --- |
| 提交操作 | `op: "submit"`, command: Command |
| 推进操作 | `op: "advance"` |
| 移动命令 | `type: "Move"`, side: Side, unitId: i32, direction: Direction |
| 攻击命令 | `type: "Attack"`, side: Side, unitId: i32, targetId: i32 |
| 换方命令 | `type: "EndTurn"`, side: Side |
| 移动事件 | `type: "UnitMoved"`, unitId: i32, from: Position, to: Position |
| 受伤事件 | `type: "UnitDamaged"`, unitId: i32, damage: i32≥0, health: i32≥0 |
| 换方事件 | `type: "TurnChanged"`, side: Side, turnIndex: i64≥1 |
| 结束事件 | `type: "GameEnded"`, winner: Side |

命令 ID 只要求 i32；负数或 0 也进入规则并可被 UnknownUnit/UnknownTarget 拒绝，不在协议层改变第一阶段判定优先级。Operation 和 Command 不带 sequence，由 Submit 分配。

### 检查点及结果

Checkpoint 精确字段：`operationIndex: i32 或 null`、`session: Session`、`state: DigestState`、`stateHash: hash`、`result: OperationResult`。只有初始检查点 operationIndex 为 null。其 state 在规范化排序后语义等于从 initialSnapshot 提取的 DigestState（不按输入数组排列比较），session 也必须相等；其结果为 initial。checkpoints 长度等于 operations，索引严格为 0..N-1。

| OperationResult 分支 | 精确字段 |
| --- | --- |
| 初始点 | `kind: "initial"` |
| 成功提交 | `kind: "submitted"`, sequence: i64≥1 |
| 空推进 | `kind: "empty"` |
| 业务成功 | `kind: "executed"`, sequence: i64≥1, `outcome: "Accepted"`, events: Event[] |
| 业务拒绝 | `kind: "executed"`, sequence: i64≥1, `outcome: "Rejected"`, errorCode: RuleError, events: 空数组 |

RuleError 穷举：GameFinished、NotYourTurn、UnknownUnit、NotYourUnit、InvalidDirection、OutOfBounds、Blocked、Occupied、NoActionPoints、UnknownTarget、FriendlyTarget、NotAdjacent、UnknownCommand。最后两种协议不可构造情形 InvalidDirection/UnknownCommand 仍保留为内核错误码，但回放实际执行不应产生它们。

结构预校验只推导提交/推进计数与队列长度，不执行规则：submit 增加待执行数，advance 在非空时减少，否则不变；结束必须为空。检查点序号、计数、result.kind 与这个推导相符。不得预先根据移动/攻击结果推断期望 Game 或 Event 正确性：结构有效但执行不符应归 ReplayDivergence。期望状态需满足玩法不变量、配置身份及自身 hash，submit/empty 的预期状态须与前一检查点相同。

## 4. 规范化与环境身份

沿用阶段计划第 5 节字段顺序。Configuration 中 units 按 id、obstacles 按 y 再 x；Game.units 按 id。所有数值输出最短十进制，负零归零，不使用指数；严格 i32 输入只允许无小数/指数的 JSON 整数 token。state 的 64 位字段均保留字符串。对象不输出任何可选空字段；winner 必须显式 null 或 Side。UTF-8 无 BOM、无空白、无结尾换行，标识字符串无需转义。SHA-256 输入是这些确切字节，输出小写。

runtimeVersion 取 `Environment.Version.ToString()`，os 为 windows/linux/macos，architecture 为 `RuntimeInformation.ProcessArchitecture` 名称转小写（如 x64、arm64）。不支持的 OS 明确失败，不猜测归类。runtimeVersion、os、architecture 和 buildHash 必须与当前宿主逐项相等。

分别计算以下七项实际加载程序集完整文件字节的 SHA-256。按如下固定顺序拼接 UTF-8 文本，各行以单个 LF 结束（包括最后一行），取 SHA-256 得到 buildHash：

```text
AiNative.Core=<coreHash>\n
AiNative.GridGame=<gridHash>\n
AiNative.GridGame.Hosting=<hostingHash>\n
AiNative.Headless=<headlessHash>\n
System.Text.Json=<jsonHash>\n
System.Text.Encodings.Web=<encodingHash>\n
System.IO.Pipelines=<pipelinesHash>\n
```

上面的 `\n` 表示一个 LF 字节，不是反斜杠和字母 n，也不额外加入代码块的展示换行。缺少程序集文件/无法读取时为 HostFailure/BuildIdentityUnavailable。首版不支持单文件打包。构建身份仅用于严格兼容匹配，不是安全签名。此清单扩充覆盖共享库与实际 JSON 依赖；旧构建身份不相等，按原策略拒绝恢复，不迁移历史快照。

## 5. CLI JSON 结果与错误码

除 help/旧演示外，stdout 恰好一个 JSON 对象加 LF，字段全部必填：

| 字段 | 类型与语义 |
| --- | --- |
| protocolVersion | i32=1 |
| status | `Completed` 或 `Failed` |
| category | Completed / InputError / SimulationFailure / HostFailure / ReplayDivergence |
| code, message | 稳定代码、诊断字符串；message 不作为机器判断依据 |
| location | Location 或 null；失败能定位时填写 |
| accepted, rejected | 非负 i32；当前调用实际完成的 Accepted/Rejected 命令数，不含 submit、空推进和快照之前历史 |
| comparedCheckpoints | 非负 i32；replay 已匹配的检查点数（包含初始点，不含首个不匹配点）；其他能力为 0 |
| initialCheckpoint | Checkpoint 或 null；尚未创建/校验成功时 null |
| checkpoints | 当前调用已正常完成操作的实际 Checkpoint[]；故障操作不加入，发生分歧的正常操作加入 |
| finalCheckpoint | 最后一个可信实际检查点或 null；空操作/inspect 成功时等于初始点；故障后只是历史证据，不代表故障实例可用 |
| artifacts | Artifact[]；仅列成功发布的文件 |
| divergence | Divergence 或 null；只在 ReplayDivergence 时非 null |

Location 字段：`operationIndex: i32 或 null`、`sequence: i64≥1 或 null`、`path: string 或 null`。null 索引表示初始点或尚未开始；path 是 JSON 字段路径或输入/输出绝对路径，用于诊断。Artifact 字段为 `kind: "snapshot" 或 "replay"`、`path: string`（发布文件绝对路径）。

Divergence 字段：`expectedHash: hash`、`actualHash: hash`、`differences: Difference[]`。Difference 字段：`path: string`、`kind: "Changed" / "Missing" / "Added"`、`expected: JSON值`、`actual: JSON值`。不存在一侧填 null，用 kind 区分真实 null。path 根为 state/session/result，实体定位形如 `state.game.units[id=2].health`；事件按 `result.events[0].health` 定位。所有差异按 path 的 ordinal 顺序、再 kind 排序；数组有序比较，单位按 ID 匹配。缺失整个对象报告该对象，不再重复其子字段。hash 不同由底层字段差异解释，不仅报告 hash 值。

| category / 退出码 | code 穷举及触发 |
| --- | --- |
| Completed / 0 | RunCompleted、SnapshotInspected、ReplayMatched、HostTracesMatched |
| InputError / 2 | InvalidArguments（参数）、InputReadFailed（读取）、InvalidJson（语法/重复字段）、InvalidProtocol（字段/类型/枚举/数字表示）、UnsupportedVersion（文件/规则/摘要版本或 rulesId）、UnsupportedRandomAlgorithm、EnvironmentMismatch、InvalidConfiguration、ConfigHashMismatch、ExpectedConfigMismatch、InvalidSnapshot（状态/计数/hash）、InvalidReplay（检查点完整性及内容/hash）、PendingCommands（结束队列非空）、ComparisonInputMismatch（compare 的起点或操作不同） |
| SimulationFailure / 1 | SessionFaulted（执行/查询故障，包括规则返回 null 或拒绝消耗随机）、SequenceOverflow（健康实例提交序号耗尽） |
| HostFailure / 1 | OutputExists、OutputPathConflict、OutputWriteFailed、BuildIdentityUnavailable、StandardOutputFailed、UnexpectedHostFailure |
| ReplayDivergence / 3 | CheckpointMismatch |

多错误优先级：CLI 参数 → 输入读取 → JSON/字段结构 → 版本/算法 → 环境 → 配置及内容标识 → 状态/检查点内部一致性 → 操作队列平衡 → 输出目标预检 → 执行。多个输入按起点文件、expect-config、commands 顺序；数组按索引，字段按本契约表顺序。Replay 内快照使用相应版本/配置错误，检查点自身错误归 InvalidReplay；正常执行产生的不匹配只归 CheckpointMismatch。

inspect 验证成功返回一个初始点，checkpoints 为空；replay 的统计、checkpoints 均为实际执行记录，不能返回期望冒充实际。规则事件序列化失败属于 HostFailure，保存最后可信检查点，不将不完整记录发布为完整回放。stderr 不可写时尽力保留退出码；stdout 写失败可能没有 JSON，此例外不得被包装为成功。

compare 只校验并比较两份证据，允许来源环境不同；不会调用严格恢复入口执行命令。两份记录必须具有相同配置、初始玩法/随机状态、调度位置和操作序列。结果匹配返回 HostTracesMatched，分歧使用已有 location/divergence；accepted/rejected=0，initialCheckpoint/finalCheckpoint=null，checkpoints=[]。来源环境保留在输入回放中。详细语义见[宿主对比](../../host-boundary-and-presentation.md#3-严格恢复与宿主证据比较)。

## 6. 样例、向量与复核方法

[样例索引](examples/README.md)包含默认场景、队列提交/推进、空推进、业务拒绝、初始及结束快照、完整回放、成功和失败响应。样例环境是显式虚构的协议夹具，不能据此声称可在当前二进制恢复；生产环境匹配没有绕过选项。

[hash-vectors.json](examples/hash-vectors.json) 固定保存规范化字节文件名、字节数和 SHA-256。`.canonical.json` 文件故意无结尾 LF，这是哈希输入而非普通格式化 JSON。包含配置、初始状态、移动后、结束状态、随机 state=UInt64.MaxValue 的状态，以及构建身份组合向量。数值与规则结果人工推导，哈希以 Python 标准库计算并用系统 shasum 独立复算，不使用本工程实现生成期望。

未来测试须读取固定字节/固定摘要，分别断言生产规范化器的字节相等和 hash 相等；不得在测试准备阶段用被测规范化器重新生成期望。随机极值向量验证数字编码及 hash 覆盖，不代表 GridGame 已使用随机伤害。历史构建向量继续用固定的三组 64 字符虚拟程序集哈希验证组合编码，不代表当前生产程序集清单；七项真实文件哈希另测。

## 7. T1 完成边界

契约编写与数据复核责任：当前主 Agent。后续 P2-T2–T6 的实现责任由承接任务的实现 Agent 承担；最终代码独立审查由另一 Reviewer Agent 或人承担，不由实现者自审替代。T1 记录仅说明契约固化；P2-T2/T3 的实现与行为验证另见阶段计划交付记录。

T1 交付检查：公共 API 的状态与失败语义、CLI 互斥和保留字、每个协议对象的字段/联合分支、完整样例与固定字节哈希、与上位计划的一致性。独立文档复核结果另记于阶段计划；发现影响公共契约的问题应在 T2 开始前解决。
