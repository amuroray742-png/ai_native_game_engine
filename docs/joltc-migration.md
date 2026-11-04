# joltc 后端与多线程验收

## 边界与版本

基础物理绑定使用 [joltc](https://github.com/amerkoleci/joltc/tree/2bbcf73b1bec261e0351814662922f1b4784454c)，固定提交 `2bbcf73b1bec261e0351814662922f1b4784454c`。Jolt 仍固定5.6.0提交 `e77f175595e64cb44218cc9d9d56fc365ad0e36a`。两者源码由 `scripts/build_native.py` 获取至被忽略的 `TestResults/native-deps/`，校验提交和clean状态；不追踪main、不使用隐式系统安装版本。

`IPhysicsWorld`、Core和GridGame规则边界保持。`IPhysicsWorldFactory.Create(int workerThreads = 0)` 创建固定线程配置的世界；`PhysicsSnapshot.WorkerThreads` 携带同一设置。C#继续使用netstandard2.1/C#9与DllImport，没有引入JoltPhysicsSharp或反向托管碰撞回调。

调用路径为游戏 → Physics契约 → C# Jolt适配 → 项目物理世界服务 → joltc/Jolt。球盒形状、刚体定义、创建销毁、冲量、位姿速度查询、碰撞监听通过joltc接口。保留项目C ABI是为了集中管理世界所有权、稳定ID、回调收集与完整恢复；没有复制上游全部C接口。

产物仍为 `ainative_jolt.dll` / `libainative_jolt.so`，静态包含joltc和Jolt各一份。Runner从已哈希的绝对路径延迟加载，GridGame及离线compare不加载原生库。构建材料记录两个源码pin、扩展源、补丁与生成源hash、完整编译命令、有效配置和binary hash，并附两份许可证。

## 多线程与完整状态

- `workerThreads` 为必填整数0..16；0使用调用线程，正数为固定后台工作线程数，调用线程也可能执行任务。禁止按CPU数量自动选取，PhysicsSandbox示例为2。
- 单个世界的Submit/Step/查询/保存/释放由宿主串行调用；多线程仅用于一次物理更新内部。独立世界可由不同宿主线程并发推进，每世界拥有allocator和job system。
- joltc的全局基础资源以世界引用计数管理，创建/销毁registry受锁保护，监听函数表为固定原生函数。并行回调仅收集数据，互斥保护队列、原子记录失败；Update完成后按实体/子形状/事件排序去重，随后才返回C#。
- `WorkerContactCount` 是进程级诊断计数，用于证明实际工作线程执行了碰撞回调；不进入权威状态或摘要。
- 物理快照ABI升至3：magic `0x334a4e41`、UInt32线程数、1024字节BodyID generation、对象数、对象定义/BodyID及Jolt完整内部状态。旧ABI不静默转换。
- 外层`state.physics.workerThreads`、blob和场景配置严格一致。完整状态仍包含休眠、接触缓存、对象定义、ID分配及实体映射，保存边界和候选恢复事务保持不变。
- 外层协议仍为v2，GridGame版本仍为1；PhysicsSandbox游戏版本升至2。旧物理场景更新gameVersion并增加workerThreads后重新run生成材料；旧快照/回放明确拒绝。
- 确定性承诺限于同构建、同环境、同线程配置。跨平台比较分别记录；不同线程配置属于不同实验，不用compare结果宣称互换兼容。

## 必要原生扩展

`joltc_extensions.inl` 编译进固定joltc源的构建副本，直接使用该翻译单元的内部类型；外部不复制opaque对象布局。扩展提供完整SaveState/RestoreState、单线程任务系统、每世界allocator更新入口、显式确定性设置，以及异常安全创建和最后世界释放时registry容量清理。

`prepare_source.py` 仅生成构建副本：BodyManager恢复使用RAII解锁；JobSystemThreadPool启动失败时停止已启动线程，连首线程启动失败也回收heads。原始依赖checkout保持clean。MSVC的joltc、服务边界和故障探针使用`/EHs /EHc-`，因为上游C接口内部可能抛异常，不能使用`/EHsc`的extern-C不抛异常假设省略清理。

`thread_start_probe` 是独立测试可执行程序，故障注入仅在该目标编译，发布DLL不包含注入钩子。它验证第0/1/3个工作线程启动失败、已有世界仍可推进、后续成功创建2线程世界，并核对工作线程、候选分配和世界计数。构建脚本必须先通过该进程才发布原生库，报告随原生制品保存。

## 验收与证据

迁移前基线为[v2验收](v2-acceptance.md)。现有222项测试保留；新增并行测试覆盖0/1/2/4工作线程、128刚体碰撞、睡眠、刚体增删/冲量后的恢复连续性、逐Tick事件和完整blob一致、4世界并行更新、设置损坏及资源回收。CLI原80项全部保留，增加28项：0/2/4线程64刚体的独立进程重复/重放/保存恢复、不同配置比较拒绝、非法线程设置与旧游戏版本拒绝。

2026-11-04审查后的本地原生构建、故障探针、229项测试和108次CLI全部通过，证据为`TestResults/joltc-unit-rtti`与`TestResults/joltc-windows-rtti`。独立Reviewer复核了原生生命周期、多线程边界和异常路径，独立执行线程启动故障探针，未发现剩余代码阻断项。

最终代码提交 `f6e155614715da958474f964951984f671251680` 的[双平台CI 41826261281](https://github.com/rayss1/ai_native_game_engine/actions/runs/41826261281)全部通过；已下载并核验制品，而非仅查看作业状态。

| 平台 / .NET运行时 | 单元测试 | 真实CLI | 制品ID |
| --- | --- | --- | --- |
| Windows x64 / 10.0.14 | 229通过，无失败/跳过 | 108通过 | 13318674448 |
| Linux x64 / 10.0.14 | 229通过，无失败/跳过 | 108通过 | 13319222959 |

两边均通过1585个截断位置和1586个单字节变异的独立原生进程探针，worlds=0。线程启动探针分别注入第0/1/3线程启动失败，3次候选创建均清理，旧世界仍可推进，随后2工作线程创建成功；liveWorkers/liveAllocations/worlds均为0。首轮Linux探针链接暴露RTTI不一致，最终提交已对Jolt、joltc、桥接与探针统一关闭RTTI，并在两平台重新验收。

平台比较制品 `13318389010` 的GridGame、普通PhysicsSandbox、0/2/4线程密集场景共5组均为`HostTracesMatched`。该结果不扩大跨平台或不同线程配置的恢复兼容承诺。

下载与核验索引：`TestResults/joltc-ci-41826261281/verified-summary.json`。核验TRX计数、每条CLI实际/预期退出码与stdout code、两类原生探针、两个源码pin及ABI，并确认制品库和Runner实际库的SHA-256均等于native-build.json记录。原生库SHA-256：

- Windows：`f2b2ce18a04199002daa9b982627d8b2bc5d14e0b7e2838da33466c4575c4fe1`
- Linux：`7f5e8f3949fabca119729262102e50eda88529cc1d19cba026a2b64a8eccbd5b`

平台制品保留7天；过期后按上述代码提交及README命令重新生成。最终证据追加仅修改文档，不重复声明引擎测试已在文档提交上执行。

复现仍使用README中的 `python scripts/build_native.py`、`dotnet build`、`dotnet test` 和 `python scripts/verify_v2.py --output <新目录>`。CI分别构建两个平台的真实原生库，并比较GridGame、普通物理示例和0/2/4线程密集场景的证据。

Unity Editor、Player、IL2CPP与性能收益未在本次声明完成；多线程能力通过正确性和实际工作线程回调验证，不据此宣称性能提升。
