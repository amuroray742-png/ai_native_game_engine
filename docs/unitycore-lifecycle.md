# UnityCore 应用服务契约

本轮目标是修复 UnityCore 首次导入中的 13 项问题，允许一个有边界的 Unity 应用级单例，并将该决定与行为修复分别记录。适用源码为 `packages/com.ainative.unitycore`，验证宿主为 PhysicsSandbox。具体玩法仍由现有 PlayHost 推进；没有改变 v2 文件协议或纯 C# 模拟内核。

## 所有权与装配

`AiNative.UnityCore.Core` 是兼容层唯一应用级服务入口；它与 `AiNative.Core` 模拟内核不是同一概念。Logger、模块和 TimeMachine 均为实例对象。Core 通过显式注册管理应用服务，不持有跨对局的随机源、权威世界或会话。静态 `Core.Log` 只转发到当前 Core 的日志对象，不创建第二个单例。

`RegisterModule(instance)` 仅允许在停止状态调用。模块 `OnRegister` 只为自身调用 `RegisterInterface<TService,TModule>`，失败时撤销本次全部接口映射。重复接口、重复实例和嵌套注册明确失败。场景装配及 `AfterInitialize(ICore)` 可以通过 `GetModule<TService>` 绑定接口，缺失接口立即抛错；运行方法使用已保存的依赖。该例外不适用于玩法、物理、Hosting 和无头工具。

## 生命周期与迁移

公共入口改为 `RunAsync(CancellationToken = default)`、`CloseAsync()`、`RestartAsync(CancellationToken = default)`；旧 `Close`、`Restart` 不再保留。`IModule.Initialize/Start` 增加 CancellationToken，返回的 `UniCoreException?` 为 null 表示成功。原 `Logger.Inst` 改为传入 `ICore.Logger`，入口诊断可使用 `Core.Log`。移除泛型反射 Singleton 和对外可修改的 ModuleManager 列表。

所有生命周期入口、模块回调及异步完成须在 Unity 主线程执行。模块必须响应取消，释放尚未交付的局部资源；尤其退出 Play 的取消收尾不能依赖下一次 PlayerLoop。外部操作不能立即取消时，模块须自行隔离并回收迟到结果，不可在关闭后发布资源。

RunAsync/RestartAsync 的外部 CancellationToken 仅控制启动；成功后断开外部取消注册。传给模块的内部令牌仍保留到 CloseAsync 请求取消，避免模块已取消而驱动仍运行。多个初始化共同响应取消时保留 Canceled 结果；混有真实错误或清理错误时仍报告失败。关闭操作保存独立完成源，允许关闭等待者同步恢复后启动下一轮，不改变其他等待者所等待的关闭结果。

| 状态与操作 | 行为 |
| --- | --- |
| Stopped → Run | 捕获稳定模块列表，创建独占根对象和禁用的 Alpha/Omega 驱动，进入 Starting。 |
| Starting | 按 ModuleOrder 升序、同序按注册顺序调用 Initialize，等待所有结果后串行 AfterInitialize、Start、AfterStart；最后进入 Running 并启用驱动。 |
| Running → Run | 幂等；Starting 时重复 Run 或 Restart 拒绝，Closing 时不得重新启动。 |
| 任意启动阶段失败 | 结束后续阶段，停止驱动、取消、等待全部已进入的初始化收尾，逆序 Shut；原始异常、通知异常及清理异常均保留。 |
| CloseAsync | 先停派发、取消启动并等待收尾，逆序 Shut、清除回调及根对象。重复关闭等待同一次操作。 |
| RestartAsync | Close 成功后重新初始化同一批实例；模块注册和接口映射保留，模块须支持 Shut 后重新 Initialize。 |
| 清理失败 | 其余模块仍尝试释放，最终报告失败并保持 Faulted，不自动重启或重复调用 Shut。 |
| 退出 Play / 应用 / 脚本重载 | 立即隔离旧 Core 并请求关闭。SubsystemRegistration 为下一次 Play 开放新实例；旧实例不能发布成功或改写新实例。 |

每轮只对已经进入 Initialize 的模块调用 Shut，包括部分初始化失败者；Shut 必须同步、幂等且能释放部分资源。错误通知只是通知，不能将失败改成成功。初始化任务先捕获各自错误，使 WhenAll 等待所有任务；仅消费其返回数组一次，不能再次读取池化 UniTask。

## 表现回调与定时器

TimeMachine 的 Update/LateUpdate/FixedUpdate/OmegaLateUpdate 与 Timer 仅承载应用和表现工作，不参与模拟权威 Tick。回调 order 从大到小，同 order 按注册顺序。Alpha 在本帧第一个 FixedUpdate 或 Update 建立时间起点，Omega 结束计时；没有 FixedUpdate 的渲染帧同样会重置预算。

注册 ID 在每个调度器内递增且不复用，溢出在修改注册前失败。票据绑定具体注册，不经 Core 查找；注销幂等，失效票据 Id 为 null。同类型同委托重注册替换旧注册并使旧票据失效。Remove(int) 接收注册 ID，不再接收委托哈希。票据只能由 Register 方法取得，不再公开手动构造；票据相等性采用对象身份，不能只用 Id/Type 判断跨调度器票据相等。

每次派发取得稳定批次：新增注册在下一次对应阶段派发生效；注销立即跳过尚未执行项。ClearAll 使全部旧票据及当前批次失效，不重置 ID。回调异常向调用方传播，finally 恢复派发状态；不允许同一阶段重入。

定时器每次更新最多触发一次，触发后重新计时，不补发长帧中错过的周期。重注册重置计数和 order；repeats=-1 无限、0 不触发、正数按指定次数。intervalS 须为有限正数，dt 须为有限非负数。不再复用 Timer 对象，避免旧批次引用已归还并重新初始化的对象；移除或 ClearAll 后由普通对象生命周期释放引用。
