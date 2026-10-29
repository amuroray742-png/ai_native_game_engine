# 第一阶段：无头网格回合制玩法闭环

状态：T1–T6 已实现并完成本地验收（2026-10-29）。本文件记录开发任务、接口与实际证据；完整架构方向见[设计文档](ai-native-game-engine-design.md)，实现约束见[代码规范](engineering-guidelines.md)。

## 1. 目标与范围

不启动 Unity，从外部 JSON 加载场景，按命令完成一局。生产代码分为 Core、GridGame、Headless 三个项目，测试独立；生产库没有第三方依赖，宿主使用 System.Text.Json。

本阶段已包含场景加载、只读查询、命令调度、移动、攻击、回合、胜负、固定算法随机源及控制台演示。通用 Agent CLI、命令文件协议、快照持久化、回放文件、状态哈希、迁移、性能报告和 Unity 接入留到后续阶段。

### 玩法规则

- 双方各一个单位，场景给出唯一正整数 ID；A 先手，回合编号从 1 开始。
- 默认每回合 2 行动点，移动或攻击耗 1 点。耗尽不自动换方，当前方仍可 EndTurn；换方后恢复配置指定的行动点，每次换方回合编号加 1。
- 移动命令只接受 MoveDirection 的 Up、Down、Left、Right；分别对应 Y+1、Y-1、X-1、X+1。规则层按执行时当前位置计算目标，禁止越界、障碍或单位占用；非法枚举值返回 InvalidDirection。
- 攻击目标必须为相邻敌方单位，曼哈顿距离为 1；固定配置伤害，生命值最低为 0。死亡立即决定胜负。
- 非法操作不改变玩法状态、不消耗随机数、不产生事件。结束后所有玩法命令均拒绝。
- 首版玩法不使用随机伤害；独立测试验证随机算法和状态隔离。

## 2. 模块与接口

| 模块 | 实现职责 | 允许的项目依赖 |
| --- | --- | --- |
| AiNative.Core | Session、FIFO、规则契约、执行结果、故障态、SplitMix64 | 无 |
| AiNative.GridGame | 配置领域校验、地图与单位、规则、隔离视图和领域事件 | Core |
| AiNative.Headless | 文件/JSON、装配、固定脚本、终端输出 | Core、GridGame |
| AiNative.Tests | 行为测试与测试替身 | 上述三个项目 |

SDK 基线为 10.0.202，global.json 允许同 feature band 的补丁更新；核心与玩法使用 netstandard2.1 / C# 9，宿主和测试使用 net10.0。所有项目启用可空分析、确定性构建及警告即错误。测试包固定为 xUnit 2.9.3、VS runner 3.1.4、Test SDK 17.14.1，并提交依赖锁文件。

这些目标为未来适配保留边界，不能替代实际 Unity 编译与运行验证。

### 已落地的能力边界

| 类型/入口 | 行为 |
| --- | --- |
| SimulationSession&lt;TCommand, TView&gt;(rules, seed) | 独占规则实例和随机源，单线程且不可重入 |
| IGameRules.Execute(command, random) | 仅在 Session 推进时执行规则；返回 CommandResult |
| Submit(command) → long | FIFO 入队并返回递增序号，不验证玩法；null 不入队 |
| Advance() → StepResult? | 一次执行一条；空队列返回 null，且不调用规则 |
| GetState() → TView | 取得隔离视图；故障实例拒绝查询 |
| GridGameRules.Create(definition) | 先完整校验再创建，复制集合，不保留调用方可变集合 |
| Move(side, unitId, direction) | 不可变方向移动命令 |
| Attack(side, unitId, targetId) | 不可变攻击命令 |
| EndTurn(side) | 不可变回合结束命令 |
| GridGameView | 地图、障碍、单位、当前方、行动点、回合、胜方；集合和元素均不可修改 |
| DeterministicRandom(seed) | splitmix64-v1，NextUInt64 显式推进状态；无符号溢出是算法的一部分 |
| ScenarioLoader.Load(path) | 文件/JSON 结构校验与数据映射，领域校验仍由 Create 负责 |

规则实例装配后不能被第二个 Session 共用，也不能绕开 Session 手工调用 Execute；这是扩展契约，不是恶意插件沙箱。读取 State 仅用于诊断，本阶段没有随机状态恢复或快照 API。

CommandResult 的 Error 为 null 表示成功，否则为稳定原因代码；事件顺序由规则明确决定。StepResult.Sequence 是调度序号，不是游戏回合编号。拒绝命令会出队并产生结果，但不改变玩法回合或行动点。意外异常（包括规则拒绝时违规消耗随机数）标记 Session 故障并重抛；之后禁止提交、推进与查询，不尝试继续使用可能不完整的状态。

事件采用不可变数据对象：UnitMoved、UnitDamaged、TurnChanged、GameEnded。伤害事件记录实际扣除值；致死攻击按“受伤、结束”顺序返回事件。没有回调、全局总线或宿主反向裁决。

### 拒绝优先级

先检查 GameFinished，再检查 NotYourTurn。移动依次检查 UnknownUnit、NotYourUnit、InvalidDirection、OutOfBounds、Blocked、Occupied、NoActionPoints；攻击依次检查 UnknownUnit、NotYourUnit、UnknownTarget、FriendlyTarget、NotAdjacent、NoActionPoints。第一个不满足条件决定拒绝原因。

查询和返回的事件均不能修改内部状态。查询时复制集合，单位对象为不可变对象，后续行动通过替换对象提交新状态，先前视图不会随新逻辑步变化。

## 3. 任务与验收结果

| 任务 | 依赖 | 实现及证据 | 状态 |
| --- | --- | --- | --- |
| T1 工程基线 | 无 | 四项目解决方案、SDK、nullable、EditorConfig、依赖锁；根目录 build/test 与只读 format 可运行 | 已完成 |
| T2 核心调度与随机 | T1 | FIFO 单步、空队列、异常故障隔离、独立随机状态；固定 SplitMix64 向量测试 | 已完成 |
| T3 场景与加载 | T1/T2 契约 | JSON 严格输入、领域合法性、不可变元素与集合隔离；结构及领域失败测试 | 已完成 |
| T4 移动与回合 | T2/T3 | 单格移动、行动点、显式换方；非法移动、耗尽行动点及执行时校验测试 | 已完成 |
| T5 攻击与胜负 | T4 | 固定伤害、生命下限、即时胜负、有序事件；无效攻击和胜后队列拒绝测试 | 已完成 |
| T6 演示与验收 | T2–T5 | 外部 JSON 六步演示、最终断言、退出码、逐步状态/事件重复比较及进程测试；独立审查完成 | 已完成 |

实现按 T1→T2→T3→T4→T5→T6 集成。没有为本阶段搭建 ECS/GAS、反射注册、通用 Result 框架、联网或存档体系。

### T1 的检查边界

`.editorconfig` 管理基本格式，构建启用编译器检查，`dotnet format --verify-no-changes --no-restore` 只验证不修改。已增加基础 GitHub Actions 构建、测试、格式和演示检查；远端尚未执行。依赖方向由项目引用和独立审查确认，尚无专门自动架构扫描器；命名、职责和可读性仍需人工审查。

### T2 的确定性证据

种子 0 的前三个固定 SplitMix64 输出为 `E220A8397B1DCDAF`、`6E789E6AA1B965F4`、`06C45D188009454F`，作为独立常量测试期望，不由被测实现生成。另以 ulong.MaxValue 种子验证溢出与实例间隔离。测试范围是当前支持环境中的算法与行为，不扩展为跨运行时承诺。

### T3 的场景格式

示例见 [demo.json](../src/AiNative.Headless/demo.json)。根对象必填字段：

| 字段 | 类型与约束 |
| --- | --- |
| formatVersion | 整数，必须为 1 |
| width / height | 正整数；坐标从 (0,0) 开始，不分配整幅稠密地图 |
| obstacles | 必填数组，可为空；每个元素包含整数 x、y，不允许越界或重复 |
| units | 两个单位，双方各一个；包含正整数 id、字符串 side（A/B）、position（x/y）、正整数 health |
| actionPointsPerTurn | 正整数，默认演示为 2 |
| attackDamage | 正整数，默认演示为 2 |

所有字段名区分大小写；不允许未知字段、重复属性、缺失字段、null 数据或 null 数组元素。side 使用枚举字符串转换，接受 A/B 的大小写形式，不接受整数枚举。初始单位不能重叠或占据障碍位置。种子由演示入口固定为 42，不是 JSON 字段。

JSON 错误含属性/路径等上下文，领域错误指出字段或实体 ID；非法配置不会产生可运行对局。配置只在创建时生效，不支持运行中更新。

## 4. 演示与执行方法

仓库根目录执行：

```sh
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore
dotnet format --verify-no-changes --no-restore
dotnet run --project src/AiNative.Headless --no-build
```

SDK 使用本机已有版本，无需 Unity。默认 JSON 随构建复制到程序输出目录，从其他工作目录直接启动已构建程序也能找到场景。

可追加一个显式场景路径：

```sh
dotnet run --project src/AiNative.Headless -- src/AiNative.Headless/demo.json
```

该参数只替换场景，仍运行固定脚本和最终断言；任意配置不保证能完成演示。格式版本检查只针对场景，不是第二阶段的规则/快照兼容协议。

默认 5×5 地图，障碍 (2,2)，A 在 (0,0)、B 在 (2,0)，双方生命值 4、伤害 2：

1. A 移动到 (1,0)，余 1 行动点。
2. A 攻击 B，B 余 2 生命值，A 余 0 行动点。
3. A EndTurn，B 行动，回合 2。
4. B 攻击 A，A 余 2 生命值。
5. B EndTurn，A 行动，回合 3。
6. A 攻击 B，B 生命值 0，A 胜利，余 1 行动点。

输出为面向人的步骤与状态说明。返回码 0 表示六步全部成功且最终状态通过断言；1 表示执行、输出或验收失败；2 表示参数、读取、JSON 或领域配置错误。配置边界与执行边界分开捕获异常，不能把输出故障误报为配置错误。

## 5. 验收记录与独立审查

本地已验证：

- Debug 构建成功，0 警告、0 错误。
- xUnit 55 项测试全部通过，无跳过。
- 只读格式检查通过。
- 默认六步演示符合预期最终状态；显式路径测试通过。
- 无效配置返回 2；脚本拒绝、最终断言不符及输出故障返回 1。
- 在临时目录启动构建产物，进程退出 0 并输出 Demo passed。
- 同配置、种子与命令重复运行 10 次，比较逐步状态、事件类型与载荷、命令序号，结果一致。

独立 Reviewer 已只读审查核心、玩法、宿主与测试，并独立运行 55 项测试。审查发现输出 IOException 被误归类为配置错误；修复后新增输出故障回归测试，Reviewer 复核确认无未解决的阻断问题。按非阻断建议去除空项目节点，保留单行自动属性以改善扫描可读性。

当前未验证 Unity、跨平台/跨运行时行为和远端 CI，尚未实现快照或回放文件。没有以第一阶段测试替代这些后续验收。

### 方向命令调整

Move 不再接受目标坐标，此为进程内 API 变更；尚无回放文件协议需要迁移。连续排队的方向命令逐条依据执行时位置计算。移动命令日志只显示行动方、单位 ID 和 direction，以及执行结果，不显示 from/to；位置仍可从初始状态和每步状态输出读取。

方向接口调整后本地 59 项测试通过；覆盖四方向、四边界、非法方向枚举、障碍与占用，以及连续队列按执行时位置移动。只读格式检查和默认演示通过。此前 55 项记录为第一阶段初次验收证据。
