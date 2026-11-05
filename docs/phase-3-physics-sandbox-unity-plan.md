# 第三阶段：PhysicsSandbox 接入 Unity Play 实施与验收

**状态：Windows x64 Editor 已完成本地实施与验收。** 本阶段交付一个可在 Windows x64、Unity Editor 6000.3.23f1 中操作的 PhysicsSandbox 场景。玩家能生成球和盒、施加冲量、删除刚体、暂停和读档。已有基线分别是 [GridGame 的 Editor Play 验收](unity-play-host.md) 和 [Jolt 的无头验收](managed-physics-migration.md)。独立 Player、IL2CPP 和跨构建存档兼容另立里程碑。

沿用[框架 v2](framework-v2.md) 的文件协议、游戏版本 2 和 Jolt ABI 4。PhysicsSandbox 以 60 Hz 推进，实时输入归入**提交后下一个尚未执行的 Tick**。Jolt 独占物理裁决，Unity 负责采样、推进时机和呈现。验收比较两个宿主实际执行的 v2 操作序列，机器的墙上时间和 Unity 渲染帧号只供诊断。

## 一、实时输入、掉帧与暂停

每个渲染帧先确定暂停状态并采样输入，再由 `PlayHost.Pump` 推进，最后呈现。一次玩法点击产生一次 `Submit`，当场取得连续序号。Tick T 完成后、T+1 开始前提交的所有输入，均在 T+1 按序消费一次。该步处理完输入再推进物理一次，同帧后续补步照常运行。宿主每帧最多推进四步，结余留到下一帧。一次长帧即使留下六步积压，此时刚提交的输入仍归下一次实际执行的步。[公共时钟实现](../packages/com.ainative.unity/Runtime/PlayHost.cs)

**暂停期间继续接收玩法点击并排队。** 这里的暂停指游戏内暂停，Editor 仍运行 Update。暂停与继续属于宿主控制，不分配玩法输入序号。暂停保留已有积压和已排队输入，冻结 Tick、已处理数、随机状态及物理世界。新提交只增加已提交数和待处理数。暂停期间的时间不计入积压。继续后，若积压或本帧新增时长足够，第一步依序处理暂停前和暂停中排队的输入。时长不足时输入留在队列里。显式保存遇到待处理输入应返回 `PendingCommands`，保持队列和 Tick 原值。

用以下固定向量检验时钟，场景采用[标准物理场景](../examples/data/physics.scenario.json)，种子 42，两个工作线程，从 Tick 0 开始。时长以 60 Hz 的步数表示。

| 宿主操作 | 必须观察到的结果 |
| --- | --- |
| 一个渲染帧收到 10 步时长 | 完成 Tick 1–4，余 6 步。每个 Tick 各有一个空输入结果列表。 |
| 暂停，跨多个渲染帧点击三次，依次给实体 2 施加冲量 `[1,0,0]`、删除实体 999、给实体 2 施加冲量 `[0,1,0]` | 返回序号 1、2、3。停在 Tick 4，已提交 3、已处理 0、待处理 3。尝试保存失败。继续前没有输入结果或物理步。 |
| 继续，传入 0 新增时长 | 仅消耗旧积压，完成 Tick 5–8。Tick 5 的结果依次为接受、拒绝 `UnknownBody`、接受。两条冲量仅在 Tick 5 执行。 |
| 再传入 0 新增时长两次 | 第一次完成 Tick 9–10，第二次零步。至此已处理 3、待处理 0，Tick 6–10 没有重复输入。 |

另测无积压时的暂停。先在不足一步的帧提交一条输入，再暂停并提交第二条。继续后累计时长仍不足一步时，两条都保持待处理。凑足一步后，两条在同一 Tick 按原序消费。对照均匀帧率时，以已完成 Tick 为采样条件。若分别在完成 Tick 4 和 Tick 10 时点击，两次输入应分别落在 Tick 5 和 Tick 11。

## 二、读档后的第一帧

接入前，沙盒观察只提供实体的位姿、速度和活动状态，生成事件只有实体 ID。现由物理适配已持有的刚体定义生成沙盒的只读表现数据。每个存活实体至少提供稳定 ID、形状、尺寸、位置和旋转，以及动静类型和活动状态。正常创建、运行中生成及恢复后首次观察都使用同一入口。Unity 只按 ID 创建、更新、移除显示物体。球的半径取 `size.x`，盒的三轴全长取 `2 × size`。形状和尺寸不依赖历史点击、生成事件、旧 GameObject 或 Unity 对物理快照字节的解析。[沙盒状态与事件](../examples/AiNative.PhysicsSandbox/SandboxSimulation.cs) · [刚体定义的所有者](../src/AiNative.Physics.Jolt/BodyRegistry.cs)

读档沿用 v2 恢复流程。先校验模块、配置、同环境身份及完整快照，恢复候选会话，在隐藏的候选画面中应用初始观察。两者就绪才替换旧局，清除旧局的待处理输入、插值、积压和待播事件，使旧回调失效。读档后先呈现存档 Tick，验收时保持暂停直到截图完成。读档和画面重建均不得调用 `Advance`。[恢复校验](../src/AiNative.Hosting/StateCodec.cs)

验收使用[标准操作文件](../examples/data/physics.commands.json)，分别在 Tick 90 和 91 各保存一次。此时队列为空，Jolt 更新和本步接触事件消费均已完成。两处刚体集合的差异可以直接检验场景是否靠公开观察重建。

| 检查点 | 通过条件 |
| --- | --- |
| Tick 90，删除前 | 恢复后画面恰有 ID 1、2、3。运行中生成的 ID 3 是半尺寸为 `[0.4,0.4,0.4]` 的盒。 |
| Tick 91，删除与再生成后 | 恢复后画面恰有 ID 1、2、4。ID 3 消失，ID 4 是半径 0.4 的球。往返加载这两份存档，每个 ID 只有一个显示物体。 |
| 第一帧与后续帧 | 在任何新 Tick 前检查缓冲的前后观察均为存档 Tick，逐个核对可见实体的几何、位置、旋转，并在相机和 UI 刷新后截 Game 视图。触发旧局的延迟回调不能改写新画面。继续相同操作后，每一 Tick 都应与不中断运行相符。 |
| 失败路径 | 分别注入环境不匹配、损坏快照和候选画面创建失败。记录失败原因。旧局的 Tick、画面和可推进性均应保留，已分配的候选资源应释放。 |

## 三、Unity 与 CLI 逐 Tick 对照

1. **固定起点。** 两端独立从相同场景、游戏版本、种子 42、`workerThreads=2` 和锁定的 Jolt 构建启动。先固定操作文件，再运行两端。Unity 从场景的实际输入入口提交，CLI 从文件提交。均匀帧与补步帧都要在输入边界处分帧，例如先完成 Tick 90，下一帧采样删除和生成命令，再由时钟推进 Tick 91。另存每个 Unity 帧的输入序号、暂停状态、实际推进的 Tick 范围和呈现 Tick，用来核对掉帧与暂停发生的位置。
2. **只由场景时钟推进。** Unity 的记录器旁路记录每次实际完成的 `Submit` 和每次 `Pump` 内实际完成的单步。每步返回后、下一步开始前，从同一会话编码结果和完整检查点。一次补四步就产生四个推进检查点。复用 Hosting 的 v2 编码与摘要。实时缓冲只保留相邻观察，事件批次按序取一次。验收器不能另行推进，也不能在渲染帧结束时倒推中间状态。完整捕获仅在验收模式启用。[现有记录逻辑](../src/AiNative.Hosting/HostVerification.cs) · [实时缓冲](../src/AiNative.Hosting/Presentation/RealtimePresentationBuffer.cs)
3. **比较每个操作。** 使用 CLI 的 `compare --expected <CLI回放> --actual <Unity回放>`。核对初始检查点和每次提交、推进的运行计数、随机状态、完整游戏及物理状态、`stateHash`、输入序号、接受或拒绝原因，以及有序事件的实体 ID、子形状和类型。通过要求为退出码 0、`HostTracesMatched` 且检查点数准确。物理字节按 v2 精确比较。

| 对照组 | 预期结果 |
| --- | --- |
| 标准物理场景，均匀推进与多步补帧各跑一次 | 每份 Unity 记录独立对照同一 CLI 记录。240 次推进、4 次提交，共 **245 个检查点**（含初始点）。至少出现一次 `ContactStarted`。在消费实时表现批次时核对事件的 Tick、顺序和次数。 |
| 第一节的暂停向量 | 将 4 次推进、3 次提交、6 次推进写成固定 v2 操作序列。暂停和继续只记入 Unity 帧日志。两端均有 **14 个检查点**（含初始点）。 |
| Tick 90 与 91 的读档续跑 | 每个宿主分别恢复自己环境中生成的存档，从该 Tick 执行相同后缀。先核对恢复点和不中断运行的对应点，再将后缀逐操作对齐。两端从相同恢复点起录的后缀才能直接交给 `compare`。 |
| 负对照 | 仅在暂停向量的 Unity 回放副本中把 Tick 5 第二条输入的拒绝原因从 `UnknownBody` 改成合法值 `StaticBody`。预期退出码 3、`CheckpointMismatch`、零基 `operationIndex=7`，首个差异为 `$.result.inputs[1].error`。 |

## 四、实施顺序与交付

| 顺序 | 工作与完成条件 |
| --- | --- |
| 1. 沙盒会话与视图 | 由游戏模块显式提供同一实例的强类型 Play 会话和工具视图。先在无头测试中覆盖球盒区分、删除、恢复后的首次查询与查询无副作用。 |
| 2. 独立 Unity 工程 | 在 `examples/unity/PhysicsSandbox/` 装配公共 Play 包、游戏适配和 Windows x64 原生插件。核对实际加载的程序集、Jolt DLL、ABI 和构建清单摘要。做出可手动操作的场景，重开后只保留新局的世界，停用或退出 Play 后世界数应回到进入前的基线。 |
| 3. 真实场景验收 | 在 PlayMode 覆盖第一节的帧序列、第二节的成功与失败路径。分别在开启、关闭 Domain Reload 时进出 Play。将两端的 v2 回放交给独立 CLI 进程执行第三节对照。 |
| 4. 审查与归档 | 按[仓库规范](engineering-guidelines.md)独立审查输入归属、资源所有权和记录器推进路径。用新的输出目录保存输入文件、环境摘要、两端原始回放、帧日志、恢复点快照与截图、PlayMode 结果、CLI 命令及退出码。复现命令和制品索引见第五节。 |

只在三类验收全部通过后关闭本项。实际运行失败时保留首个分歧和对应场景，由实现者复现、独立审查者核对。

## 五、Windows Editor 实施与验收记录

2026-11-05，在本机 Windows x64、Unity 6000.3.23f1 运行[独立场景](../examples/unity/PhysicsSandbox/Assets/Scenes/PhysicsSandbox.unity)。准备脚本核对原生构建清单并同步插件。进入 Play 后读取实际加载的 Jolt DLL 和托管程序集摘要。沙盒使用场景内 UGUI 面板输入，测试通过 GraphicRaycaster 命中按钮并调用其点击处理器。[准备与核验入口](../scripts/unity_physics.py) · [对照脚本](../scripts/verify_unity_physics.py)

| 验收 | 本机结果 |
| --- | --- |
| 开启和关闭 Domain Reload | 每种设置均通过 11 个 PlayMode 用例和 1 个 EditMode 进出 Play 用例。退出后的活动物理世界数为 0。 |
| 同起点的逐操作比较 | 每种设置下，均匀推进和补步各匹配 245 个检查点，暂停向量匹配 14 个检查点。两种设置共 6 组。 |
| 读档与续跑 | Tick 90 的 ID 3 为盒，Tick 91 的 ID 4 为球。各宿主用本环境自己的存档续跑，逐操作对照本宿主不中断的后缀。Unity 与 CLI 的后缀独立匹配 153 和 150 个检查点，两种设置共 4 组。 |
| 暂停和负对照 | 时钟按 4、4、2 步消费积压。三次暂停输入在 Tick 5 按序产生接受、拒绝、接受。两个负对照均退出 3，首个分歧为零基操作 7 的 `$.result.inputs[1].error`。 |
| 文件与失败恢复 | 真实临时存档槽覆盖写和回读通过。待处理输入使保存返回 `PendingCommands`。环境不匹配、摘要损坏、候选画面初始化失败均保留原对局。 |
| 回归 | 249 项 .NET 测试通过。108 个无头 CLI 进程及原生损坏探针通过。GridGame 使用临时工程副本重跑，快慢表现与两种重载设置的四组对照各匹配 16 个检查点。 |

第一次对照时，Unity 和 CLI 在 Tick 1 的 Jolt 快照字节相同，Mono 却把同一个单精度数输出为不同长度的小数。沙盒现在在 Mono 中按 v2 的十进制规则读写 binary32。真实 PlayMode 用例覆盖负零、最短字符串 `7.038531E-26` 及两个舍入中点附近的合法输入。逐 Tick 比较仍使用完整状态与精确摘要。[协议边界](framework-v2.md) · [数值与视图回归](../tests/AiNative.Tests/PhysicsPlayTests.cs)

**复现。** 先按 [README](../README.md#验证) 准备 Windows x64 原生库与 Editor，再选择新的输出目录运行：

```sh
python scripts/unity_physics.py verify --output TestResults/physics-play
```

输出包含输入、快照、CLI 命令与退出码、原始回放、Unity 帧日志、截图、测试 XML 和 Editor 日志。帧日志的 `renderFromTick`、`renderToTick` 和 `renderFraction` 来自已完成的绘制，读档首帧用于插值的两个观察均来自存档 Tick。本轮完整运行目录为 `TestResults/physics-unity-accepted/`。可移植的输入、记录、XML 和 CLI 结果另行归档。[机器摘要](evidence/phase-3/physics-play/summary.json) · [验收制品 ZIP](evidence/phase-3/physics-play/physics-play-evidence.zip)

[Tick 90 读档首帧](evidence/phase-3/physics-play/restored-90.png) · [Tick 91 读档首帧](evidence/phase-3/physics-play/restored-91.png)

本轮复用本机已有的固定版本 Windows 原生构建。独立审查已复核输入序号、每步检查点、候选释放和场景按钮绑定。人工试玩、Player、IL2CPP 和本次改动的远端 CI 状态均不在上述结果内。
