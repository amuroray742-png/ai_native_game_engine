# UnityCore UI 契约与实施状态

## 当前状态

本模块按 [设计记录](unitycore-ui-design.md) 与 [实施计划](superpowers/plans/2026-11-12-unitycore-ui.md) 开发，已有基础窗口所有权及生命周期，并接入页面栈、模态顺序、事务回滚、输入/焦点策略、有界缓存、LRU、Trim、旧会话隔离、安全区更新和内置过渡；实际验证范围见下文。Task 6 已补齐 Clear、迟到任务隔离、外部销毁恢复与组件诊断回归；Task 7 已补齐独立交互 Demo 与真实 Bundle 集成；Task 8 的统一隔离验证脚本与最终交付审查仍待完成。

包路径为 `packages/com.ainative.unitycore.ui`，命名空间为 `AiNative.UnityCore.UI`。独立工程为 [UIDemo](../examples/unity/UIDemo/README.md)，包含可点击场景、真实 Bundle 内容构建入口和集成测试。

## 边界与依赖

模块单向依赖 UnityCore、Assets、UniTask 2.5.11 和 uGUI 2.0.0，使用 Unity 6000.3.23f1。它是应用表现服务，不引用模拟内核、Hosting 或游戏示例，不推进权威 Tick。宿主提供 EventSystem 和显式 Presenter 工厂，不增加全局单例、反射扫描或依赖注入容器。

`UIPresenter` 是连接业务与窗口实例生命周期的必要抽象，提供同步 `Bind()`、`Resume()`、`Suspend()`、`Unbind()`。业务依赖由工厂闭包显式传入，不经全局事件总线查找。

## 已实现公共契约

- `UIOptions(int closedCacheCapacity = 8, int maxPageDepth = 16)`：容量非负，页面深度为正；参考分辨率 1920×1080、匹配值 0.5、遮罩黑色 alpha 0.5。
- `UITransitionOptions(float durationSeconds, bool fade, Vector2 enterOffset, float enterScale)`：时长有限非负、偏移有限、缩放有限且为正；`default` 表示无过渡。
- `UIWindowDefinition<TView>(string address, UIRole role, Func<TView, IAssets, UIPresenter> presenterFactory, UICachePolicy cachePolicy = UICachePolicy.Release, bool opaqueFullscreen = false, bool allowHudInput = false, UITransitionOptions transition = default)`，其中 `TView : UIView`。
- `UIRole` 固定为 Hud、Page、Modal、Overlay。只有 Page 可以开启 OpaqueFullscreen 或 AllowHudInput；Overlay 的目标契约为永不交互。
- `UIModule(UIOptions options, EventSystem eventSystem)` 及 `Register<TView>(UIWindowDefinition<TView> definition)`：同一 View 类型不能重复登记，运行中禁止登记新类型。
- `UIView` 显式序列化根 CanvasGroup、SafeArea、Visual 和可选默认 Selectable；绑定前校验根 CanvasGroup 位于 View 根且 enabled，子 CanvasGroup 不得 ignoreParentGroups；SafeArea 必须是 View 的直接子节点，Visual 为 SafeArea 的独立后代。
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
