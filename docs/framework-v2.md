# 通用框架 v2 契约

本文件是当前契约，验证状态单列在[v2 验收索引](v2-acceptance.md)。v1 文档及材料仅保留历史证据，执行入口统一 v2，不静默迁移。

## 公共边界

| 边界 | 职责及所有权 |
| --- | --- |
| Core 的 SimulationRuntime&lt;TCommand,TObservation,TState&gt; | 独占 IGameSimulation 与随机源；Create/Restore、Submit、Advance、Capture/GetState、ExportState、Dispose |
| IGameSimulation | Execute 处理单输入，Advance 推进游戏一次，GetState 提供隔离观察，ExportState 捕获完整游戏状态；Dispose 释放游戏资源 |
| TickResult&lt;TObservation&gt; | Tick、按序 InputResult、按序 GameEvent、该 Tick 的不可变 Observation；观察持有不可序列化的实例身份 |
| Hosting 的 IGameModule | Id/Version/Timing、Description、显式 CodeTypes/NativeLibraries、配置/命令/状态编解码和结果验证；Create/Restore 返回完整 IHostedGame |
| IHostedGame | 面向工具的完整运行时，不暴露规则/会话的配对；状态通过 JsonElement 隔离 |
| IPhysicsWorld / IPhysicsWorldFactory | 明确创建/恢复/释放世界；刚体操作、Step、查询、Capture；元数据离线校验与原生候选恢复分开 |

接口用于两个真实游戏共享边界；模拟、Hosting、Physics 与 Headless 没有反射扫描、Service Locator、自动插件发现或通用系统调度。Headless 是可复用库，Examples.Runner 是显式装配入口。框架项目不得引用 Example。

Unity 应用服务允许单独的、有边界的例外：`AiNative.UnityCore.Core` 是兼容层唯一的应用级单例，模块在场景装配或 `AfterInitialize(ICore)` 阶段按接口绑定依赖，运行阶段使用已绑定引用。该包的帧回调与定时器仅服务表现和应用工作，不替代 PlayHost 的模拟时钟；无头、回放、存档以及各对局所有权维持本契约。[生命周期及公开 API](unitycore-lifecycle.md)

## 时间、错误与保存边界

Unity 侧的 [Assets 资源包](unitycore-assets.md) 单向依赖 UnityCore，拥有本地资源、实例、池和场景；其异步关闭扩展不改变模拟时间、状态、文件协议和每局 PlayHost 所有权。

Submit 分配连续输入序号并入队。Advance 消费调用开始时全部排队输入，按序执行，再推进一次游戏并发布结果；即使无输入也增加 Tick。输入结果携带输入序号，TickResult 携带 Tick，游戏回合只存在于 GridGame 状态。运行时禁止重入，包括 Tick 回调期间的提交、推进、观察与保存。

手动模式 ticksPerSecond=0、DeltaSeconds=0；固定频率为创建时确定的整数频率，PhysicsSandbox 为60。Core 不访问墙上时间。宿主决定何时调用 Advance，并负责采样与追赶，不在 Core 引入累积器或系统调度。

固定频率宿主把实时输入交给提交后下一个尚未执行的 Tick。同一 Tick 开始前的多条输入按提交序号消费一次，补步和暂停均沿用这条规则。PhysicsSandbox 的暂停输入策略、具体帧序列和检查点见[Unity 实施与验收](phase-3-physics-sandbox-unity-plan.md)。

业务拒绝须保证该输入不改变游戏状态或消耗随机数；Core 检查随机不变量，游戏测试验证状态不变量。后续输入、计时与物理继续运行。意外异常使实例故障，后续正常操作失败，只有释放可继续。计数溢出明确失败；Submit 溢出发生在入队之前。

ExportState/StateCodec.Encode 只在健康、无待处理输入时成功。物理 Save 还要求碰撞已消费，删除后已完成下一次 Step；不会为保存自动执行输入。CaptureGameState 用于验证中有待输入的检查点，不是可恢复快照：这种检查点只有完整操作前缀才能解释，不能传给恢复入口。

游戏负责观察的不可变投影；运行时不会为每 Tick 自动复制完整物理世界。快照和逐操作验证才捕获完整状态。Dispose 幂等；释放后的模拟/物理操作抛 ObjectDisposedException。恢复先验证外层和环境，再建候选、重建刚体、恢复内部状态、核对完整状态，失败释放候选；原对局由调用方保持，成功才替换并释放旧实例。

## Jolt 最小范围

第三方依赖默认集中在 [`third_party/`](../third_party/README.md)：原生构建脚本获取 `JoltPhysics/` 与 `joltc/`，NuGet 恢复使用 `nuget/`。`native/jolt/` 保留项目维护的服务、扩展和补丁，`artifacts/` 保存构建产物；依赖目录调整不改变公开物理契约或版本锁定。

固定 Jolt 5.6.0，提交 e77f175595e64cb44218cc9d9d56fc365ad0e36a。项目 C ABI 4，单精度、每 Tick 一次更新。基础接口迁移至 joltc 提交 `2bbcf73b1bec261e0351814662922f1b4784454c`，当前分层及验收见 [托管物理迁移](managed-physics-migration.md)，前一轮证据保留在 [joltc 迁移](joltc-migration.md)。`IPhysicsWorldFactory.Create(int workerThreads = 0)` 显式指定 0..16 个工作线程，0 在调用线程执行；PhysicsSandbox 场景必填 `workerThreads`，示例取2。单个世界仍要求宿主串行调用；独立世界可并发推进，每世界拥有临时内存与任务系统。支持球体/盒体、静态/动态刚体、创建/删除、冲量、位姿/线速度/角速度/休眠查询、接触开始/结束。size 是盒体半尺寸，球体使用 size.x 作为半径。上限1024刚体、4096刚体对/接触约束；容量不足明确失败。没有角色控制器、车辆、关节、软体、CCD 或 GPU 物理。

CMake 关闭示例、测试、调试渲染、profiler、GPU/CPU compute 扩展、LTO、double；启用受控浮点和异常边界。所有有效 BOOL、编译/链接 flags、编译器识别、实际 compile_commands、源提交、恢复补丁与二进制摘要进入原生构建材料。Runner 注册延迟的绝对路径 DllImport resolver，实际加载与哈希均来自 AppContext.BaseDirectory；仅注册不加载原生库。其他宿主必须保证同样的身份与加载位置一致性。运行身份包含 native-build.json 与实际原生库内容摘要；Windows/Linux 的构建身份分别记录。

C# 的 BodyRegistry 唯一拥有实体映射、BodyID 分配 generation 和退役映射，JoltWorld 持有原生 SafeHandle。原生世界仅拥有原生刚体、临时分配器、任务系统与接触缓存；并行碰撞回调通过互斥锁收集，更新完成后才读取；全局 Jolt 类型初始化受世界引用计数保护。原生回调只收集事件，异常延迟到 Update 返回后报告，不穿过 Jolt 更新锁执行 C#。原生事件只携带 BodyID；更新后由 C# 映射为实体，按稳定实体ID、子形状ID和事件类型归并排序。事件转换失败使世界故障。已删除 BodyID 的托管映射保留到接触消费，分配时避开未消费ID，防8位generation绕回串实体。

C# PhysicsSnapshotCodec 编码物理恢复 blob：格式标识 0x344a4e41、固定 workerThreads、ID分配generation、按实体排序的对象定义/材质/质量及BodyID，随后为原生捕获的 Jolt 全局、刚体、休眠与接触缓存。旧 ABI 3 或旧 magic 明确拒绝并重新生成材料，外层协议和游戏版本不变。`state.physics.workerThreads`、blob 内线程数和场景配置必须一致，均进入状态或配置摘要；同构建同环境复现以同线程配置为边界。JoltWorldFactory 在 C# 中校验元数据、创建候选、重建对象、恢复内部状态并核对完整快照；失败 Dispose 候选，不接触调用方原世界。固定版本的结构预读验证 All 标志、有限数、活体引用、重复ID、数量、合法接触/子形状、禁止CCD/约束、完整EOF。仅恢复位姿速度不符合此契约。

Jolt 原始 BodyManager::RestoreState 使用手动锁，不保证读取异常时解锁。构建脚本在 build 目录生成单一翻译单元副本，将该作用域改为 RAII 解锁；原始源码 checkout 保持精确固定提交和 clean。补丁脚本与生成源摘要进入构建身份。同时对线程池启动失败增加停止和回收补丁，并用独立子进程故障注入验证。MSVC 对 joltc 与项目边界使用 `/EHs /EHc-`，避免假设所有 C 接口均不抛异常而省略清理。升级 Jolt/joltc 必须重新审查结构布局与补丁，不能只改版本字符串。

原生复用 joltc 的 Update2、TempAllocator 及物理设置 Get/Set；临时分配器初始 16 MiB，使用上游 malloc fallback，配置进入构建清单。专用扩展仅保留完整内部状态、单线程 jobs、异常安全创建与最终 registry 清理。

沙盒的强类型 Play 会话和 CLI 视图共享同一实例。托管 `IPhysicsWorld.GetBodies()` 现在返回 `IReadOnlyList<ObservedBody>`。每个观察从世界登记的不可变定义复制形状与尺寸，并附带本 Tick 的位姿、速度及活动状态。已有物理接口实现需更新签名并重新编译。v2 存档的 `state.bodies` 仍写入原有诊断字段，几何定义仍由原物理快照恢复。[几何观察](../src/AiNative.Physics/PhysicsTypes.cs)

PhysicsSandbox 游戏 JSON 中的有限 binary32 数值沿用 .NET CLI 的最短可往返十进制表示。Mono 的写入长度、负零及接近两个浮点值中点时的读取结果会与 .NET 不同，沙盒适配在 Mono 中使用显式的最近值、同距取偶规则，保留负零。原生 ABI、快照字段和游戏版本沿用 v2。[数值编解码](../examples/AiNative.PhysicsSandbox.Hosting/PhysicsJsonFloat.cs)

## v2 文件协议

所有输入严格检查 UTF-8、重复/未知/缺失字段、非配对 Unicode 和字段类型。Int32 使用整数字面量，不接受指数、小数或 `-0`，零只接受 `0`；此限制不改变物理 binary32 负零的保留规则。非负 Int64/UInt64 用无前导零的 ASCII 十进制字符串。摘要为64位小写 SHA-256。对象属性按 ordinal 排序；模块规范化配置与领域状态（例如实体按ID排序），数组中有业务顺序的事件/操作保持原顺序。

场景：

```json
{"formatVersion":2,"gameId":"grid-game","gameVersion":1,"configuration":{"width":5,"height":5,"obstacles":[],"units":[{"id":1,"side":"A","position":{"x":0,"y":0},"health":4},{"id":2,"side":"B","position":{"x":2,"y":0},"health":4}],"actionPointsPerTurn":2,"attackDamage":2}}
```

具体配置与操作以[GridGame](../examples/data/grid.scenario.json)、[PhysicsSandbox](../examples/data/physics.scenario.json)为可执行材料，games/describe 提供模块与能力描述。协议版本与游戏版本是两个不同维度：外层保持2，GridGame为1，PhysicsSandbox因新增必填线程配置和快照ABI升为2。旧物理记录明确拒绝，按新场景重新生成。

命令文件：

```json
{"formatVersion":2,"operations":[{"op":"submit","command":{"type":"EndTurn","side":"A"}},{"op":"advance"},{"op":"advance"}]}
```

每次 advance 消费所有待处理输入，文件末尾必须无待处理输入；提交可以为零或多条。整个文件校验通过后才创建/恢复并执行运行时，非法后缀不会执行合法前缀。

快照字段：

| 字段 | 含义 |
| --- | --- |
| formatVersion / gameId / gameVersion | 外层协议与显式模块注册身份 |
| configuration / configHash | 内嵌规范化配置及内容摘要 |
| runtime | tick、submitted、processed、pending、ticksPerSecond、randomAlgorithm、randomState |
| state | 模块完整恢复状态；物理示例含 nextId、physics.backend/data/hash、bodies 诊断 |
| stateHash | gameId/gameVersion/configHash/runtime/state 的规范 JSON 摘要 |
| environment | runtime、os、architecture、buildHash、components（实际加载代码/原生材料摘要） |

保存时 submitted=processed、pending=0；tick不等于输入数。tick=0 时 processed=0；随机算法为 Core 指定 SplitMix64 版本。输入序号从1连续分配，恢复保留当前随机内部状态而非重新播种。stateHash排除environment、实例身份、表现数据；诊断数据单列在物理blob之外，但也纳入证据完整性检查。

回放含 formatVersion、initialSnapshot、commands、initialCheckpoint、checkpoints。每个操作一个检查点，字段为 operationIndex、runtime、state、stateHash、result。初始索引为null；提交结果含 kind=submitted/sequence；推进结果含 kind=tick/tick/inputs/events。inputs记录 sequence/accepted/error，事件字段归模块所有。提交检查点不改变状态和随机；推进检查点处理所有待输入并增加一个Tick。

读取回放验证结构、摘要、状态不变量和计数连续性，不在解析中执行玩法。物理离线检查验证blob外层与对象元数据、诊断ID和motion一致；原生内部状态的完整结构和可执行性在恢复时验证，离线compare不加载Jolt，不证明任意人为构造blob可恢复。合法记录的期望与真实执行分歧属于 ReplayDivergence；损坏结构或摘要属于 InputError。

## CLI 和比较语义

- games；describe --game ID。
- run (--scenario FILE --seed N | --snapshot FILE) --commands FILE，可带 --snapshot-out / --replay-out。
- inspect --snapshot FILE；replay --input FILE；恢复类入口可带 --expect-config SCENE。
- compare --expected RECORD --actual RECORD。要求相同模块、配置摘要、初始状态摘要、规范操作序列；输入不一致为 ComparisonInputMismatch。

除帮助外 stdout 为一份 formatVersion=2 JSON，包含 status/category/code/message、location、accepted/rejected、comparedCheckpoints、initialCheckpoint/checkpoints/finalCheckpoint、observation/data、divergence、artifacts。成功0，宿主/模拟故障1，输入错误2，分歧3。stderr单独诊断，输出流故障不回滚已发布文件。

处理顺序：参数 → 所需输入文件及各自结构/版本/领域验证 → expect-config → 当前环境/严格匹配 → 输出预检 → 创建或恢复 → 执行/逐点验证 → 顺序发布。v1 的全文件错误优先级不再作为当前契约，v2每个文件在读取时完成验证；任何输入错误均发生在执行前。协议字段错误携带路径，回放分歧携带首个operationIndex；执行期间故障携带当前操作索引，创建/初始捕获前故障索引为空。错误原生库版本返回 HostFailure/NativeLibraryIncompatible，消息包含 NativeLibraryVersionMismatch。内部物理候选恢复失败分类为模拟故障并清理资源。

replay重新执行且在首个分歧处停止，保留实际可信前缀；compare纯比较已有证据，允许来源环境不同，包含来源身份和首个字段差异。二进制差异路径包含state.physics，长值用前缀与摘要代替整段base64。compare不冒充跨平台恢复验证。

输出预检不创建目录、不覆盖已有目标；同目录临时文件写完关闭后禁止覆盖发布。不提供多文件事务：后续发布失败保留已成功文件并只列出实际制品。

## 表现策略

SequentialPresentationQueue要求同实例连续Tick，BeginNext→检查IsActive→应用UI/动画→Complete。只使用该帧自带观察。Reset后旧帧无法Complete或继续写新对局。

[Unity Play](unity-play-host.md) 与 CLI 共用 `GameRegistry.ReadScenario`。GridGame 的 `CreatePlay` 返回同一会话的强类型入口和工具协议视图。场景宿主向 `IPlaySession` 提交类型化命令并接收 `AdvanceTick` 的帧，CLI 从 `IGameModule.Create` 取得 `IHostedGame`。GridGame 的跨帧验收使用 `HostVerification.RecordSteps`，通过实际场景入口执行每个操作，在交还最后一个检查点前冻结终局状态。

固定频率 Unity 会话由 `PlayHost.Pump` 独占推进。验收模式在实际提交返回后和每个单步完成后调用 `HostTraceRecorder`，从同一会话捕获状态并核对相邻操作。单步观察回调内禁止再次推进、提交、替换或释放宿主。CLI 的主动驱动与 Unity 的被动记录使用同一检查点与回放编码。帧日志单列实际绘制的前后 Tick 与插值比例。[PhysicsSandbox 验收](phase-3-physics-sandbox-unity-plan.md)

RealtimePresentationBuffer保留Previous/Current两个观察，插值由表现适配按时间设置完成。PresentationEvents独立FIFO，含输入结果和事件，不附带历史完整观察。TakeEvents只取一次，延迟回调执行前检查IsActive；Reset清空旧批次并更换generation。IsAt检查当前指定Tick及该Tick事件已取出，宿主仍负责应用完成与相机/UI刷新。不会无限保存历史画面，也不会为截图清空未来所有表现。

## 重新生成与兼容

Hosting 的结果编码、观察/状态编码及顺序帧交付失败后，通过运行时的 `MarkFaulted` 显式终止实例。此入口用于弥补编码发生在模拟 Tick 提交之后的边界：不回滚已提交 Tick，不允许继续提交、推进或捕获状态。Core 不依赖 JSON 或宿主实现。CLI 在创建/恢复与执行阶段遇到模块抛出的协议异常时报告 SimulationFailure（退出码1），保留原错误码、字段路径、当前操作索引及最后可信检查点；输入预检中的同类异常仍为 InputError（退出码2）。

恢复候选在发布前同时核对完整游戏状态与 runtime 的 Tick、submitted、processed、pending、Timing 和随机状态；不一致释放候选，分别报告 `RestoreStateMismatch` 或 `RestoreRuntimeMismatch`。检查不修改旧会话。

不运行v1文件、不静默转换计数。按examples/data配置创建v2场景和操作，经run重新生成快照/回放。改变框架、游戏、适配、原生二进制或构建配置会改变身份；旧材料在严格恢复时明确拒绝。协议错误UnsupportedVersion提示重新生成。历史测试/CLI行为到v2的映射与实际证据见[v2验收](v2-acceptance.md)。
