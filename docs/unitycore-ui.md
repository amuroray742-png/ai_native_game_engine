# UnityCore UI 契约与实施状态

## 当前状态

GridGame 已接入本模块：HUD由真实Bundle加载，Presenter管理绑定，场景与应用服务寿命分离；Windows Editor及玩法轨迹验证见[GridGame UI接入](unity-play-host.md#gridgame-ui-模块接入2026-11-17)。UIDemo的Player证据不自动扩展为GridGame Player验收。

本模块按 [设计记录](unitycore-ui-design.md) 与 [实施计划](superpowers/plans/2026-11-12-unitycore-ui.md) 开发，已有基础窗口所有权及生命周期，并接入页面栈、模态顺序、事务回滚、输入/焦点策略、有界缓存、LRU、Trim、旧会话隔离、安全区更新和内置过渡；实际验证范围见下文。Task 6 已补齐 Clear、迟到任务隔离、外部销毁恢复与组件诊断回归；Task 7 已补齐独立交互 Demo 与真实 Bundle 集成；Task 8 已补齐统一隔离验证脚本、层级顺序回归与交付证据；详见末节。

2026-11-16 已补齐 Windows x64 Mono/IL2CPP Release Player 专项，两后端各十组检查通过，并完成双重载296次Editor回归；限定范围见[Player验收](#windows-player-与-il2cpp2026-11-16)，不扩展下方历史证据。

同日后续完成两个后端的[原生鼠标键盘与Alt+F4系统关闭](#windows-player-原生输入与系统窗口关闭2026-11-16)；鼠标由用户执行，键盘与关闭由CUA执行，销毁回调资源归零。抓图及标题栏关闭按钮的工具限制单独记录。

最新一轮已补齐[原生会话客户端截图与用户点击标题栏×关闭](#原生交互截图与标题栏关闭2026-11-16)：Mono/IL2CPP共34张实时PNG、两后端各4个视觉检查点和10个交互检查点通过；退出0且资源归零。

包路径为 `packages/com.ainative.unitycore.ui`，命名空间为 `AiNative.UnityCore.UI`。独立工程为 [UIDemo](../examples/unity/UIDemo/README.md)，包含可点击场景、真实 Bundle 内容构建入口和集成测试。

## 边界与依赖

模块单向依赖 UnityCore、Assets、UniTask 2.5.11 和 uGUI 2.0.0，使用 Unity 6000.3.23f1。它是应用表现服务，不引用模拟内核、Hosting 或游戏示例，不推进权威 Tick。宿主提供 EventSystem 和显式 Presenter 工厂，不增加全局单例、反射扫描或依赖注入容器。

`UIPresenter` 是连接业务与窗口实例生命周期的必要抽象，提供同步 `Bind()`、`Resume()`、`Suspend()`、`Unbind()`。业务依赖由工厂闭包显式传入，不经全局事件总线查找。

## 已实现公共契约

- `UIOptions(int closedCacheCapacity = 8, int maxPageDepth = 16, Vector2? referenceResolution = null, float matchWidthOrHeight = 0.5f, Color? modalMaskColor = null)`：不可变选项；容量非负、页面深度为正。默认参考分辨率 1920×1080、匹配值 0.5、遮罩黑色 alpha 0.5；分辨率须有限且为正，匹配值及颜色 RGBA 须有限且位于 0..1。null 使用默认值，显式 Color.clear 保持透明。
- `UITransitionOptions(float durationSeconds, bool fade, Vector2 enterOffset, float enterScale)`：时长有限非负、偏移有限、缩放有限且为正；`default` 表示无过渡。
- `UIWindowDefinition<TView>(string address, UIRole role, Func<TView, IAssets, UIPresenter> presenterFactory, UICachePolicy cachePolicy = UICachePolicy.Release, bool opaqueFullscreen = false, bool allowHudInput = false, UITransitionOptions transition = default)`，其中 `TView : UIView`。
- `UIRole` 固定为 Hud、Page、Modal、Overlay。只有 Page 可以开启 OpaqueFullscreen 或 AllowHudInput；Overlay 的目标契约为永不交互。
- `UIModule(UIOptions options, EventSystem eventSystem)` 及 `Register<TView>(UIWindowDefinition<TView> definition)`：同一 View 类型不能重复登记，运行中禁止登记新类型。
- `UIView` 显式序列化根 CanvasGroup、SafeArea、Visual 和可选默认 Selectable；绑定前校验根 CanvasGroup 位于 View 根且 enabled，子 CanvasGroup 不得 ignoreParentGroups；SafeArea 必须是 View 的直接子节点，Visual 为 SafeArea 的独立后代。
- 窗口子 Canvas 只能继承 Root 排序；overrideSorting=true 在绑定前、激活/恢复后及打开提交前被拒绝，沿既有回滚/清理路径处理。业务运行中不得修改此排序约束；无逐帧自动扫描修复。
- `UIResult` 与 `UIOpenResult<TView>` 提供 Error、Succeeded；默认结果不是成功，失败的 Open 结果访问 View/Handle 抛出 InvalidOperationException。
- `UIHandle` 为不透明值身份，内部区分 owner、generation、instance、session 与 cleanup-only 标志。
- `UIOperationException(string message, Exception innerException, bool committed, UIHandle? handle = null, UIHandle? cleanupHandle = null)` 保留原始异常、提交状态及可选清理身份。

`IUI` 的 OpenAsync（HUD/Modal/Overlay）、PushAsync（Page）、CloseAsync、ReleaseAsync、BackAsync、BringToFront、TryGet、HasModal 与 ModalStateChanged 已接入实际窗口所有权。关闭按 CachePolicy 缓存或释放；容量为零时释放，Release 成功等待 scope 排空。过渡参数已生效，默认零时长无动画；正时长使用独立 Visual 的线性淡入淡出、位移及缩放，按 unscaledDeltaTime 推进。TryGet 返回稳定可见且未清理的记录，包括透明页面后可绘制的前页，不代表可交互；不返回正在进入/退出的候选。诊断提供不可变窗口/挂起操作/页面深度/模态/缓存/失败清理计数；缓存计数包含尚未成功释放的淘汰保留项。TrimAsync 已实现，按调用时的缓存会话快照清理；ClearAsync 已实现运行中会话清理与重建，在受保护回调内返回 ReentrantCall；具体失败重试和旧会话隔离见 Task 6。

## 运行契约

ClearAsync 在运行中停止接收新请求、取消并排空操作，清理所有栈内、可见、缓存及失败记录；失败继续清理其他实例并保持停收，允许重试。只有成功后才能恢复接收并由宿主释放或替换旧 Model。Shutdown 复用排空路径且不恢复接收；Clear 成功建立新一代 UI scope，保留窗口定义和宿主 EventSystem。

诊断仅报告实际组件、材质和纹理身份及已观察到的风险，不预测 native 批次，不自动判断全部实例材质来源或 TMP fallback/submesh。无法归因的来源应标为未知；专项归因与 Frame Debugger 性能检查另行人工执行。

## 验证记录

2026-11-12：Windows Editor 隔离宿主首次缺少类型时编译失败（CLI 退出码 6），不算执行断言。补齐值类型、登记、停止操作及诊断声明后，当前完整 EditMode 套件 18 项通过、0 失败、0 跳过。证据位于 `TestResults/ui-task1-red-20261112/task1-final.xml` 与同名日志。完整日志记录了一次 Unity CDN 配置请求超时，未发现 JobTempAlloc/TLS 警告；该环境告警未影响测试结果。角色保留测试验证定义不会另造任意 layer，实际绘制顺序仍待 PlayMode 验证。

2026-11-12 后续本地 PlayMode 记录 `TestResults/ui-registration-fresh-20261112/rollback-diagnostics-green.xml` 为 9 项通过、0 失败、0 跳过；覆盖基础所有权、查询、计数和解绑失败重试。原文“未执行 PlayMode”已据本地 XML 更正，这不代表当时已完成双 Domain Reload 或真实输入验收。

## 基础生命周期收敛（2026-11-13）

窗口清理采用独立进度，避免一个回调失败阻止其余资源释放：

| 状态/步骤 | 提交与清理规则 |
| --- | --- |
| 工厂/Bind/Resume | 外部回调处于提交保护内；Bind/Resume 进入前登记清理责任，返回后检查取消 |
| 激活 | 激活后再次检查取消，只有检查成功才提交 Visible 并返回成功 |
| Close/回滚 | 先退出可见集合，分别尝试 Suspend、Unbind、scope.CloseAsync；成功步骤不重复 |
| CleanupFailed | 保留类型身份与 cleanup handle；仅当所有必要步骤成功才移除记录 |
| Core.CloseAsync | 停收、取消 Open、等待 Open 收尾及窗口 scope 排空，然后交给 Assets 关闭 |
| Core 强制退休 | 即使跳过异步关闭，Shut 仍同步隔离 Root 并尝试所有窗口解绑；Assets 随后强制回收资源 |
| 回调内退休 | 当前外部回调结束后同步解绑，不等待下一帧；原始错误与解绑错误同时保留 |
| Core.RestartAsync | 正常关闭成功后使用新 generation、Root、scope；旧句柄不能操作新窗口 |

Presenter 清理允许部分执行、View 已销毁以及 Unbind 成功后重试 Suspend；不能通过访问已销毁 View 完成必要解绑。EventSystem 由宿主保证覆盖 UI 运行周期，跨场景时同样由宿主保留并避免重复。

Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0，在仓库外独立 UIDemo 副本执行。最终 UI、Core、Assets 的 122 个源码/程序集定义/JSON 文件与验证副本逐文件 SHA-256 一致，指纹见 [机器摘要](evidence/unitycore-ui/lifecycle-20261113/summary.json)。独立只读审查发现的回调错误被延迟清理覆盖问题已修复；审查后运行最终矩阵，Reviewer 已复核四份结果，未发现剩余基础生命周期阻断项。

| Domain Reload | PlayMode | EditMode |
| --- | --- | --- |
| 开启 | 20/20 | 19/19 |
| 关闭 | 20/20 | 19/19 |

合计 78 次执行，0 失败、0 跳过。PlayMode 通过真实 Core 注册 Assets/UI，覆盖正常关闭及顺序、重复关闭、同模块重启、旧句柄隔离、回调取消、回调内关闭/退休、成功后取消不关窗、清理失败重试、双窗口强退失败隔离及双重异常保留。两份 EditMode 结果均包含连续两次真实 EnterPlayMode/ExitPlayMode，并在每次退出后检查 Suspend/Unbind 计数及 Root 移除。

开发期首次 15 项 PlayMode 有 3 项失败，分别复现本轮三项问题。后续扩展测试曾因预期异常日志类型不匹配而失败；首次实际 Play 周期测试的 NUnit SetUp 在重载后重复执行导致失败，改为协程首次 Enter 前准备。以上失败材料均保留，未覆盖或计入最终通过次数。最终日志有已知 LicensingClient validation 提示但许可证成功取得；PlayMode 含由 LogAssert 明确匹配的故障注入异常。未发现 C# 编译错误、JobTempAlloc/TLS 警告。

完整 XML 与日志见 [证据归档](evidence/unitycore-ui/lifecycle-20261113/lifecycle-evidence.zip)，本地原始输出为 `TestResults/ui-lifecycle-20261113/`，`workspace.txt` 指向独立源码副本。本轮还检查了未跟踪 UI 文件的行尾空白、JSON/asmdef 语法、文档本地链接及 `git diff --check`。未运行远端 CI 或无关模拟内核测试。

复现时使用新的证据目录和独立工程副本；不在用户打开的工程运行测试。分别运行 `unity run <副本工程> -- -executeMethod AiNative.UnityCore.UI.Tests.UIPlayCycleTests.ReloadOn` 与 `ReloadOff` 设置重载，再各运行以下两种模式，每次使用独立 XML/日志路径：

```text
unity test <副本工程> --mode PlayMode --filter AiNative.UnityCore.UI.Tests --output <新XML路径> --timeout 600 --format json -- -logFile <新日志路径>
unity test <副本工程> --mode EditMode --filter AiNative.UnityCore.UI.Tests --output <新XML路径> --timeout 600 --format json -- -logFile <新日志路径>
```

本轮基础生命周期门禁已通过。Task 2 的全部边界及后续 UI 功能不据此视作完成；未验证的真实 Bundle、真实鼠标键盘、截图、性能采样、Player、IL2CPP 和移动端不计入本轮范围。

## Task 3 页面栈、模态与事务回滚（2026-11-13）

本轮在前述生命周期基线上完成无动画导航事务，没有新增依赖或改变模拟内核与 Assets 公共接口。`UIRun` 按运行所有权、打开/恢复、关闭/置顶拆成 partial 文件；`UINavigation` 唯一拥有页面和模态顺序，窗口 scope 仍由 UIRun 持有。

| 能力 | 已实现行为 |
| --- | --- |
| 页面导航 | Page 仅 Push；根页 Back/Close 返回 NoBackTarget；非栈顶 Close 返回 NotTopPage；栈深超限明确拒绝，不丢历史 |
| 页面可见性 | 透明页保留后方页面直到第一个不透明页；更早页面 Suspend 并 inactive；可绘制前页可以 TryGet，但不能交互或 Release |
| 模态 | Open/Close 与页面共用导航门禁，Back 优先关闭顶模态；支持非顶模态关闭与层内置顶，同一顶模态置顶幂等 |
| 输入和焦点 | 共用遮罩位于顶模态下方，仅顶模态交互；无模态时仅顶页及允许的 HUD 交互；Overlay 不交互；最后模态关闭恢复仍有效的非模态选择 |
| 导航忙碌 | 导航事务串行，其他导航返回 NavigationBusy；HUD/Overlay 独立加载可交错；重复 Close 等待原完成源，完成源在保护区和导航门禁外完成 |
| 回滚 | 提交前取消/Bind/Resume/Suspend 失败恢复原页、输入和焦点；恢复也失败时先同步隔离整组导航窗口，再逐个清理，不影响独立 HUD/Overlay |
| 故障所有权 | 候选清理失败保留 CleanupHandle 和类型占用；已释放 View 的必要解绑仍可重试；已有失败窗口可通过原句柄 Release |
| 提交后故障 | 通知/资源清理失败报告 Committed=true；已销毁窗口不提供虚假展示句柄；回调触发 Core 关闭后停止旧通知并保留原异常与取消 |
| Core 关闭 | 停收、取消后同步禁用 Root，再等待挂起操作与资源排空，避免后续窗口在清理期间短暂保留输入 |

提交点、恢复失败隔离范围、模态返回焦点及关闭中通知取消的细节见 [设计第 5 节](unitycore-ui-design.md#5-状态导航事务与重入)。`Committed` 表示本次请求是否提交；恢复失败时原导航历史可能已隔离，不能将 false 理解为没有任何清理副作用。

测试在仓库外独立 UIDemo、Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0 中执行。最终结果如下：

| Domain Reload | PlayMode | EditMode |
| --- | --- | --- |
| 开启 | 43/43 | 19/19 |
| 关闭 | 43/43 | 19/19 |

合计 **124 次执行，0 失败、0 跳过**。每组 PlayMode 含原 20 项所有权/生命周期回归和新增 23 项导航测试；EditMode 含原公共契约与实际两次进出 Play。覆盖页面与模态顺序、透明链、栈限、加载挂起、取消时机、回调部分失败、故障重试、提交后通知、工厂/Presenter/激活/禁用/焦点/模态事件重入、HUD 焦点及 Core 关闭交错。输入证据为 CanvasGroup 策略、遮罩层级和 EventSystem 选择回调，不能替代真实射线、鼠标键盘或输入后端验收。

独立 Reviewer 指出的模态隔离通知、通知中关闭、HUD 焦点链、先隔离后排空及同步退出幂等问题均已修复并回归；Reviewer 已复核最终代码、设计契约和四份 XML，未发现剩余具体阻断项。134 个 UI/Core/Assets 源码、程序集定义与 JSON 文件逐文件匹配验证副本，指纹见 [机器摘要](evidence/unitycore-ui/navigation-20261113/summary.json)。完整 XML、日志和开发期失败材料见 [证据归档](evidence/unitycore-ui/navigation-20261113/navigation-evidence.zip)，本地目录为 `TestResults/ui-navigation-20261113/`；复现命令沿用上一节的精确 UI 过滤与 ReloadOn/ReloadOff 入口。

开发期先以 4 项失败验证缺失导航功能；扩展回归有一次测试错误地假设资源实例化一定异步，现改为消耗真实 Assets 每帧实例预算再验证 NavigationBusy，不给生产流程人为增加 Yield。该失败未计入最终通过数。最终日志无 C# 编译错误或 JobTempAlloc/TLS 警告；保留已知许可校验提示及 LogAssert 明确匹配的故障注入异常。JSON/asmdef、本地文档链接、未跟踪文件空白与 `git diff --check` 检查通过。

实施计划 Task 3 已完成。Task 4 缓存/LRU/Trim，Task 5 安全区/动画/完整输入验收，以及 Clear、外部销毁、完整迟到任务、交互 Demo、Bundle 和 Player 验收仍待后续任务；本轮未运行远端 CI、无关模拟测试、性能采样或截图验收。

## Task 4 有界缓存、LRU、Trim 与旧会话隔离（2026-11-13）

在 Task 3 基线上完成独立关闭缓存。类型字典维持唯一实例，`UIWindowCache` 链表维护 LRU；`UIRun.Caching` 负责入缓存、淘汰及 Trim，不增加公共接口或第三方依赖。

| 能力 | 已实现行为 |
| --- | --- |
| 冷缓存 | Cache 策略关闭后 Suspend、inactive，保留绑定与 scope；命中复用 View/Presenter，不重复 Bind 或 reparent |
| 有界 LRU | 默认容量 8，容量 0 直接释放；重开后关闭刷新顺序，重复 Close 不刷新；栈内页面不计入关闭缓存 |
| 真实释放 | 腾位等待实际资源销毁；正在释放或清理失败的条目继续计入容量；受害条目失败时释放新关闭候选并返回已提交异常 |
| 会话隔离 | 缓存重开获得新句柄；旧句柄不能 Close、Release 或 BringToFront 新会话；未重开的关闭句柄可以 Release |
| 取消回滚 | 健康回滚恢复原句柄与 LRU 顺序；冷隐藏恢复失败则清理并提供可重试 CleanupHandle，不恢复旧展示身份 |
| Trim | 捕获缓存记录及会话，跳过等待期间重开或重开后再关闭的候选；不删除导航历史；单项失败继续其他项并支持重试 |
| Core 生命周期 | 真实 Core 关闭等待挂起 Trim/清理并排空缓存；缓存 Resume 内 Restart 会取消旧 Open，不把候选重新放回旧缓存 |

独立只读审查发现了重复 Close 在清理期间错误返回 WindowBusy 的回归；已修复为共享原关闭完成源，原有两项回归测试恢复通过。审查复核 17 项新增缓存测试及生产代码，未发现剩余具体阻断项。容量保留、失败回退释放及回滚原顺序的完整规则见 [设计第 6 节](unitycore-ui-design.md#6-缓存与-assets-所有权)。

Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0，在仓库外独立 UIDemo 副本执行，未使用用户打开的工程。最终矩阵：

| Domain Reload | PlayMode | EditMode |
| --- | --- | --- |
| 开启 | 60/60 | 19/19 |
| 关闭 | 60/60 | 19/19 |

合计 **158 次执行，0 失败、0 跳过**。每组 PlayMode 含原 43 项生命周期/导航回归和 17 项缓存测试；EditMode 含公共契约和实际连续两次进出 Play。缓存测试覆盖同实例新会话、旧句柄拒绝、LRU 更新、零容量/Release 策略、实际销毁期间类型与容量保留、取消恢复、失败淘汰、Trim 会话交错及继续清理、页面历史、冷隐藏失败重试、真实 Core 关闭/重启、回滚失败以及双 Trim 与 Core 关闭交错。

最终 UI/Core/Assets 的 137 个源码、程序集定义及 JSON 文件与验证副本 SHA-256 一致。Reviewer 已独立复核最终四份 XML、哈希、源码指纹、归档内容与验收文档，无剩余阻断问题。机器摘要及指纹见 [summary.json](evidence/unitycore-ui/cache-20261113/summary.json)，XML、完整日志、重载设置日志及开发期红灯见 [证据归档](evidence/unitycore-ui/cache-20261113/cache-evidence.zip)。本地原始目录为 `TestResults/ui-cache-20261113/`；复现命令沿用基础生命周期节的 ReloadOn/ReloadOff 与精确 UI 测试过滤。

开发期最初 5 项缓存测试为 1 通过、4 失败，证明缺失功能；首轮全套为 46 通过、2 失败，定位重复 Close 回归；修复后扩展回归 56/56，通过审查建议补测后 60/60。这些结果保留但不计入最终矩阵。最终日志无 C# 编译错误/警告、JobTempAlloc/TLS 或 assertion failed；保留已知许可校验提示及原生命周期测试明确匹配的故障注入异常。JSON/asmdef、文档本地链接、未跟踪源码空白与 `git diff --check` 同步检查。

实施计划仅新增完成 Task 4。Task 5 安全区/动画/完整输入验收，Task 6 Clear、外部销毁与完整迟到任务，以及交互 Demo、真实 Bundle、Player/IL2CPP/移动端、性能采样和远端 CI 未据此宣称完成。

## Task 5 输入、焦点、安全区和内置过渡（2026-11-13）

在 Task 4 基线上完成 Task 5。新增 `UITransition` 管理每次动画及票据，`UIRun.Presentation` 管理安全区检测、动画接入与提交保护外的完成通知；`UISafeArea` 执行归一化，`UICanvasInputGate` 维持额外 Canvas 的窗口输入约束。继续使用既有 ITimeMachine，不新增公共接口或第三方依赖。

| 能力 | 已实现行为 |
| --- | --- |
| 输入路由 | 双模态只有顶层响应射线点击和 Submit，共用遮罩吞掉外部点击；Overlay 穿透；导航过渡期间阻断 UI 输入，HUD 加载完成不能解除阻断 |
| 焦点 | 旧选择必须存活、激活、可交互且属于允许输入的窗口，否则恢复合法默认值或清空；支持默认控件已销毁、HUD 关闭及下层模态动画关闭 |
| Prefab 校验 | enabled 根 CanvasGroup、禁止 ignoreParentGroups、直接子节点 SafeArea 与独立 Visual 后代；非法结构在 Bind 前失败并清理 |
| 额外 Canvas | 仅报告排序/批次风险，不重写材质和层级；附加局部射线过滤，防止 overrideSorting 绕过根输入策略 |
| 安全区 | 每个运行一张尺寸/safeArea 检测票据，仅在变化时更新正文；零尺寸有限回退，背景/遮罩不收缩；关闭和强退注销票据 |
| 内置过渡 | Visual 的线性 fade/offset/scale，保存并恢复基准；读取 unscaledDeltaTime，timeScale=0 仍能完成，零时长不注册动画票据 |
| 导航事务 | 入场期间保留旧页，动画完成才提交；退出先恢复前页再动画，历史保留到提交；非顶模态退出不改变原顶模态/遮罩顺序 |
| 取消与关闭 | 缓存重开取消恢复基准及旧会话；新候选取消清理；停止 TimeMachine 派发后 Core 关闭/强退仍终止过渡，不等待动画 Tick |

具体节点、输入和过渡契约见 [设计第 7 节](unitycore-ui-design.md#7-输入安全区与动画)。独立审查指出并修复了非顶模态退出错误置顶、已销毁默认焦点访问，以及安全区尺寸回调内 Core 退休后继续写入几何的问题；同时补强 overrideSorting 射线边界和非导航关闭焦点刷新。当前独立代码审查无剩余具体阻断项。

Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0，在仓库外独立 UIDemo 副本执行，未修改用户打开的工程。最终矩阵：

| Domain Reload | PlayMode | EditMode |
| --- | --- | --- |
| 开启 | 81/81 | 25/25 |
| 关闭 | 81/81 | 25/25 |

合计 **212 次执行，0 失败、0 跳过**。每组 PlayMode 包含原 60 项生命周期/导航/缓存回归及新增 21 项输入、焦点、过渡和屏幕更新测试；EditMode 包含原 19 项及新增 6 项安全区归一化/节点隔离用例。验证真实 Core 注册、关闭/重启路径，以及完成/强退票据注销、停止派发退出、取消基准复位、前页 Resume 失败回滚、HUD 交错、重复动画 Close、尺寸回调内退休。横竖尺寸、安全区恢复及零尺寸采用可控矩形输入；不是设备刘海屏实测。

最终 UI/Core/Assets 的 147 个源码、程序集定义及 JSON 文件与验证副本 SHA-256 一致。Reviewer 已独立复核最终四份 XML、本地与 ZIP 内哈希、源码指纹、归档内容及设计/验收/实施计划，无剩余具体阻断项。机器摘要及源码指纹见 [summary.json](evidence/unitycore-ui/presentation-20261113/summary.json)，最终 XML、完整日志、重载设置及开发期材料见 [证据归档](evidence/unitycore-ui/presentation-20261113/presentation-evidence.zip)。本地原始输出为 `TestResults/ui-task5-20261113/`，复现入口沿用基础生命周期节的 ReloadOn/ReloadOff 与精确 UI 过滤。

开发期先用 3 项失败验证过渡未实现，接入后 63 项通过；扩展输入回归两次因测试错误假设 overrideSorting 在 inactive Staging 重设父级后保留而失败。现于激活后明确建立该射线绕过条件，再验证模态期间过滤有效且框架不改 sortingOrder。后续 80 项、审查补测后的 81 项均通过；历史失败材料保留，不计入最终矩阵。

最终日志无 C# 编译错误/警告、JobTempAlloc/TLS 或 assertion failed；保留许可校验提示、LogAssert 明确匹配的 Suspend 故障注入与额外 Canvas 警告。Editor 退出另有 Mono abort_threads 和 debugger-agent 端口提示，全部最终命令实际退出码为 0。JSON/asmdef、文档本地链接、未跟踪源码/文档空白与 `git diff --check` 已检查。

Task 5 完成范围是框架行为及 EventSystem.RaycastAll/ExecuteEvents 的路由验证。Task 6 的 Clear、完整外部销毁与迟到任务，Task 7 的交互 Demo、真实 Bundle、鼠标键盘后端与截图，以及性能、Player/IL2CPP/移动端和远端 CI 均未据此宣称完成。

## Task 6 Clear、迟到任务、外部销毁与诊断（2026-11-13）

本轮完成运行中 Clear、旧运行迟到任务隔离、外部销毁恢复及不可变诊断，新增 23 项 PlayMode 测试。Clear 与 Shutdown 共用停收、取消、操作排空和资源释放路径；Clear 成功后创建新一代 UI scope，旧句柄失效，允许打开新根页。失败继续清理其他记录并保持停收，重复 Clear 可重试；宿主必须等待成功后才能释放或替换业务 Model。重复调用共享结果，受保护回调内调用返回 ReentrantCall；Core 关闭或退休优先，不允许迟到 Clear 重开 UI。

外部销毁由 Update 轮询 AssetInstance.IsAlive/View 存活状态，检测先于屏幕尺寸未变化的提前返回，不依赖 OnDestroy。恢复任务受操作跟踪和导航事务保护，剔除失效页面/模态并恢复可用前页；回调销毁候选不能提交成功。销毁的缓存与关闭窗口直接释放，清理失败保留原句柄供 Release 重试；等待期间再次 Close 共享完成结果。缓存准入在 LRU 等待结束后重新检查存活状态，旧任务仅可移除自己持有的记录。

Clear 的模态 false 通知若订阅者抛错，下一次 Clear 可重新通知整个订阅列表；订阅者须幂等，不提供恰好一次保证。外部销毁恢复错误保存在 LastRecoveryError，供诊断历史失败，不因后续 Release 成功自动清空。

诊断快照增加 Windows、StateCounts、实际 Graphic/Canvas/Mask/RectMask2D 及资源 ID，集合不可修改且不暴露 Unity 对象引用。总组件数包含 Root 与 inactive 后代；窗口内 Canvas 项用于定位额外 Canvas/overrideSorting。Graphic.material 记录配置或默认基础材质，不读取 materialForRendering，不等同最终渲染材质；MaterialOrigin 为 Unknown，TMP 专项、来源归因及批次仍需人工检查。实例 ID 只适用于当前进程。

测试覆盖 Clear 清空栈/缓存/动画与挂起 Open、失败保留 Model/继续清理/重试、回调重入与 Core.Close、启动中退出、复用模块的新 Core 隔离、旧加载与旧 Clear 迟到、销毁栈顶/前页/模态/缓存/候选、恢复错误以及诊断快照。迟到加载通过受控 IAssetBackend 驱动真实 Core/AssetsModule/UIModule；从未激活对象通过真实 AssetScope 下的受控窗口记录验证 IsAlive 检测。这些不是 UI 真实 Bundle 集成证据。复用既有实际进出 Play 测试。UIDemo 启用 Core/Assets testables，并为已有 Assets 测试添加内置 Audio 模块；UI 包未增加音频运行依赖。

独立只读审查指出并修复回调销毁后提交、Clear 重入判断顺序、恢复失败重复通知、LRU 等待后死对象入缓存及死缓存重复 Close 未共享完成等问题，最终代码无剩余具体阻断项。

Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0，在仓库外独立 UIDemo 副本执行，未修改用户打开的工程。最终代码矩阵如下：

| Domain Reload | PlayMode | EditMode |
| --- | --- | --- |
| 开启 | 104/104 | 25/25 |
| 关闭 | 104/104 | 25/25 |

共 **258 次 UI 执行**；相关 Core PlayMode **16/16**、Core EditMode **24/24**、Assets AssetCancellationTests **12/12**，共 **310 次执行，0 失败、0 跳过**。最终 UI 矩阵在最后一项重复 Close 修复后重跑；相关 Core/Assets 回归在该 UI 局部修复前通过，其代码未随之变化。154 个包源码/配置及宿主 manifest 文件与隔离副本 SHA-256 一致。独立审查已复核七份最终 XML、本地与 ZIP 内哈希、154 份源码/配置指纹、44 项归档内容及设计/验收/实施计划，无剩余具体阻断项。文档链接、JSON/asmdef、源码/文档空白及 git diff --check 均已检查。

机器摘要、七份最终 XML 哈希与源码指纹见 [summary.json](evidence/unitycore-ui/cleanup-20261113/summary.json)；最终 XML、完整日志、重载设置及开发期材料见 [证据归档](evidence/unitycore-ui/cleanup-20261113/cleanup-evidence.zip)。本地原始输出为 `TestResults/ui-task6-20261113/`，最终 UI 文件使用 `verified-on/off-play/edit` 前缀；复现沿用基础生命周期节 ReloadOn/ReloadOff，过滤 UI.Tests、UnityCore.Tests 和 Assets.Tests.AssetCancellationTests（完整名称见摘要）。

开发期保留三项红灯、84 项通过、扩展测试等待环导致的隔离 Editor 主动终止（无完整 XML）、错误复用退休 Core 导致的 93 通过/1 失败，以及修正后的 99 项通过。首次加载既有 Assets 测试因宿主缺少 Audio 模块编译失败，修正后 102 项通过；随后补测至 104 项并执行最终矩阵。历史中止、编译失败和旧矩阵不计入最终通过次数。

最终七份日志未发现 C# 编译错误/警告、JobTempAlloc/TLS 或 assertion failed；保留许可校验提示、LogAssert 匹配的 Suspend 故障注入和额外 Canvas 警告，以及 Editor 退出 Mono abort_threads/debugger-agent 端口提示，最终命令退出码均为 0。

Task 6 验证限于上述框架行为。Task 7–8 的交互 Demo、真实 Bundle UI 集成、真实鼠标键盘后端、设备刘海屏/旋转、截图、性能、Player/IL2CPP/移动端和远端 CI 未据此宣称完成。

## Task 7 独立 UIDemo 与真实 Bundle 集成（2026-11-13）

独立 [UIDemo 工程](../examples/unity/UIDemo/README.md) 已包含 Bootstrap/Other 场景、8 个窗口 Prefab、共享图片、ResourceGroups 及 meta，使用 uGUI Text 和显式 Model/View/Presenter。宿主先注册 Assets 再注册 UI，始终从部署后的 Bundle 加载；测试使用 EditorAssets.CreateConfigured 并确认配置为 AssetBundle。新增 Core 测试程序集内部可见性只用于 Demo 测试重置真实 Core，不扩大生产公开接口。

Demo 可经按钮走 Home/Gallery/Details、透明页、HUD、双模态、Toast、Back、重复打开拒绝、容量 1 缓存/LRU、Trim 和动画取消。独立宿主面板显示错误与诊断，故障停收时仍可重试 Clear。Clear 成功后才释放旧 Model、替换会话依赖并 Push 新根页；失败保留旧 Model。已有工厂读取宿主持有的当前 Model，不运行中重新登记定义。Single 切场景保留宿主、EventSystem、Core 与 UI Root。Toast 两秒后关闭，旧会话计时不能关闭新展示。

宿主先 await 动态图片 lease，再打开窗口；Presenter 同步绑定。测试直接比较两个 Bundle Prefab 的原始图片引用与动态 lease，避免只验证赋值；分别验证“Model 释放后缓存持有，Trim 最后卸载”和“Clear 全窗口后 Model 继续持有，释放最后 lease 才卸载”。最终 SBP 2.6.2 产物包含 **9 个地址、9 个 Bundle**，catalog 与所有 Bundle 的部署哈希均匹配；构建克隆 ResourceGroups 到新目录，开发配置 Version 仍为 1，未写入发布版本。

Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.0，在仓库外 `C:/unity-verification/ui-task7-6b94b8` 执行，未操作用户其他工程。最终矩阵：

| Domain Reload | UI PlayMode | UI EditMode | Bundle Demo PlayMode |
| --- | --- | --- | --- |
| 开启 | 104/104 | 25/25 | 11/11 |
| 关闭 | 104/104 | 25/25 | 11/11 |

共 **280 次执行，0 失败、0 跳过**。11 项新增 Demo 用例覆盖三页/透明页射线路由、双模态焦点、HUD/Toast/缓存句柄、共享图片与最后持有者、容量 1 LRU、动画取消、失败 Clear 保留/重试、宿主异常显示、Single 切场景及 Toast 到期/旧会话隔离。原 104 项 UI PlayMode 与 25 项 EditMode 全量回归，包含实际进出 Play。没有重复借用前轮 Core/Assets 测试数量。

可见 Editor 的原生输入与上述 RaycastAll/ExecuteEvents 测试分开验收。先发现 Pipeline 1.0.0 的输入命令仅支持新 Input System；Demo 使用旧版 StandaloneInputModule，因此改用 Windows Computer Use 的操作系统输入事件。文字可访问性确认 Game View 焦点，原生空白点击与 Unity Input.mousePosition 校准坐标后，实际点击 Next 从 1 页到 2 页，键盘 Return 到 3 页；鼠标 Modal 与 Return 形成双模态，Return/方向键逐层返回并恢复焦点；鼠标 Clear 更换为 Session 2，Scene 切到 Other 且 Core 仍运行。它是 OS 注入的原生输入验证，不是人工手持设备测试，也没有将 ExecuteEvents 冒充输入后端。

Game View 从 **source=screen** 捕获 2560×1256 画面并等比例缩为 1280×628。最终 [稳定页](evidence/unitycore-ui/demo-20261113/stable-final.png)、[双模态](evidence/unitycore-ui/demo-20261113/modals-final.png) 与 [双页过渡](evidence/unitycore-ui/demo-20261113/transition-final.png) 已检查。过渡辅助菜单在真实入场约 100 ms 暂停：诊断为 Home Visible、Gallery Entering、pending=1；恢复后 depth=2、pending=0，无宿主错误。未使用 Camera RenderTexture 截图。最终可见 Console 错误为 0；退出前恢复 auto-tick/background override，并正常关闭隔离 Editor。

独立只读审查补强了 Prefab 依赖与动态 lease 身份、两种最后持有者顺序、Toast 自动关闭/旧会话计时及异常显示回归。截图审查发现并修复宽屏标题裁切和 HUD 重叠，改用顶部/底部锚定，随后重新生成、构建、测试和截图。342 份源码/配置/meta 与隔离副本匹配；隔离宿主额外安装 Pipeline 1.0.0 仅用于观察和截图，生产 manifest 的既有依赖逐项匹配，工具 manifest 单独归档。生成场景/Prefab/设置由 Editor 产出并复制回源码，未手改场景 YAML。独立 Reviewer 已核对最终六份 XML、源码/配置/meta 指纹、构建/部署/归档 Bundle 哈希、三张截图、manifest、场景/Prefab GUID 引用及原生输入状态记录，最终证据与文档审查通过，无剩余阻断项。

机器摘要与指纹见 [summary.json](evidence/unitycore-ui/demo-20261113/summary.json)，六份最终 XML、日志、SBP 构建报告及实际 Bundle、输入观察、截图和开发期材料见 [完整归档](evidence/unitycore-ui/demo-20261113/demo-evidence.zip)。本地输出为 `TestResults/ui-task7-20261113/`，最终 XML 使用 `final-on/off-ui-play/ui-edit/demo` 前缀。复现入口为 UIDemoBuilder.Create/BuildContentSbp/ReloadOn/ReloadOff；测试分别过滤 `AiNative.UnityCore.UI.Tests` 与 `AiNative.UIDemo.Tests.UIDemoTests`。

开发期首构建因多个 Label 子资源同类型同名被拒绝，修正唯一命名后构建通过；首 8 项 Demo 通过，扩展到 10 项时一项 LogAssert 错配外层/内层异常，修正后 10 项通过；布局及 Toast 补测后 11 项通过，再运行最终矩阵。Windows 窗口截图两次超时，后续使用 Pipeline screen 捕获和文字焦点；一次探索性 focusedWindow 脚本返回 null，改为明确选择 GameView。所有失败/旧截图保留，不计入最终通过证据。

最终矩阵及构建日志未发现 C# 编译错误/警告、JobTempAlloc/TLS 或 assertion failed。已知许可提示、测试明确匹配的故障注入、Unity 的旧 Input Manager 弃用提示，以及 Editor 退出 Mono/debugger-agent 信息分别保留；所有最终测试命令退出码为 0。Markdown 本地链接、JSON/asmdef、源码/文档空白、342 份指纹及 git diff --check 已检查；Editor 生成的序列化文件保留原格式。Task 8 的统一验证脚本及最终完整交付审查未完成；Frame Debugger/CPU/GPU/overdraw 性能、设备刘海屏/旋转、Player/IL2CPP/移动端与远端 CI 未由本轮代替。

## Task 8 层级顺序、隔离验证与交付（2026-11-13）

本轮收敛 HUD/Overlay 的稳定层级为**成功提交顺序**，包括缓存重开。候选入场时暂置顶，入场完成、输入刷新及存活校验通过后再次置顶；取消候选不重排其余窗口，稳定窗口仍可 BringToFront。Page/Modal 保持导航顺序。新增 4 项 PlayMode 覆盖 HUD/Overlay 的冷开、缓存重开、较晚开始却较早完成的交错及显式置顶。先运行 ConcurrentHudUsesCommitOrder，得到预期的 sibling index 断言失败，再修复生产路径。

新增 [verify_unity_ui.py](../scripts/verify_unity_ui.py) 与 [门禁单测](../scripts/test_verify_unity_ui.py)，复用 unity_packages 的独立复制工具，不复制 Assets 整套验收脚本。新输出目录中保存复制前源码指纹、生成 fixture 后指纹、脚本指纹、完整命令/退出码、XML、日志和 Bundle。重建真实 SBP 内容并核对构建/部署哈希；六组测试采用精确过滤，拒绝失败、跳过、空/不足基线的报告及跨重载测试集合不一致。完整日志包含退出尾部检查，拒绝编译错误、普通运行时异常、Debug.LogError、断言和 JobTempAlloc/TLS 告警。故障注入仅按 suite 允许预期的精确异常链，每行至多一次，并仍要求 XML 通过 LogAssert。版本从实际 Editor 日志提取并核对工程要求；运行期间源码变化使本轮失败。

独立代码审查发现并补强了普通运行时异常日志检测和实际 Editor 版本核对。脚本单测先保存缺少模块的导入失败，随后补齐实现；新增版本用例前一次测试编辑错误造成 NameError，已修正。最终 **10 项验证门禁 Python 单测 + 2 项既有包复制单测全部通过**。开发期失败材料不计为实际行为断言通过。

审查后以最终脚本在 `C:/unity-verification/78b90386a649` 运行，Windows x64、Unity **6000.3.23f1**、Unity CLI **1.0.0**：

| Domain Reload | UI PlayMode | UI EditMode | Bundle Demo PlayMode |
| --- | --- | --- | --- |
| 开启 | 108/108 | 25/25 | 11/11 |
| 关闭 | 108/108 | 25/25 | 11/11 |

共 **288 次 Unity 执行，0 失败、0 跳过**，不把两种设置下的重复运行计成不同测试。真实 SBP 构建部署 **9 个 Bundle**；源目录 346 份包/工程文件与复制时指纹一致，脚本 3 份指纹另列。生成场景/Prefab 和重载设置由隔离 Editor 操作，生成后的指纹单列，不声称生成制品仍与源码逐字节相同。最终六份日志通过门禁，保留已知许可校验、受 LogAssert 匹配的注入异常与 Editor 退出 Mono/debugger-agent 提示；命令退出码全部为 0。

可见截图在本轮首个验证副本 `C:/unity-verification/a6342f25b7d4` 单独执行；其 346 份复制源指纹与最终矩阵相同，额外 Pipeline 1.0.0 仅用于观察，不加入生产 manifest。确认帧计数变化后，使用宿主公开操作形成稳定页、双模态和真实 Gallery 入场；本轮未重新注入原生鼠标/键盘，相关后端证据保持 Task 7 历史范围。Game View 原始 2560×1256，source=screen 等比例输出 1280×628：[稳定页](evidence/unitycore-ui/delivery-20261113/stable.png)、[双模态](evidence/unitycore-ui/delivery-20261113/modals.png)、[过渡](evidence/unitycore-ui/delivery-20261113/transition.png)。暂停时 Home 为 Visible、Gallery 为 Entering、pending=1；恢复后 depth=2、pending=0、宿主无错误，Console error=0。暂停还原 Editor 布局曾使视口比例异常，恢复最大化和重绘后重新截图；初始异常图保留为开发材料。结束时恢复 auto-tick/background override 并正常关闭本轮 Editor。

脚本的 Passed 仅表示自动矩阵，截图需独立观察与复核；其原始 summary 保持截图 Pending，最终交付摘要另汇总本轮已观察证据，不伪造脚本自动截图能力。入口见 [UIDemo README](../examples/unity/UIDemo/README.md) 与 [包 README](../packages/com.ainative.unitycore.ui/README.md)。完整六份 XML、日志、Bundle、Python 结果、红灯材料、截图和状态记录见 [归档](evidence/unitycore-ui/delivery-20261113/delivery-evidence.zip)，指纹及实际数量见 [summary.json](evidence/unitycore-ui/delivery-20261113/summary.json)。本地最终输出 `TestResults/ui-task8-final-20261113/`；首轮与红灯分别保留在 `ui-task8-first-20261113/`、`ui-task8-red-20261113/`。

最终独立审查已复核源码/脚本指纹、六份 XML、完整日志、Bundle、三张截图及状态记录，无剩余阻断项。Frame Debugger/CPU/GPU/overdraw、设备安全区/旋转、Player/IL2CPP/移动端与远端 CI 未执行，仍为独立后续验收。未夹带诊断性能优化、无关引擎测试或自动提交。

## 稳定路径分配与诊断采样（2026-11-13）

在 Task 8 基线上减少已确认的稳定路径分配，不增加公共接口、依赖或缓存层。外部销毁扫描仅在发现失效窗口时创建恢复列表；安全区写入移入 ApplySafeAreas，使捕获回调只在屏幕参数变化时构造。恢复顺序、存活检查和取消保护保持原契约。UIDemo 的完整 UI/Assets 快照改按 unscaled 时间每 0.25 秒采样，计数允许延迟一个采样周期；状态提示和 Toast 到期检查仍逐帧执行。IUI XML 注释补齐主线程、重入、取消、缓存句柄、提交后故障与 Model 替换约束：Clear 必须 await 完成且 UIResult.Succeeded=true 才允许替换 Model，正常完成的拒绝结果不是清理成功。

新增两项回归：稳定屏幕/销毁观察在预热后同帧直接执行 256 次，诊断面板 Update 在同帧采样间隙直接执行 64 次。均使用 [Unity ProfilerRecorder 的当前线程 GC.Alloc 采样](https://docs.unity.com/en-us/engine/6000.0/script-reference/unity/profiling/profilerrecorderoptions/collectonlyoncurrentthread)，先验证 new byte[1024] 阳性对照，再 Reset/Start 测量，避免无效计数器伪造通过。旧 UI 路径记录 512 个样本；旧 Demo 路径断言明确非零（原始 Count=14336 超过请求容量4096，因此只作为存在分配的红灯，不用于精确分配总数或字节量）。修复后两项均为 0；Demo 同时验证 timeScale=0 时导航后计数更新、Clear 后 SESSION 2/根页计数刷新。结论仅适用于这两个受控稳定方法路径，不代表整个 Unity 帧零 GC、完整项目性能达标或 CPU/GPU 改善幅度。

初稿 GC.GetAllocatedBytesForCurrentThread 在旧实现上返回零，未能暴露已知分配，该通过记录已明确排除。首次隔离导入时 ApiUpdater.MovedFromExtractor 子进程退出0x80000003，未执行断言；保留原始日志后，同源码重试恢复。随后 ProfilerRecorder 两项真实红灯与修复后 2/2 定向通过均归档，未将工具失败当作行为测试红灯。

独立代码审查通过后，最终使用统一脚本在 `C:/unity-verification/4fcdb2a7dcfe` 执行，实际 Windows Editor 6000.3.23f1、Unity CLI 1.0.0：

| Domain Reload | UI PlayMode | UI EditMode | Bundle Demo PlayMode |
| --- | --- | --- | --- |
| 开启 | 109/109 | 25/25 | 12/12 |
| 关闭 | 109/109 | 25/25 | 12/12 |

共 **292 次 Unity 执行，0 失败、0 跳过**；**10 项 Python 门禁单测通过**。包含原有导航、生命周期、缓存、外部销毁、安全区与真实 Bundle 回归。脚本最小基线同步升级为109/25/12，实际数量仍从 XML 汇总。346 份包/工程文件复制指纹及 3 份脚本指纹与当前源码匹配；六份最终 XML/完整日志、catalog 与9个 Bundle 的构建部署哈希已校验。日志通过编译/普通运行时错误/断言/临时分配泄漏检查；预期故障注入、许可提示和退出 Mono/debugger-agent 信息保留。

本轮未改变布局，未重新截图或注入原生输入；Task 8 图像与交互记录保持历史归档，不转记成本轮视觉结果。CPU/GPU、Frame Debugger/overdraw、设备、Player/IL2CPP、远端 CI 仍未验收。机器摘要及测量口径见 [summary.json](evidence/unitycore-ui/idle-20261113/summary.json)，完整日志、XML、测量用例/基线源码及失败材料见 [归档](evidence/unitycore-ui/idle-20261113/idle-evidence.zip)。最终独立审查已复核源码/脚本指纹、六份 XML、完整日志、Bundle、测量红灯/绿灯及文档范围，无剩余阻断项。147 个本地链接、8 份 JSON/asmdef、改动及未跟踪文本空白和 git diff --check 已检查；本地材料为 `TestResults/ui-idle-final-20261113/` 与 `ui-idle-red-20261113/`。

## Windows Player 与 IL2CPP（2026-11-16）

新增独立 [Player 验证入口](../scripts/verify_unity_ui_players.py) 与仅由 `-uiSmoke -uiResult` 启用的 [运行检查](../examples/unity/UIDemo/Assets/UIDemoPlayerSmoke.cs)。不改 UI 包运行契约，也不改变模拟内核。构建走 `AssetPlayerBuildSupport.BuildDeployedPlayer`，沿用真实 SBP 部署及 Bundle 类型保留；每轮复制三包和 UIDemo 到新的短路径，在本次生成的场景和内容上构建，拒绝已有输出。两个后端均为 Windows x64 Release、Minimal managed stripping，实际运行 Direct3D11、1280×720，Unity 6000.3.23f1 / CLI 1.0.0。Pillow 10.4.0 完整验证 PNG chunk 和像素解码，拒绝损坏、截断与纯色文件；截图仍需实际查看。

| 实际执行 | 结果 |
| --- | --- |
| Mono Release Player | 十组检查通过，运行推进778帧，退出码0 |
| IL2CPP Release Player | 十组检查通过，运行推进752帧，退出码0；实际 GameAssembly.dll、global-metadata.dat 与运行报告后端匹配 |
| Domain Reload 开 / 关 | 各109 UI PlayMode、25 UI EditMode、14真实Bundle Demo，合计296次通过，0失败/跳过 |
| Python 门禁单测 | 12项通过，含错误后端/报告/截图拒绝与禁止覆盖证据 |
| 屏幕检查 | 两后端 Home、双模态共4张实际屏幕截图；文字、共享图片、遮罩与HUD显示正常，无首轮清屏残影 |

十组 Player 检查覆盖：真实 Bundle 图片与 Model lease 身份、四层页面栈及拒绝结果、双模态阻挡及焦点恢复、同实例缓存/新会话句柄/LRU、动画取消回滚、Toast 到期与穿透、Clear 失败保留 Model 和重试、Single 场景持久化及 timeScale=0 下动画、Trim 释放最后一个图片持有者、显式 CloseAsync 后 UI 窗口/挂起/失败清理及 Assets Bundle/资源/挂起归零。两后端嵌入的 catalog 和9个 Bundle 逐文件匹配本轮构建；退出后的完整日志也通过门禁。输入使用真实 RaycastAll 命中后 ExecuteEvents，只证明 uGUI 事件路由，不是 OS 鼠标键盘或人工体验验收。退出为显式等待 CloseAsync 再 Application.Quit，不覆盖强杀进程或窗口关闭按钮路径。

首次 Mono 运行虽然十组状态断言均满足，但全局错误门禁发现 Toast 自动退出过渡与 Clear 交错产生的取消被宿主记录为失败，因此整体退出1，未发布成功摘要。新增 Editor 回归在旧宿主上复现1项失败。修复只过滤未提交、无 CleanupHandle、内部 AggregateException 非空且全部为 OperationCanceledException 的关闭错误；包含真实清理错误仍报告。修复后14项Demo预跑及最终双重载均通过，另新增自动Toast淘汰缓存时的真实清理故障用例，防止扩大吞异常范围。日志白名单仅增加该用例的精确异常链/次数，XML仍由LogAssert验证。

首次屏幕截图还暴露空场景仅有 Overlay Canvas 时，未清除的背景保留HUD动画残影。Demo场景生成器补充跟随持久宿主的纯清屏 Camera（SolidColor、cullingMask=0）；最终两后端屏幕检查未再出现残影。这是Demo宿主的背景职责，不把摄像机依赖加入UI模块。

证据见 [机器摘要](evidence/unitycore-ui/players-20261116/summary.json)、[完整归档](evidence/unitycore-ui/players-20261116/players-evidence.zip)、[独立审查](evidence/unitycore-ui/players-20261116/review.json)。归档含最终源码快照/脚本、构建/运行日志及命令、6份Editor XML、内容、Player文件哈希、4张截图，以及首次失败和红灯/绿灯材料。Player二进制保留在本机隔离目录 `C:/unity-verification/cbedb3bb803e/TestResults/ui-players/Mono|Il2Cpp`，不将大体积可执行文件加入仓库；归档中的文件哈希可核对本机产物。最终原始目录为 `TestResults/ui-players-final-20261116` 与 `TestResults/ui-player-editor-final-20261116`，首次失败为 `TestResults/ui-players-first-20261116`。

本次未执行原生输入、其他图形API/平台、强退、移动设备安全区、CPU/GPU/overdraw/压力与远端CI验收，也没有替 GridGame/PhysicsSandbox 验收 Player。历史 Editor 记录及其当时的待验范围不改写。

## Windows Player 原生输入与系统窗口关闭（2026-11-16）

在前述双后端自动smoke之外，新增只读 [UIDemoNativeProbe](../examples/unity/UIDemo/Assets/UIDemoNativeProbe.cs) 与 [原生验收脚本](../scripts/verify_unity_ui_native.py)。探针仅由 `-uiNativeResult` 启用，保存实际旧Input Manager的鼠标/Return/方向键、焦点、页面/模态/资源与退出回调；不调用UI命令、CloseAsync或Application.Quit。脚本复用隔离双后端构建，对照源码及Player文件哈希后启动进程，等待外部操作，拒绝已有证据目录。运行门禁验证十个有序稳定状态、前台原始输入、退出前活会话及退出回调后的资源归零，再检查完整日志/实际Unity版本和进程退出码。

本轮实际环境仍为Unity 6000.3.23f1、Windows x64 Release、Minimal stripping、Direct3D11、StandaloneInputModule。两个后端均由用户直接点击鼠标，CUA发送原生Return和Right，并用Alt+F4触发系统窗口关闭；没有使用ExecuteEvents或直接调用UI API代替本轮输入。用户分别确认Gallery、Session 2/Gallery，IL2CPP还确认Duplicate显示AlreadyOpen；原始输入与有序状态记录逐项交叉核对。每个后端的键盘过程是第三页→第一模态→第二模态→返回第一模态→方向键选Back→返回第三页，并恢复Modal按钮焦点。鼠标Clear后切换到Session 2，Single加载Other后Next进入Gallery，Core与资源保持运行。

| 实际执行 | Mono | IL2CPP |
| --- | --- | --- |
| 有序稳定状态检查点 | 10通过 | 10通过 |
| 原始前台输入事件 | MouseDown 7、Return 5、Right 2 | MouseDown 5、Return 5、Right 2 |
| 系统窗口关闭 | CUA原生Alt+F4，窗口消失，退出0 | CUA原生Alt+F4，窗口消失，退出0 |
| 退出前 | Session 2 / Other，3窗口、4Bundle、4资源，Core运行，Model未释放 | 同左 |
| application-quit回调采样 | frame43105，仍持有活会话 | frame13664，仍持有活会话 |
| destroy回调采样 | 同frame43105，Core停止、Model已释放、窗口/页面/模态/缓存/挂起/失败清理/Bundle/资源归零 | 同frame13664，全部归零 |

原始鼠标计数包含焦点点击、重复点击，不等于业务操作次数。退出观察只声明本轮两个实际进程的结果，不由DefaultExecutionOrder推导所有Unity退出回调的固定顺序。探针不延迟、不拦截退出，也没有提前清空会话；退出前活状态与销毁后归零共同证明本次正常窗口关闭完成资源收尾。

Windows Graphics Capture连续返回 `FrameArrived timed out` / `window capture timed out`；用户确认桌面已解锁且窗口可见后仍失败。可访问性读取可用但偶有超时，标题栏关闭按钮的AX点击又返回 `coordinate input geometry is unavailable`。因此鼠标改由用户执行，键盘由CUA逐步发送并核对记录，关闭改用Alt+F4。**本轮未取得原生交互截图，未验收标题栏关闭按钮点击，不把自动smoke截图冒充原生会话截图。** 故障与操作来源保存在capture-limitation.json和各后端actions.json中；也没有向Unity或其他第三方发送日志。

加入探针后重新构建两个Release后端，各十组自动smoke仍通过。最终Python门禁13项通过，新增原生门禁拒绝状态序列不足、后端不符、无前台输入、缺失模态Back焦点观察、提前清空会话、漏退出回调与资源泄漏等假成功材料。本轮未修改UI/Core/Assets运行实现；上一轮296次Editor结果保留历史范围，没有把它计为本次重跑。新构建与原生结果使用350份源码指纹，实际二进制位于 `C:/unity-verification/f3ac0bdffd02/TestResults/ui-players/Mono|Il2Cpp`。

证据见 [机器摘要](evidence/unitycore-ui/native-20261116/summary.json)、[归档](evidence/unitycore-ui/native-20261116/native-evidence.zip)、[独立审查](evidence/unitycore-ui/native-20261116/review.json)。归档保留原始journal、用户确认和CUA操作对应帧、实际命令/进程退出码、完整构建和Player日志、源码/脚本快照与Player文件哈希；自动smoke的4张截图单列在build目录，限定为自动smoke范围。原始材料目录为 `TestResults/ui-native-first-20261116`，该名称保留了首次启动时的路径，不代表另有未归档的成功重跑。

后续未验范围仍有标题栏关闭按钮、强杀、退出动画/加载中关闭、失焦/多显示器专项、其他输入后端/图形API、设备/移动端、性能与远端CI。

## 原生交互截图与标题栏关闭（2026-11-16）

在前轮Alt+F4验收之外，补齐Mono和IL2CPP的实际原生交互截图与标题栏×关闭。Windows Graphics Capture仍超时，AX按钮点击仍报 `coordinate input geometry is unavailable`；本轮没有修复或绕过这些OS工具接口。新增 `--screenshots` / `-uiNativeScreenshots` 开关，由[只读探针](../examples/unity/UIDemo/Assets/UIDemoNativeProbe.cs)在真实输入后的状态稳定0.35秒、无挂起UI操作时等待帧末，以ScreenCapture捕获实际Player客户端视口。PNG和同一帧的会话、页面、模态、焦点、资源计数一起写入JSONL；不导航、不发送合成UI事件、不调用CloseAsync或Application.Quit，不拦截退出。截图不是Camera RenderTexture，也不是早先smoke图片；**不含Windows标题栏**。

[验证脚本](../scripts/verify_unity_ui_native.py)除原来的十个有序状态外，新增Home、Gallery、双模态、Session 2/Other/Gallery四个有序视觉检查点。逐图检查文件名与帧号一致、1280×720、运行稳定、PNG完整性/像素解码和非纯色；交互检查点须发生在实际输入帧之后。主代理和独立Reviewer另外查看每后端四张代表图，不能只凭PNG存在或哈希一致判断视觉正确。截图记录是显式启用的验收开销，不作为性能采样。

| 实际执行 | Mono | IL2CPP |
| --- | --- | --- |
| 正式原生交互门禁 | 10检查点通过 | 10检查点通过 |
| 视觉检查点 | 4通过 | 4通过 |
| 实时PNG完整解码 | 19张通过，4张代表图逐张查看 | 15张通过，4张代表图逐张查看 |
| 前台原始输入 | MouseDown 9、Return 5、Right 2 | MouseDown 6、Return 5、Right 2 |
| 标题栏关闭 | 用户按明确指令点击右上角×后确认关闭；进程退出0 | 用户明确确认点击标题栏×；进程退出0 |
| 关闭前 | Session 2/Other，3窗口、4Bundle、4资源，Core运行，Model未释放 | 同左 |
| 退出收尾 | quit/destroy在frame17176，destroy资源归零、Core停止、Model已释放 | quit/destroy在frame24205，destroy资源归零、Core停止、Model已释放 |

键盘操作由CUA逐步发送Return/Right，按实际Input帧号与稳定状态核对；鼠标由用户执行Duplicate、Next、Clear、Scene、Next，并确认Gallery/Session 2的显示。原始鼠标次数包含额外焦点点击，不能当成业务操作数量。两次正式会话都明确要求等待最终画面截图保存后点击标题栏×，**没有发送Alt+F4**。退出方式由明确操作指令和用户确认佐证，再与正常进程退出及quit→destroy日志交叉核对；不是从客户端截图、退出码或Unity回调单独推断鼠标点了×。

本轮重新构建Unity 6000.3.23f1 Windows x64 Release（Mono/IL2CPP、Minimal stripping、Direct3D11），两个后端各十组自动smoke通过，14项Python门禁单测通过。源码350份，脚本6份指纹；新二进制位于 `C:/unity-verification/987a16014cf3/TestResults/ui-players/Mono|Il2Cpp`。没有更改UI/Core/Assets运行契约，也未重跑历史296次Editor矩阵。正式原始材料在 `TestResults/ui-native-capture-final-20261116`。

起初另复用旧Player诊断工具，用户点×后进程退出0、资源归零，但只执行Home状态，完整门禁正确拒绝为1/10，不计入正式通过次数。该材料位于 `TestResults/ui-native-visual-20261116/Mono`，归档保留其失败结果、工具错误与启动时脚本快照。Mono正式交互期间Computer Use两次报告Esc停止；每次均停止操作并在后续用户输入后恢复，没有把工具中断算成产品故障或另一次成功执行。

证据见[机器摘要](evidence/unitycore-ui/capture-20261116/summary.json)、[完整归档](evidence/unitycore-ui/capture-20261116/capture-evidence.zip)、[独立复核](evidence/unitycore-ui/capture-20261116/review.json)。代表图另列：[Mono Home](evidence/unitycore-ui/capture-20261116/Mono-home.png)、[Gallery](evidence/unitycore-ui/capture-20261116/Mono-gallery.png)、[双模态](evidence/unitycore-ui/capture-20261116/Mono-modals.png)、[Session 2](evidence/unitycore-ui/capture-20261116/Mono-session2.png)；[IL2CPP Home](evidence/unitycore-ui/capture-20261116/Il2Cpp-home.png)、[Gallery](evidence/unitycore-ui/capture-20261116/Il2Cpp-gallery.png)、[双模态](evidence/unitycore-ui/capture-20261116/Il2Cpp-modals.png)、[Session 2](evidence/unitycore-ui/capture-20261116/Il2Cpp-session2.png)。归档含全部34张原生PNG、用户确认/键盘帧、完整日志、命令/退出码、构建内容及源/脚本/Player文件哈希；自动smoke截图仍单列build目录。

本轮完成的是用户实际操作标题栏×后的正常退出，以及原生交互后的Player客户端绘制验收。OS标题栏图像/自动点击工具恢复、强杀、加载或退出动画中关闭、其他输入后端/平台、性能与远端CI仍不在此证据范围。

## 加载与退出动画中关闭（2026-11-16）

Mono/IL2CPP各完成一次Gallery等待加载结果交付时关闭、一次Back退出动画中关闭，共四场通过。用户实际点击标题栏×并确认，关闭请求命中目标挂起阶段；进程均退出0，最终Core停止、Model释放、UI/Assets资源及所有权归零，Console与Assets内部Errors无异常。主代理负责实现与执行，独立Reviewer负责代码和最终证据复核。复现命令见[UIDemo说明](../examples/unity/UIDemo/README.md#加载与退出动画中关闭-player)。

| 后端与场景 | 关闭请求帧 | 请求瞬间 | 最终结果 |
| --- | --- | --- | --- |
| Mono 加载 | 2001 | Opening，UI pending 1、Assets pending 1 | 迟到交付被隔离并回收，通过 |
| Mono 退出动画 | 9762 | Exiting，UI pending 1，alpha 0.472765 | 取消与清理通过 |
| IL2CPP 加载 | 10231 | Opening，UI pending 1、Assets pending 1 | 迟到交付被隔离并回收，通过 |
| IL2CPP 手动Back退出动画 | 12723 | Exiting，UI pending 1，alpha 0.725887 | 取消与清理通过 |

新增独立 `UIDemo.Acceptance` 程序集，只有显式命令行参数才启用。常规Demo不依赖此程序集；Assets只向此验收程序集开放既有internal backend工厂，未增加产品公共API。加载夹具使用真实BundleBackend，原生资源完成后暂缓交付，保持UI Opening与Assets pending。关闭后在OnDestroy先观察Core已退休，再交付迟到成功结果并检查回收；不假设OnApplicationQuit回调顺序，不声称物理磁盘仍在读取或原生异步IO能被立即取消。

退出动画复用Gallery的真实转场，仅验收配置将进出时长设为120秒。前三场使用R3夹具（Mono的Back由宿主API触发）；最后IL2CPP场使用手动Back夹具，Gallery就绪后等待用户点击Back，约1秒后可点击×，runner最多等待3600秒。最后一次用Computer Use确认目标窗口并置前。用户反馈“页面似乎卡住、顶部按钮提示Host operation in progress”，记录显示帧与alpha持续推进，这是长转场期间页面禁用交互、宿主拒绝并发命令的预期行为；正常Demo仍为0.35秒。准备步骤使用宿主API，不能计作新的完整原生鼠标键盘导航验收。

`wantsToQuit` 始终返回true，只记录实际关闭请求瞬间；不调用Application.Quit或提前CloseAsync。门禁要求此时仍为Opening/Exiting并有挂起操作；退出动画须观察到alpha下降且关闭时仍介于0和1。帧末截图与阶段状态一起归档。退出后检查Core、Model、窗口、资源、挂起操作和所有权，并拒绝Console异常及Assets内部Errors非空。外部点击×由操作指令和用户确认佐证，客户端截图不包含标题栏。

实际修复两处错误分类：Back退出被关闭打断时，宿主原先将未提交的纯取消UIOperationException误记为失败，现仅在宿主关闭/Core不再运行、未提交、无清理句柄且内部异常全部是取消时识别为正常收尾，混合清理错误仍报告。Assets则在backend成功返回后先检查Runtime.Closed，退休结果按取消隔离，避免Unity退出销毁迟到对象后误报AssetTypeMismatch；backend本身抛出的真实错误仍保留。新增宿主回归及退休销毁结果、运行中空结果、退休后台故障三项Assets回归。

最终有效自动验证为：UIDemo双Domain Reload各25 UI EditMode、109 UI PlayMode、15 Bundle Demo，共298次通过；正确AssetsDemo宿主使用SBP/URP Forward，Core与Assets双重载各58 PlayMode、110 EditMode，共336次通过；均无失败/跳过。18项Python门禁单测通过。R3及手动Back两个源码快照均重新构建Mono/IL2CPP Release，各后端十组自动smoke通过。环境为Unity 6000.3.23f1、Windows x64、Direct3D11、Minimal stripping。

UIDemo的359份源码指纹与R3构建一致；最终手动Back版本仅Player验收探针去掉自动Back，产品运行代码及Editor测试未再改动，不能称四场使用完全相同源码。AssetsDemo另核对322份源码，归档10份验证脚本。R3二进制位于 `C:/unity-verification/e68a09a68c8d/TestResults/ui-players`，手动Back版本位于 `C:/unity-verification/42e919c8a1cd/TestResults/ui-players`；各有Mono 151、IL2CPP 363个文件哈希，二进制本体未打入证据ZIP。

失败和恢复材料全部保留，不纳入通过数：首次Mono加载暴露夹具的quit回调顺序假设；第二次捕获上述AssetTypeMismatch；最初宿主取消回归为红灯。最终UI矩阵的on-demo曾退出1、CLI返回TEST_RUN_ERROR且无XML，原因未确定，保留日志后仅重试未完成项并继续剩余矩阵，298计数来自有效XML。曾误在UIDemo运行Assets全EditMode，产生14失败、6跳过（缺AssetsDemo资源/URP夹具等），该轮不算通过，改用正确宿主的336次结果。两次自动Back的IL2CPP会话因900秒未收到关闭而被runner终止，也不算正常关闭；因此最终改为手动Back，避免消息往返消耗动画窗口。

证据见[机器摘要](evidence/unitycore-ui/shutdown-20261116/summary.json)、[完整归档](evidence/unitycore-ui/shutdown-20261116/shutdown-evidence.zip)、[独立复核](evidence/unitycore-ui/shutdown-20261116/review.json)。四张正式视口截图：[Mono加载](evidence/unitycore-ui/shutdown-20261116/Mono-loading.png)、[Mono退出](evidence/unitycore-ui/shutdown-20261116/Mono-exiting.png)、[IL2CPP加载](evidence/unitycore-ui/shutdown-20261116/Il2Cpp-loading.png)、[IL2CPP退出](evidence/unitycore-ui/shutdown-20261116/Il2Cpp-exiting.png)。归档包含用户原话、逐帧状态、完整日志/退出码、有效和失败测试XML、两种夹具与历史失败源码、构建内容及哈希。

本轮限定受控加载交付边界与正常系统窗口关闭。强杀成功清理、物理IO在途专项、其他平台/输入后端、性能与远端CI未验收；OS截图/自动标题栏按钮工具也未据此视为修复。

## UI 审查修复（2026-11-16）

收敛固定视觉层级：允许继承排序的子 Canvas，拒绝 overrideSorting，并复查激活回调及打开提交前的候选；输入过滤不能代替视觉层级保证。UIOptions 开放分辨率、宽高匹配与遮罩颜色配置，保留原默认行为并校验有限值及取值范围。实施计划 Task 1/2 按已有验收更新状态，不重写历史结果。

本轮新增配置合法性、配置传递到实际 Canvas/遮罩、子 Canvas 继承排序、激活改写排序失败回滚及重新打开、缓存重开失败后旧句柄释放、Back 恢复 Covered 页面失败后重试回归。独立代码审查已通过，审查建议的缓存与 Covered 恢复回归已补齐。非法排序的缓存候选在失败后仍为冷隐藏，旧句柄可以 Release；框架不擅自修复作者配置。之前的 Player/Editor 证据不计为本轮重跑。

Windows Editor / Unity 6000.3.23f1 / Unity CLI 1.0.0，在独立 UIDemo 副本重新生成内容、构建并部署真实 SBP Bundle，运行双 Domain Reload 矩阵：

| Domain Reload | UI EditMode | UI PlayMode | Bundle Demo |
| --- | --- | --- | --- |
| 开启 | 36/36 | 113/113 | 15/15 |
| 关闭 | 36/36 | 113/113 | 15/15 |

共 **328 次执行通过，0 失败、0 跳过**；436 个源文件与配置指纹和当前工作区完全一致。另有 18 项 Python 验证门禁单测通过；差分空白及本轮文档本地链接目标检查通过。原始最终目录为 `TestResults/ui-review-fixes-verified-20261116`，复现入口仍为 `python scripts/verify_unity_ui.py <全新输出目录>`。

预跑使用未补最后两条回归的快照，6 份 XML 全通过，但期间工作区修改被源码指纹门禁正确拒绝，不能作为最终成功。首轮完整快照的 113 项 PlayMode 有 1 项失败：Back 测试在首次拒绝、栈/焦点/pending 恢复断言通过后，仅修改 inactive Canvas 属性未能使再次 Back 通过；改为移除测试注入的非法组件后，12 项输入专项及最终矩阵通过。没有将尚未单独定位的 Unity 属性行为认定为内部根因，也没有放宽生产排序校验。失败、专项和预跑材料均保留且不计入328次。

证据见[机器摘要](evidence/unitycore-ui/review-fixes-20261116/summary.json)、[完整归档](evidence/unitycore-ui/review-fixes-20261116/review-fixes-evidence.zip)及[独立复核](evidence/unitycore-ui/review-fixes-20261116/review.json)。本轮验证为契约、导航/输入路由和真实 Bundle 集成；未重跑 Player/IL2CPP、原生交互截图、设备安全区、性能或远端 CI。已有使用 overrideSorting 的窗口需调整为继承排序后接入，不能沿用之前仅告警的行为。

## 合并后 Windows Player 回归（2026-11-17）

以PR #30合并后的 `main` 提交 `3c886701ef39073af1e2a3d25522eb5a8e0d059b` 为基线，在全新隔离工程重新生成场景、构建真实SBP Bundle并构建Windows x64 Release Mono/IL2CPP。UI/Core/Assets三个产品包未修改；本轮扩展UIDemo的显式验收夹具、宿主内部显示选项注入和Python报告门禁。普通交互Demo仍使用原默认配置，不能将带新增夹具的Player描述为未经任何变更的合并提交二进制。

| 验收项 | Mono | IL2CPP |
| --- | --- | --- |
| 自动Player检查 | 14/14通过 | 14/14通过 |
| 运行帧数 | 967 | 966 |
| 首页/双模态截图 | 2张，完整解码并逐张查看 | 2张，完整解码并逐张查看 |
| 最终UI窗口/挂起/清理失败 | 全部0 | 全部0 |
| 最终Assets Bundle/资源/挂起/所有权/内部错误 | 全部0 | 全部0 |
| 进程退出码 | 0 | 0 |

在原十组基础检查上，增加四组：非默认显示配置传递到实际CanvasScaler/遮罩、继承排序Canvas的模态输入阻挡、缓存窗口OnEnable产生非法排序时的拒绝及旧句柄释放、Covered页恢复非法排序时的Back回滚与修正后重试。显示配置为2000×1125、宽高匹配0.25、遮罩RGBA(0.1,0.2,0.3,0.65)，结果保存实际数值并由Python独立校验；窗口栈、焦点、冷隐藏、pending与清理失败状态都有运行时断言。新实例首次激活拒绝专项仍为上一轮Editor覆盖，本轮未新增对应Player断言。

正式结果共28组Player检查通过，另18项Python门禁单测通过；环境为Unity 6000.3.23f1、Unity CLI 1.0.1、Direct3D11、Minimal stripping。440份源码/配置、4份验证脚本指纹在执行前后匹配；两个Player文件清单分别151/363份，部署Bundle与本次SBP构建一致。最终原始目录 `TestResults/ui-players-merged-accepted-20261117`，二进制保留于 `C:/unity-verification/eff48e276fe7/TestResults/ui-players/Mono|Il2Cpp`，归档保存文件哈希，不包含可执行文件本体。复现入口见[UIDemo说明](../examples/unity/UIDemo/README.md#windows-player-验收入口)。

失败与中间材料分别保留，不计入正式28组：首次 `ui-players-merged-20261117` 因两个新增夹具文件缺少namespace闭合括号导致编译失败，没有运行Player；`ui-players-merged-final-20261117` 两后端自动检查均通过，但1600×900验收配置裁切固定布局HUD，截图复核后被替代，保留其原始Passed报告；`ui-players-merged-verified-20261117` 的Mono在缓存排序专项失败，前一专项已有Canvas未可靠移除，导致重复AddComponent和OnEnable空引用。最终夹具通过ReleaseAsync完全释放上一专项实例并确认Unity null，再创建新实例进行注入，未放宽产品断言，也未认定尚未单独定位的Unity内部销毁根因。

证据见[机器摘要](evidence/unitycore-ui/merged-players-20261117/summary.json)、[完整归档](evidence/unitycore-ui/merged-players-20261117/merged-players-evidence.zip)与[独立复核](evidence/unitycore-ui/merged-players-20261117/review.json)。四张最终截图：[Mono首页](evidence/unitycore-ui/merged-players-20261117/Mono-home.png)、[Mono双模态](evidence/unitycore-ui/merged-players-20261117/Mono-modals.png)、[IL2CPP首页](evidence/unitycore-ui/merged-players-20261117/Il2Cpp-home.png)、[IL2CPP双模态](evidence/unitycore-ui/merged-players-20261117/Il2Cpp-modals.png)。归档包含最终及失败/中间轮命令、完整日志、报告、截图、源码/脚本快照及内容哈希。

本轮事件由RaycastAll/ExecuteEvents触发，退出为CloseAsync后Application.Quit；没有重跑OS原生鼠标键盘、标题栏关闭、加载/退出动画中关闭、完整Editor矩阵、性能、设备安全区或远端Unity CI。上述历史证据保留原版本与范围，不并入本轮通过数。
