# Unity UI 模块实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

用户已确认并存档 `docs/unitycore-ui-design.md`，现明确要求按照方案开始实施。本计划将设计转为 UI 包、测试、独立 UIDemo 和隔离验证入口；不再重复框架选型。当前工作区只有未跟踪的设计文档，保留其内容。主 Agent 顺序实现并集成，完成后独立只读审查；不自动提交或推送，不创建 worktree。

**Goal:** 实现具备单实例窗口、轻量 MVP、页面返回栈、模态输入、LRU 缓存和内置过渡的 uGUI 应用模块，并以真实 Assets 所有权及 Windows Editor 双 Domain Reload 验证。

**Architecture:** 新包 `com.ainative.unitycore.ui` 单向依赖 UnityCore 与 Assets。每轮运行独占 Root、scope 与记录，以导航事务和实例/展示会话句柄隔离异步操作；宿主显式提供 EventSystem 与 Presenter 工厂。

**Tech Stack:** Unity 6000.3.23f1、C# 9、UniTask 2.5.11、uGUI 2.0.0、现有 Assets 构建流程。

**Spec:** `docs/unitycore-ui-design.md`。

### 2026-11-13 生命周期优先收敛

先修复 Task 2 基础路径中的强退解绑、激活后取消和独立清理进度；测试改由真实 Core 注册、运行、关闭与重启，并补实际进出 Play。该轮独立审查还要求保留回调与延迟清理的双重错误。实际验收见 [当前状态](../../unitycore-ui.md#基础生命周期收敛2026-11-13)。此轮不将 Task 2 的所有边界或 Task 3–8 标记完成；后续 UI 功能以生命周期验证通过为前置条件。

## Global Constraints

- 首期完整包含缓存、返回栈和动画，不降格为只有 Open/Close 的骨架。
- 默认 HUD < Page < Modal < 非交互 Overlay；不新增上层交互式 HUD。
- 单根共享 Canvas、独立 DontDestroyOnLoad Root；不扩大 ICore/Assets API。
- 仅主线程；无线程检查、自动切线程、反射扫描、DI 容器、全局事件总线或新增动画库。
- 框架不依赖 TMP/Input System；UIDemo 显式 StandaloneInputModule。
- 文档中文，标识符英文；仅恢复/隔离/释放边界捕获；不修改历史证据。
- 相对 file: 包引用、OpenUPM UniTask；禁止 Resources 回退、阻塞等待、全局卸载。
- 首期验收 Windows Editor；Player/IL2CPP/移动端和人工性能分析未实际执行时明确列为未验证。
- 执行时将已批准计划落盘 `docs/superpowers/plans/2026-11-12-unitycore-ui.md`；当前计划模式仅编辑本计划文件。

## Review Focus

1. 缓存重开后旧句柄不能关闭/释放新会话；覆盖 owner、generation、instance、session 四维身份。
2. Bind/Resume/OnEnable/焦点/通知中的重入必须拒绝；异步等待者正常续执行不被误判。
3. 打开取消且回滚失败必须交付清理句柄；解绑失败不能被已释放 scope 掩盖。
4. Shutdown 不等待停止派发的动画；强退、迟到完成和 Restart 不能触碰新上下文。
5. 透明页链、双模态和恢复焦点同时存在时，绘制集合、交互集合与导航历史不能混淆。

---

## 文件职责与公共接口收敛

根目录 `packages/com.ainative.unitycore.ui/`：
- `Runtime/IUI.cs`：宿主操作入口。
- `Runtime/UIModule.cs`：IModule/IAsyncShutdownModule 装配和每轮上下文生命周期。
- `Runtime/UIRun.cs`：该轮身份、请求、记录、提交保护和完成源；internal。
- `Runtime/UIWindowDefinition.cs`、`UIPresenter.cs`：类型登记及显式业务装配。
- `Runtime/UIHandle.cs`、`UIResult.cs`、`UIOperationException.cs`：身份、正常拒绝和可恢复故障载荷。
- `Runtime/UIView.cs`：窗口根 CanvasGroup、正文 SafeArea、独立 Visual、可选默认 Selectable 的显式引用。
- `Runtime/UIRoot.cs`：唯一 Canvas、层容器、staging、安全区、遮罩、输入和焦点。
- `Runtime/UIWindowRecord.cs`：实例 scope、Presenter、会话、状态及解绑/释放进度；internal。
- `Runtime/UINavigation.cs`：页面/模态事务和稳定可见集合；internal。
- `Runtime/UIWindowCache.cs`：已关闭实例 LRU，淘汰等待实际释放；internal。
- `Runtime/UITransition.cs`：有限非缩放时间过渡与复位；internal 执行器及小型公开配置。
- `Runtime/UIDiagnostics.cs`：不可变状态快照和渲染风险信息，不做 native 批次预测。
- `Tests/EditMode/`、`Tests/PlayMode/`：对应测试及 asmdef；内部测试访问沿用既有方式，不为测试扩大 API。

公开接口推荐固定如下（实现不得另造第二条页面导航入口）：
- `UIModule(UIOptions options, EventSystem eventSystem)`，`void Register<TView>(UIWindowDefinition<TView> definition) where TView : UIView`；登记仅停止时。
- `UIWindowDefinition<TView>(string address, UIRole role, Func<TView, IAssets, UIPresenter> presenterFactory, UICachePolicy cachePolicy = UICachePolicy.Release, bool opaqueFullscreen = false, bool allowHudInput = false, UITransitionOptions transition = default)`；role 唯一决定固定层，无冗余任意 layer 参数。HUD 输入明确采用栈顶页面定义的 `AllowHudInput`，默认 false，无页面时允许。`opaqueFullscreen/allowHudInput` 仅 Page 可设置为 true；Overlay 永不接收输入。
- `UIPresenter` 为一个必要跨模块生命周期基类：`Bind()/Resume()/Suspend()/Unbind()` 四个同步抽象方法；不再给每个业务 Presenter 造接口。
- `IUI.OpenAsync<TView>(CancellationToken = default)`、`PushAsync<TView>(CancellationToken = default)` 返回 `UniTask<UIOpenResult<TView>>`，约束 TView : UIView。
- `UIOpenResult<TView>` 提供 `Error`、`Succeeded`；成功结果访问 `View` 和 `Handle`，失败访问成功载荷明确失败，不能返回假对象。
- `CloseAsync(UIHandle)`、`ReleaseAsync(UIHandle)`、`BackAsync()`、`TrimAsync()` 返回 `UniTask<UIResult>`；`BringToFront(UIHandle)` 返回 UIResult。
- `bool TryGet<TView>(out TView view, out UIHandle handle)`：可见且稳定（含可绘制的 Covered 页面），不代表可交互；隐藏/过渡/故障不返回。
- `bool HasModal`、`event Action<bool> ModalStateChanged`、`UIDiagnostics GetDiagnostics()`；事件属于模块实例，Shut 清除订阅并写明重启需重新订阅。
- 错误码集中为 UI 专属：None/NotRunning/NotRegistered/AlreadyOpen/InvalidRole/InvalidHandle/NotTopPage/NoBackTarget/NavigationBusy/ReentrantCall/StackLimitReached/WindowBusy；非法配置为参数异常。
- `UIOperationException` 保留 InnerException、`Committed`、可选 Handle 和 CleanupHandle。展示身份与清理身份分离：View 已销毁时不伪造有效展示句柄，但必要解绑未成功的故障记录仍保留 CleanupHandle（同一 UIHandle 类型、内部 cleanup-only 标记），仅供 Release，直到 Suspend/Unbind/scope 关闭均成功。每次失败后的 Release 重试创建新完成源，成功清理步骤不重复执行。取消若伴随清理失败报告组合故障，不丢失取消原因。
- 增加 `UniTask<UIResult> ClearAsync()` 作为运行中全量会话清理入口：接受后停收其他请求，取消并等待已进入操作，跳过过渡，清空页面/模态历史并释放所有可见、栈内、缓存和失败记录；失败继续其他清理、保持停收状态，允许重复 Clear 重试。成功后创建新的本轮 UI 子 scope、恢复接收并使旧句柄失效；不销毁 EventSystem、不退出 Core，不清除窗口定义。宿主只有等待 Clear 成功后才能释放或替换业务 Model。Shutdown 复用清理流程但不恢复接收。
- 句柄操作矩阵：普通展示句柄可 Close，已关闭且未重开可 Release；Visible/Covered/栈内 Release 返回 WindowBusy；Page BringToFront 返回 InvalidRole，非顶 Page Close 返回 NotTopPage；cleanup-only 句柄除 Release 外一律 InvalidHandle。HUD 有页面时仅当栈顶 AllowHudInput=true 且无模态才可交互，页面透明性本身不放行 HUD。
- UIOptions 默认 ClosedCacheCapacity=8、MaxPageDepth=16，均可显式配置；0 缓存表示释放，栈深须正数。Scaler 推荐 1920×1080、MatchWidthOrHeight=0.5，遮罩黑色 alpha=0.5；过渡默认 duration=0，Demo 显式 0.18 秒。`UITransitionOptions(float durationSeconds, bool fade, Vector2 enterOffset, float enterScale)`：duration 有限非负、offset 有限、scale 有限正数，退出反向应用同一配置；default 为无过渡，不将 default scale=0 应用到 Visual。
- UIView 通过 SerializeField 显式引用根 CanvasGroup、RectTransform SafeArea/Visual 和可选默认 Selectable；Editor fixture 经 SerializedObject 装配，不增加运行时自动绑定。SafeArea/Visual 必须为独立的后代容器；背景可放安全区之外。

已核实的复用约束：ICore 没有根对象接口（具体 Core 虽有 RootGameObject，也不向下转型）；Assets `CreateScope/InstantiateAsync/CloseAsync` 足够。TimeMachine `RegisterUpdate(Action<float>, string, int)` 返回可 Dispose 票据，但提供的是 scaled delta；过渡回调必须显式读取 `Time.unscaledDeltaTime`，不更改公共时钟语义。

## Task 1：公共契约、包与最小测试宿主

**Files:** 新包 package.json、Runtime/AiNative.UnityCore.UI.asmdef、上述公开契约文件、Tests/EditMode/UIContractTests.cs；UIDemo 的 manifest、ProjectVersion、最小测试设置。修改总设计与规范的 UI 边界说明，新增 `docs/unitycore-ui.md` 初始契约。

**Interfaces:** 产出上节全部公共声明；此任务不伪装 Open 已可用，未实现流程不得返回成功。

- [ ] 写 `UIContractTests`：重复定义拒绝、Page-only opaque/AllowHudInput、非法容量/时长/NaN、固定角色映射、失败结果不能读取成功载荷；例如 `Assert.Throws<ArgumentOutOfRangeException>(() => new UIOptions(closedCacheCapacity: -1))`。
- [ ] 建立相对依赖和测试 asmdef：manifest 显式引用 Core/Assets/UI 三包 `file:../../../../packages/<包名>`、uGUI 2.0.0、OpenUPM UniTask 2.5.11，testables 包含 UI（Core 回归按需增加）。`activeInputHandler=0`，保留 Horizontal/Vertical/Submit/Cancel 轴；不复制 URP/InputSystem 等无用途依赖。在隔离 UIDemo 中编译并运行 EditMode，保存首次缺少类型导致的失败/编译证据；不把编译错误当作已运行断言。
- [ ] 实现结果/句柄/定义/选项的最小契约，UIHandle 内部存 owner、generation、instance、session；合法配置在登记时校验。
- [ ] 重跑 UIContractTests，要求全部通过，更新正式文档的确切签名；不提交 Git。

## Task 2：运行上下文、Root 与窗口资源闭环

**Files:** Runtime/UIModule.cs、UIRun.cs、UIRoot.cs、UIWindowRecord.cs、UIView.cs；Tests/PlayMode/UIOwnershipTests.cs、UILifecycleTests.cs、测试资源工厂。

**Consumes:** ICore.RegisterInterface/GetModule、IAssets.CreateScope、AssetScope.InstantiateAsync/CloseAsync；Task 1 定义。
**Produces:** IUI 的无动画 HUD/Overlay Open/Close/Release 和真实重启/退出；internal 窗口记录及单一提交保护边界，后续导航复用。

- [ ] 写失败测试 `HudOpenOwnsOneScopeAndCloseReleaseReturnsToBaseline`、`InactivePrefabBindsBeforeEnable`、`DuplicateTypeIsRejectedWhileOpening`、`AssetsRegistersBeforeUiAndShutsAfterUi`：实例存活时资源非零，释放后真实 Unity null 且登记回基线。
- [ ] 跑目标 PlayMode，保存红灯；使用 Assets 的 Editor 配置建立真实资源所有者，不手写假的 AssetScope。
- [ ] 实现生命周期：Initialize 本地准备，AfterInitialize 缓存 IAssets/TimeMachine，Start 建每轮 Root/scope；登记只在停止时。单根 Canvas+GraphicRaycaster、Content/HUD/Page、Modal、Overlay、禁用 Staging。实例 inactive 绑定，父节点固定一次。
- [ ] 实现幂等清理进度与独立完成源；所有外部工厂/Presenter/Unity 激活调用受提交保护，异步完成在保护外。保存失败记录，Release 重试；同步 Shut 立即失效/隔离、释放票据，不等待下一帧。
- [ ] 补 `ForeignOwnerAndRestartInvalidateHandles`、`RepeatedCloseHasIndependentAwaiters`、`PartialStartStillShuts`，重跑全组直至通过。

## Task 3：页面栈、模态与事务回滚

**Files:** Runtime/UINavigation.cs、UIRun.cs、UIRun.Opening.cs、UIRun.Closing.cs、UIRoot.cs 与窗口记录；Tests/PlayMode/UINavigationTests.cs 及专用 View/焦点探针（导航、失败与重入共用真实 Core/Assets fixture）。

**Consumes:** Task 2 记录创建/释放/提交保护；Task 1 IUI 签名。
**Produces:** Push/Back/Page Close/Modal Open、Close 与固定层置顶；页面栈和模态栈唯一真相。

- [x] 写 `BackClosesModalBeforePage`、`RootBackAndNonTopCloseAreRejected`、`TransparentChainStopsAtOpaquePage`、`PushLimitPreservesHistory`，断言栈深、active 状态、句柄与输入策略。
- [x] 跑失败测试，再实现导航提交：候选准备期间旧页保留，成功后更新稳定集合；导航占用期间返回 NavigationBusy；HUD/Overlay 独立加载可交错。模态 BringToFront 如允许则纳入导航门禁并同步模态顺序；Page 一律拒绝。
- [x] 写 `CancelledPushRestoresPageAndFocus`、`BindOrResumeFailureRollsBackCandidate`、`RollbackFailureReturnsCleanupHandle`、`UnbindRetryOutlivesReleasedScope`，注入部分完成后抛错的 Presenter。
- [x] 实现事务回滚和隔离边界、清理句柄及 Committed 故障；身份占用直到必要清理成功。
- [x] 写工厂、Bind/Resume/Suspend/Unbind、OnEnable/OnDisable、焦点、模态事件的重入测试，以及 `PostCommitNotificationFailureRetainsCommittedIdentity`；重复 Close 同会话优先复用原完成源，不被导航忙碌误拒绝；重跑全部通过。

验收：2026-11-13，独立只读审查通过，双 Domain Reload 各43项 PlayMode、19项 EditMode，合计124次通过。范围与证据见 [Task 3 验收](../../unitycore-ui.md#task-3-页面栈模态与事务回滚2026-11-13)。缓存、动画和后续任务未据此标记完成。

## Task 4：有界缓存、Trim 与旧会话隔离

**Files:** Runtime/UIWindowCache.cs，UIRun/UIWindowRecord；Tests/PlayMode/UICacheTests.cs。

**Consumes:** Task 3 稳定逻辑关闭与 Task 2 实际资源清理。
**Produces:** CachedHidden 复用、LRU 容量与 TrimAsync；不持有栈内页的淘汰权。

- [x] 写 `CachedReopenKeepsInstanceButChangesSession`，断言同 View 对象、新 Handle、旧 Handle 的 Close/Release 返回 InvalidHandle；Bind 一次、Resume 每展示一次。
- [x] 写 `LruCapacityOneEvictsOldestAfterRealDestruction`、`ZeroCapacityReleases`、`StackPagesDoNotCountAsClosedCache`、`TrimDoesNotRemoveNavigationHistory`；跑红灯。
- [x] 实现类型查找+链表 LRU，冷隐藏 Suspend/SetActive(false)；取消缓存重开恢复原位置。超限等待释放；失败记录留在诊断且禁止覆盖，不当作已释放腾位。
- [x] 补缓存驱逐与重开/Trim 交错、失败清理重试测试：正在 Releasing 的实例不能重新打开，Trim 捕获稳定候选，不销毁后来已重新展示会话。跑缓存与导航回归。

2026-11-13：Task 4 已完成，新增 `UIRun.Caching.cs` 分离缓存收敛流程；17 项缓存测试与 43 项原 PlayMode 回归、19 项 EditMode 在双 Domain Reload 下共 158 次执行通过，独立审查发现的重复 Close 回归已修复。设计、失败容量保留与验收证据见 [Task 4 记录](../../unitycore-ui.md#task-4-有界缓存lrutrim-与旧会话隔离2026-11-13)。Task 5–8 不据此标为完成。

## Task 5：输入、焦点、安全区和内置过渡

**Files:** Runtime/UITransition.cs、UIRun.Presentation.cs、UISafeArea.cs、UICanvasInputGate.cs 及既有 UIRun/UINavigation；Tests/PlayMode/UIInputTests.cs、UITransitionTests.cs、UITask5Fixture.cs、UISizeProbe.cs，EditMode/UISafeAreaTests.cs。

**Consumes:** ITimeMachine.RegisterUpdate 与可释放票据，Task 3 稳定可见/交互集合。
**Produces:** 一个共用遮罩、顶模态输入、页面 HUD 策略、安全区变更、可取消不受 timeScale 影响的动画。

- [x] 写双模态遮罩 sibling 顺序与 EventSystem.RaycastAll/ExecuteEvents 测试；关闭恢复合法默认焦点；失活/销毁/不可交互旧选择不恢复。Overlay 点击穿透，过渡输入阻断。
- [x] 实现层内 CanvasGroup 策略，禁止子 CanvasGroup.ignoreParentGroups 绕过；Prefab 不合法设置在绑定前报错，额外 Canvas 仅诊断不暗改材质/层级。选择旧控件前验证归属和可交互性。
- [x] 写安全区归一化测试（含零尺寸不除零、旋转/恢复），正文节点更新、背景和遮罩全屏；实现一个持有票据的尺寸/safeArea 检测，不逐帧无变化重写 RectTransform。
- [x] 写 `TransitionCompletesAtTimeScaleZero`、`CancelledEnterRestoresAllBaselineProperties`、`ShutdownDoesNotWaitForTransitionTick`、`CompletedTransitionReleasesTicket`；跑红灯。
- [x] 实现线性 fade/offset/scale、有限时长、独立 visual、保存基准；TimeMachine 回调忽略传入 scaled dt，读取 unscaledDeltaTime。停止/取消立即复位并完成原完成源；Core 退出不依赖下帧。
- [x] 重跑输入、过渡、回滚、重入测试通过。

2026-11-13：Task 5 已完成。额外 Canvas 的排序/批次风险仅诊断，同时附加局部射线过滤防止 overrideSorting 绕过输入规则，不重写材质/层级。独立审查修复非顶模态动画顺序、销毁默认焦点和尺寸回调退休后的写入；新增 21 项 PlayMode、6 项 EditMode，双 Domain Reload 各 81/25 项，共 212 次执行通过。实际覆盖和完整证据见 [Task 5 记录](../../unitycore-ui.md#task-5-输入焦点安全区和内置过渡2026-11-13)。真实输入后端、设备与性能不由该轮路由/数学测试替代，Task 6–8 未据此完成。

## Task 6：迟到任务、外部销毁与诊断回归

**Files（实际）:** Runtime/UIModule.Clear.cs、UIRun.Destruction.cs、UIRun.Diagnostics.cs、UIWindowDiagnostic.cs 及既有运行/导航/缓存/诊断文件；Tests/PlayMode/UIClearTests.cs、UILateTaskTests.cs、UITask5Fixture.cs，复用 EditMode/UIPlayCycleTests.cs。Assets 仅增加测试程序集内部可见性；UIDemo 增加 Core/Assets testables 与既有 Assets 测试所需内置 Audio 模块。

**Consumes:** 前述全部生命周期和清理路径。
**Produces:** 正常/强制退出、Restart 与意外外部 Destroy 的确定隔离；真实组件诊断。

- [x] 写运行中/启动中退出、连续两次 Play、同模块 Restart、挂起 Open 强退、旧加载迟到、旧清理晚完成、外部 Destroy 栈顶/前页/缓存窗口测试。
- [x] 跑红灯，实现捕获上下文而非重查当前模块；观察器只报告事实，模块拥有跟踪后的清理任务。冷隐藏从未激活的对象需依据 AssetInstance.IsAlive 检测，不依赖 Unity 必发 OnDestroy。外部销毁后移除无效导航记录并恢复可用前页；活动事务不得提交已销毁候选。
- [x] 实现诊断快照：各状态数量、stack/modal/cache、pending、failed cleanup；按实际 Graphic/Canvas/Mask/RectMask2D 记录材质/texture、overrideSorting/extra Canvas 等风险。自动诊断只声明客观组件与资源身份，不声称通用计数能够判定实例材质来源、TMP submesh/fallback 身份或预测批次；无法归因的来源标记未知，TMP 专项和材质归因列为人工检查，并同步设计文档诊断章节与正式契约。
- [x] 添加 `CleanupHandleRetriesAfterViewDestroyed`、`ClearDrainsStackCacheAndPendingOpen`、`FailedClearKeepsModelAndAllowsRetry`、`ClearInvalidatesOldHandlesAndAcceptsNewRoot`：Clear 中拒绝新请求、取消未提交 Open，失败继续清理且不重新开放；成功恢复接收。Clear 从外部同步回调调用同样拒绝重入。
- [x] 添加诊断不可修改内部集合、缓存持有不为零、最终清理归基线测试；全 UI 测试及相关 Core/Assets 生命周期测试通过。

2026-11-13：Task 6 已完成，新增 23 项 PlayMode；双 Domain Reload 各 104 项 PlayMode、25 项 EditMode，共 258 次 UI 执行，另有 52 次 Core/Assets 生命周期回归，均通过。迟到加载通过受控后端配合真实 Core/Assets/UI 验证；从未激活对象通过真实 AssetScope 内的受控记录验证。独立代码审查已通过，证据及范围见 [Task 6 验收](../../unitycore-ui.md#task-6-clear迟到任务外部销毁与诊断2026-11-13)。Task 7–8 保持未完成。

## Task 7：独立 UIDemo 与真实 Bundle 集成

**Files:** examples/unity/UIDemo/Assets/UIDemoBootstrap.cs、Views/、Presenters/、Editor/UIDemoBuilder.cs、Tests/UIDemoTests.cs，Packages/manifest.json、ProjectSettings/、README.md；必要 prefab/scene/ResourceGroups 与 meta。

**Consumes:** 完整 IUI；`EditorAssets.CreateConfigured(groups, ...)`、`AssetBuilder.Build(settings, output)`、`AssetDeployment.CopyLatestToStreamingAssets(settings)`；克隆构建配置再构建到新目录，不修改开发配置版本。UIDemo 提供 `UIDemoBuilder.Create/BuildContentSbp/ReloadOn/ReloadOff` 批处理入口。
**Produces:** 可直接打开的独立工程，显式注册 Assets 后 UI，真实可点击演示与 Bundle 测试。

- [x] 写集成测试：经真实射线点击走三页/透明页、HUD、双模态、Toast、Back、重复打开拒绝、缓存实例命中与容量为1的 LRU、动画中取消；显示错误结果与诊断，不吞异常。
- [x] 实现显式 Model/View/Presenter 和宿主装配；使用 uGUI Text，构建真实 Prefab，资源工厂不依赖运行时反射或 Resources。Host 保存 Core 引用用于退出，不在 OnDestroy 重取 Core.Inst。
- [x] 用现有资源构建流程生成新目标 Bundle 并部署到隔离 UIDemo，测试窗口与动态图片 lease 的共享、缓存持有、Trim 最后卸载；不能用 AssetDatabase 成功代替 Bundle 验证。
- [x] 增加切场景 Root 保留及 `ClearAsync` 会话替换演示/测试：清理涵盖栈内、可见、缓存和故障实例，等待成功后释放旧 Model，再替换宿主持有的会话依赖对象，预先注册的显式工厂读取新依赖（不在运行中改定义）。失败不释放旧 Model，重试 Clear；恢复接收后可 Push 新根页，旧句柄失效。最小 Root 页面 Back 无目标时不静默消失，不强行 Release 可见页。
- [x] 运行 Demo 测试，保存稳定页和双页过渡截图；Screen Space Overlay 使用 Game View/屏幕捕获，不复用 Camera RenderTexture 截图。可见 Editor 中另验真实鼠标/键盘输入：先发现 Pipeline 可用能力，无真实注入能力则明确列为人工验收阻断项，不宣布交互验收通过；RaycastAll/ExecuteEvents 只证明事件路由，不能冒充输入后端验收。README 明确启动、资源构建、验证边界及采样操作。

2026-11-13：Task 7 已完成。11 项真实 Bundle Demo 测试覆盖事件路由、共享身份、两种最后持有者卸载、LRU、取消、Toast、Clear 重试与 Single 切场景；双 Domain Reload 各 UI PlayMode 104、EditMode 25、Demo 11，共 280 次通过。可见 Editor 另经 Windows 原生鼠标/键盘事件验证导航、双模态返回、Clear 和切场景；稳定/模态/过渡 screen 截图及诊断已归档。独立代码和截图审查通过，实际记录见 [Task 7 验收](../../unitycore-ui.md#task-7-独立-uidemo-与真实-bundle-集成2026-11-13)。Task 8 保持未完成。

## Task 8：隔离验证脚本、独立审查与交付

**Files:** scripts/verify_unity_ui.py、scripts/test_verify_unity_ui.py，docs/unitycore-ui.md、包 README、UIDemo README、总设计/规范与必要入口链接；保留设计文档为设计记录。

**Consumes:** 现有 unity_packages.py 隔离复制工具、Unity CLI、Task 7 Demo 构建入口。
**Produces:** 一条新证据目录的 Editor 验证命令，实际结果与未验证边界记录。

- [ ] Python 单测验证输出已存在拒绝、相对包引用、隔离复制包括三包、失败 XML/日志不算成功、预期测试计数非零且无 skip、日志错误及 JobTempAlloc/TLS 警告检测。先红后实现，不复制整份 Assets 矩阵脚本。
- [ ] 脚本顺序：调用 `unity_packages.verification_workspace(output)` 与 `copy_verification_project(project, workspace)` 复制独立源码 → 编译/生成 Demo fixture → 构建 Bundle → Domain Reload 开/关各 EditMode+PlayMode → XML/完整日志审查 → 截图/指纹/summary.json/workspace.txt；失败现场保留。使用短路径临时工程，不启动或更改用户已打开工程。过滤仅 `AiNative.UnityCore.UI.Tests` 与 Demo 的精确 fixture；不能用 `AiNative.UnityCore` 宽过滤拉入依赖 AssetsDemo Probe 的整套 Assets 测试，相关 Assets 回归仍在隔离 AssetsDemo 中单独执行。
- [ ] 运行 `python -m unittest discover -s scripts -p test_verify_unity_ui.py -v`；再运行 `python scripts/verify_unity_ui.py TestResults/ui-editor-<唯一后缀>`。不预填通过数量，按 XML 汇总。
- [ ] 独立只读审查完整 UI 变更；修复后重跑受影响测试，关键契约变动重跑完整 UI 矩阵。单一失败最多三轮假设/修改/验证，保留失败材料，阻塞时报告而非无穷重试。
- [ ] 检查本地 Markdown 链接、JSON/asmdef、全部新增文件空白及 `git diff --check`；确保未跟踪新文件也被检查。未请求则不 commit/push。
- [ ] 更新实际验收记录，明确编译/测试数/截图/证据位置。人工 Frame Debugger、CPU/GPU/overdraw 性能对照以及 Player/IL2CPP/移动端未执行则单列，不以结构测试或截图声称性能达标。

## 验证策略与执行次序

按 Task 1→8 顺序 Native 实现，任务间复用同一隔离源码快照并在正式矩阵前重新复制，防止验证旧代码。每个任务尽可能先跑对应失败测试，再最小实现后跑绿灯；Unity 不可用或授权/工具不足时保留静态检查和红灯材料，明确报告阻塞，不宣称测试通过。调用 Unity 前加载 unity-cli 技能，真实运行/截图按项目启动技能执行。

实现完成不等于全部跨平台验收完成。交付最少包括完整首期能力、自动化契约测试、可用 Demo、真实 Bundle 测试、Windows Editor 双重载证据或明确的环境阻塞。性能对照提供步骤与采样口径，不自动许诺最低 draw call。
