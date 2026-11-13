# Unity UI 模块框架设计与技术决策

- 存档日期：2026-11-12。
- 状态：框架方案已确认，基础窗口与生命周期正在实施；当前实现与实际验收见 [实施状态](unitycore-ui.md)，本文不代表全部功能已完成。
- 适用读者：UI 模块的后续实现者与审阅者。
- 范围：uGUI 应用表现服务，不改变模拟内核、Hosting、PlayHost 的职责，不持有权威玩法状态，不推进模拟 Tick。

## 1. 背景与已确认决策

复用现有 [UnityCore 生命周期](unitycore-lifecycle.md)与 [Assets 资源所有权](unitycore-assets.md)，新增通用 UI 服务。遵守 [架构与代码规范](engineering-guidelines.md)。

用户已确认：

- 使用 uGUI。
- 新建独立 UIModule，依赖 UnityCore 和 Assets；UI 资源交给 Assets，UIModule 提供窗口级封装。
- 模块拥有跨场景 Screen Space Overlay Root。
- 单实例窗口，重复打开明确拒绝。
- 轻量 MVP，显式装配，不使用反射扫描、DI 容器、全局事件总线或自动绑定。
- 首期包含缓存、返回栈、窗口动画。
- 页面栈与模态弹窗分离；Back 优先关闭顶层模态，再返回上一页；HUD、提示不入栈。
- 按窗口配置释放或缓存；已关闭缓存采用有界 LRU，栈内前页不参与普通缓存淘汰。
- 动画采用轻量内置过渡，支持淡入淡出、根视觉容器位移和缩放，不新增动画库。
- 可以新建独立 UIDemo 验证。

[总设计](ai-native-game-engine-design.md)与架构规范已纳入 UI 应用表现服务边界；实现和验证按阶段推进，不能把设计存档视作功能已接入。

以下具体 API、容量默认值和执行策略是设计建议，实施时可在不改变已确认行为的前提下收敛。新增交互层等范围变更应重新确认。

2026-11-13 Task 3 实现页面栈、模态及基础输入/焦点，Task 4 接入有界缓存、LRU、Trim 和展示会话隔离，Task 5 补齐输入路由验证、安全区与内置过渡，Task 6 接入 Clear、外部销毁恢复、迟到任务隔离与诊断快照；实施收敛及实际验证见第 5–9 节与 [验收记录](unitycore-ui.md)。Task 7 已接入独立 UIDemo 与真实 Bundle；设备与 Player 等仍分别验收。

## 2. 包、依赖与职责

拟新增包：`packages/com.ainative.unitycore.ui`，命名空间 `AiNative.UnityCore.UI`。

运行程序集引用 UnityCore、Assets、UniTask 和 UnityEngine.UI。基线为 Unity 6000.3、UniTask 2.5.11、uGUI 2.0.0。不硬依赖 TMP、Input System 或第三方动画库，不建设 uGUI/UI Toolkit 双后端，不依赖模拟内核、Hosting、物理或 Example。

| 组成 | 职责 |
| --- | --- |
| UIModule / IUI | 显式登记、窗口实例、导航、层级、模态输入、过渡、资源所有权 |
| UIView : MonoBehaviour | 序列化控件引用和展示方法，不查找 Core.Inst 或业务服务 |
| Presenter | 普通 C# 对象，绑定业务观察、处理界面交互、提交业务命令，不推进 Tick |
| 宿主装配 | 登记窗口定义和 Presenter 工厂、注入业务依赖、配置 EventSystem |

窗口定义以 View 类型为唯一身份，包含 Prefab 地址、角色（Page / Modal / HUD / Overlay）、固定层级、页面是否不透明全屏、关闭后缓存策略、过渡参数和 Presenter 工厂。一类型登记一项定义，不自动扫描。

Presenter 使用显式同步的 Bind / Resume / Suspend / Unbind：

- Bind：实例创建时建立本地绑定。
- Resume：接入业务观察和展示刷新。
- Suspend：停止展示刷新、展示订阅与动画。
- Unbind：释放剩余绑定。

清理回调必须幂等并可从部分完成状态重试。持久业务状态属于 Model，不能依赖缓存实例保存。无需为每个 View 和 Presenter 机械生成接口。

Suspend 与 Unbind 拥有独立清理进度：Suspend 失败仍尝试 Unbind 和 scope 关闭，重试只执行未成功步骤。因此 Presenter 必须允许 Suspend 在 Unbind 成功、View 已销毁后重试，清理不能依赖 View 仍然存在。绑定或恢复进入前登记其清理责任，部分回调抛错也必须收尾。

应用 Root 跨场景不意味着会话依赖永久有效。宿主替换或释放对局前，先关闭并释放相关窗口，包括栈内和缓存实例，再释放业务服务。UI 状态不进入 v2 权威快照。

## 3. UI 层级

### 3.1 固定显示层

默认一个根 Canvas，下列容器从下到上排列：

| 显示层 | 内容 | 输入策略 |
| --- | --- | --- |
| Content / HUD | 游戏状态、快捷栏等常驻界面 | 没有模态时可交互；由页面策略决定是否可操作 |
| Content / Page | 主菜单、背包、设置等导航页面 | 默认只有栈顶页面可交互 |
| Modal | 确认框、选择框等模态窗口 | 只有顶层模态可交互 |
| Overlay | Toast、飘字、非交互提示 | 不接收输入，不阻挡点击 |

另有始终禁用的 Staging 节点，仅用于实例创建和绑定，不参与显示。

这些层首先是 RectTransform 容器，不是每层一个 Canvas；不默认给每窗口添加 Canvas。根 Canvas 使用 Screen Space Overlay 与 CanvasScaler，配置由显式选项提供。后续只有在测量证据支持时才给热点区域增加子 Canvas。

**默认 HUD 位于 Page 下方。**“是否需要交互式 HUD 始终位于全屏页面上方”尚未确认；首期不擅自增加该层。若确有需求，应增加明确交互层，不得把交互窗口放入非交互 Overlay。

### 3.2 层内顺序

共享 Canvas 内主要通过 sibling 顺序控制覆盖关系，越靠后的兄弟节点越靠上。

- HUD 按成功打开顺序排列，可在自身容器内置顶。
- Page 由页面栈控制，不允许 BringToFront 绕过导航。
- Modal 按模态顺序排列，新模态位于上方。
- Overlay 按展示顺序排列，新提示位于上方。

窗口定义固定角色和所属层，业务不能任意跨层或设置 sortingOrder。框架不为了合批全局按材质重排控件。

### 3.3 模态遮罩与焦点

推荐整个 Modal 层共用一个全屏遮罩，始终位于顶层模态正下方。例如打开两个弹窗后，从下到上为：下层弹窗、遮罩、顶层弹窗。避免多个遮罩叠加越来越黑。遮罩默认吞输入，不自动关闭窗口。

同时禁用下层窗口交互及射线接收，清除下层键盘/手柄选择，焦点交给顶层模态。关闭时仅恢复仍有效且可交互的旧选择，否则选择窗口明确配置的默认焦点。模态不等于暂停游戏，宿主负责玩法输入策略。

首个模态打开前保存非模态选择，最后一个模态关闭时按当前交互集合恢复；该选择不随模态重排或非顶模态关闭而丢失。页面允许 HUD 输入时，合法 HUD 选择同样可恢复。选择在候选激活前捕获，不能被候选 OnEnable 自行设置的焦点覆盖。已在顶层的模态 BringToFront 幂等；非顶模态置顶同步更新顺序、遮罩及焦点。

### 3.4 导航、可见性与缓存分别管理

- 导航栈决定 Back 返回哪里。
- 可见集合决定哪些窗口需要绘制。
- 交互目标决定哪些控件可以操作。
- 已关闭缓存独立于以上三者。

不透明全屏页覆盖后，旧页保留实例但冷隐藏；透明页保留背后真正需要显示的页面，直到遇到不透明全屏页。下层页面即使可见也不接收输入。

缓存窗口留在原层容器并 inactive，不因每次隐藏和恢复反复 reparent。动画期间新旧页仍在 Page 容器中按导航顺序绘制，不临时增加“动画 Canvas”或任意改 sortingOrder。

## 4. 最小操作与句柄

以下为拟新增能力，不是现有公共 API：

| 操作 | 契约 |
| --- | --- |
| OpenAsync<TView>(token) | 打开 HUD、Modal、Overlay；Page 必须使用 Push |
| PushAsync<TPage>(token) | 新页面入栈 |
| BackAsync() | 优先关顶层模态，否则 Pop 页面；仅剩根页时返回 NoBackTarget |
| CloseAsync(handle) | 逻辑关闭；Page 仅栈顶可关闭，等同 Pop，非栈顶拒绝 |
| ReleaseAsync(handle) | 销毁已关闭缓存实例，或重试失败清理；可见或仍在栈中的窗口不能直接释放 |
| TryGet<TView> | 只查询稳定可见的实例 |
| BringToFront(handle) | 仅作用于允许置顶的同角色层内窗口，不绕过页面导航 |
| TrimAsync | 清理可淘汰缓存，不静默删除返回历史 |
| ClearAsync | 停收并排空本轮窗口及操作，成功后创建新 UI 代际；失败保持停收，允许重试 |

提供模态状态只读查询及实例级订阅，订阅者显式解绑，不创建全局消息总线。

单实例身份从创建中一直占用到释放完成。可见、Covered、过渡中、清理失败的同类型窗口重复打开均拒绝；只有已经逻辑关闭的 CachedHidden 可以显式 Open/Push 重新激活，属于缓存命中，不是对已打开窗口的自动复用。

UIHandle 绑定模块 owner、运行 generation、实例及展示会话。同实例缓存重开生成新展示会话句柄；旧句柄不能关闭新展示。关闭后的原句柄仅在该实例尚未重新打开时可以 Release。

预期拒绝通过 UI 局部小型结果及错误码表达，不建设全仓 Result 框架；非法登记立即失败。取消遵循异步取消语义，加载和清理异常保留原始故障。

打开未成功而回滚失败时，UI 操作异常携带仅供 Release 重试的清理句柄及原始错误；不能要求调用方从只查询可见窗口的 TryGet 找到失败记录。失败记录保留所有者并占用身份。

## 5. 状态、导航事务与重入

区分物理实例、展示会话与导航记录。主要状态包含 Opening、Entering、Visible、Covered、Exiting、CachedHidden、Releasing、CleanupFailed，释放完成后移除记录。

所有操作及取消仅支持 Unity 主线程，不加线程锁、线程身份检查或自动切线程。

导航操作 Push / Back / Modal 开关首期串行，导航中重复请求返回 NavigationBusy，不隐式排队、不采用 latest-wins。非导航 HUD/Overlay 加载可交错，但同样遵守提交保护。

Push：保留当前页 → 加载或取得缓存候选 → inactive 暂存、校验和 Bind → Resume、进入过渡 → 提交新栈顶 → 按遮挡规则处理旧页 → 返回句柄。准备和进入期间旧页可恢复，不提前销毁。

Task 3 无动画实现的提交点收敛为：候选准备完成后，在同一同步保护区内恢复/隐藏受影响页面、更新输入与焦点，检查取消，最后替换页面/模态列表。列表替换前失败恢复原快照；列表替换后不把清理或通知失败伪装为未提交。候选在成功分配交互权前不接收输入，失败后先同步隐藏，再等待资源排空。后续动画接入须保留同样的提交与回滚边界。

Open/Push token 只控制提交前；取消时停止候选过渡并恢复旧页视觉和焦点，缓存候选回原缓存，新实例释放。成功后 token 取消不关闭窗口。

Back/Close 不接受业务取消 token；接受后负责完成收敛。先恢复前页并校验，再过渡和提交栈变化。提交前普通故障尽可能恢复原稳定状态，无法恢复时隔离受影响窗口并报告错误。提交后的清理或通知故障不伪装成“操作未发生”。

所有同步外部回调均纳入统一提交保护：Bind/Resume/Suspend/Unbind、框架激活或禁用触发的 Unity 生命周期、焦点切换及模态通知。保护区内修改 UI 生命周期明确拒绝，不仅保护 Bind。先建立内部状态，再执行外部回调，交付前复核代际与状态；完成源在保护区外完成，避免等待者同步恢复被误判为重入。

Core 生命周期请求不属于 IUI 的重入拒绝范围：回调可引发 Core.CloseAsync 或强制退出。打开流程须在工厂、Bind、Resume、父级变更与激活之后检查本次取消，不能在退出后继续 Resume 或发布成功。强制退出先停止接收、取消并隔离 Root；若正在外部回调中，则在最外层保护退出时同步解绑，不递归进入尚未结束的 Bind，也不等待下一帧。回调错误与延迟解绑错误同时保留，清理异常不能覆盖原始错误或取消。

提交后通知失败时，错误明确标明操作已提交，并携带该操作仍适用的身份。打开成功后可以携带当前展示句柄；Close/Back 已释放对象时不保证有效展示句柄，不能为故障载荷保留已释放对象。调用方不得盲目重试。

回滚本身无法恢复时，当前实现将受影响的整组页面/模态历史隔离：先退出可见集合、禁用输入及 GameObject，再逐个等待清理；其他 HUD/Overlay 不因此移除。故障记录仍保留所有权，已有窗口可用原句柄 Release 重试，未成功打开的候选通过 CleanupHandle 重试。隔离导致 HasModal 改变时仍通知订阅者。Committed=false 只表示本次候选/关闭未提交，不保证恢复失败后原历史仍存在。

ModalStateChanged 只在零/非零模态状态改变时触发。逐个调用订阅者并汇总错误；任一订阅者触发 Core 关闭/退休后停止剩余通知，同时保留已发生异常与取消原因。若最后一个模态 Close 已提交、正在等待实际销毁时 Core 开始关闭，窗口继续释放，但延迟通知取消会返回 Committed=true 的操作异常；不需要状态变化通知的关闭可正常成功。Shut 清空订阅，重启后宿主须重新订阅。

模态 BringToFront 同样以顺序列表替换为提交点。焦点回调导致 Core 停收后不恢复旧运行历史；若仍运行但同步恢复也失败，则退役该 UI 运行，宿主通过 Core.CloseAsync 排空剩余 scope。该接口不为同步操作伪造异步清理成功。

重复 Close 等待独立完成源，不反复消费池化 UniTask。解绑与资源释放分别记录完成情况；Suspend/Unbind 可幂等重试，必要解绑和 scope.CloseAsync 均成功才解除 CleanupFailed。一个回调失败仍继续其余资源清理并汇总错误，不逐句扩大 try/catch。

## 6. 缓存与 Assets 所有权

所有权：本轮 UI scope → 每实例子 scope → Prefab 实例与动态资源 lease。栈内、Covered、缓存窗口继续持有自己的 scope。

- 默认冷隐藏：Suspend 后 SetActive(false)，停止输入、展示订阅与过渡，不只设置 alpha=0。
- 栈内页面不计入可淘汰缓存；设显式栈深上限，达到后拒绝 Push，不静默丢历史。
- 已关闭缓存采用独立 LRU，建议默认上限 8，可设为 0；缓存命中移出 LRU，归还更新顺序。
- 超限需等待最旧实例真实释放，不能仅从字典删除。释放失败保留故障所有者，不覆盖。
- 数量上限不是字节预算；少量窗口仍可能持有大图集和字体。首期提供 Trim、diagnostics 和栈深控制，不伪造精确显存预算。宿主决定内存告警时何时 Trim。
- 首期不做独立 Canvas 热缓存，后续有实测热点再扩展。

Close 成功表示进入配置的终态：缓存策略下资源保留，释放策略下等待实际销毁。Release 成功始终表示资源清理完成。

Task 4 实现使用运行内的类型字典维护实例唯一性，`UIWindowCache` 用链表维护关闭顺序，不拥有页面栈。容量默认为 8；容量为 0 或 Release 策略直接清理。缓存命中复用 View、Presenter 和 scope，不重复工厂、Bind 或 reparent；每次展示调用 Resume，逻辑关闭调用 Suspend 后 inactive，保留 Bind。重复 Close 幂等且不刷新 LRU，重开后再次关闭才更新顺序。

入缓存前先释放最旧条目并等待真实清理完成，清理中或失败的旧条目继续占用容量，`ClosedCacheCount` 包含这些保留项，不等于当前可复用数量。若淘汰失败，新关闭窗口回退到释放，避免超限；Close 返回已提交异常，保留受害窗口的 CleanupHandle。清理失败仍占类型身份，不能用重新 Open 覆盖；Release 或 Trim 可重试。关闭完成源与资源清理完成源分开，已完成的缓存 Close 不会阻止后续 Release、Trim 或 Core 关闭。

缓存重开先移出 LRU 并分配单调递增的展示会话。提交前取消或失败且恢复健康时，恢复原关闭句柄与原 LRU 顺序；失败尝试的会话号不重复使用。若回滚期间缓存已被其他关闭填满，按原顺序执行容量检查：恢复候选比现有最旧项更早时直接释放它，否则先腾位。无法恢复冷隐藏或导航时清理候选，清理失败通过新 CleanupHandle 重试，不恢复旧展示句柄。旧句柄在成功重开后不能 Close、Release 或 BringToFront 新会话，即使新会话已再次关闭。

Trim 在调用时捕获缓存记录及展示会话，逐项清理前重新检查仍属于同一缓存会话；等待前项销毁期间发生重开或重开后再关闭的后项会被跳过。Trim 不遍历或释放页面历史、可见窗口；单项失败继续处理其余候选并汇总异常。并发 Trim 共用每实例清理完成源；Trim 与清理计入 Pending，真实 Core 关闭等待它们收敛后排空剩余缓存。清理回调与重开回调同样受同步重入保护。

复用 [IAssets](../packages/com.ainative.unitycore.assets/Runtime/IAssets.cs)、[AssetScope](../packages/com.ainative.unitycore.assets/Runtime/AssetScope.cs) 的 CreateScope / InstantiateAsync / CloseAsync。AssetInstance.Dispose 仅请求 Destroy，Destroyed 是 internal；UI 不访问内部成员，不扩大 Assets API。

实例化到禁用 Staging，实例自身设 inactive，绑定后激活；Awake 只处理本地初始化，不假定 Presenter 已绑定。动态资源通过窗口 scope 的 IAssets 视图取得，不暴露 UI 根 scope 关闭权。

业务不能自行 Destroy 框架窗口；意外外部销毁使实例失效并进入统一清理，移除无效导航记录，不恢复不存在的对象。生命周期观察器只报告事实，不能在 OnDestroy 同步等待释放。

Task 6 使用现有表现 Update 票据逐帧检查 AssetInstance.IsAlive 及 View 存活，不依赖 OnDestroy 或安全区发生变化。扫描只发现事实，清理由捕获的 UIRun 持有并计入 Pending；不与 Opening、Releasing、挂起 Close 或活动导航事务并行修改同一记录。导航准备/恢复及列表替换排除已销毁记录，恢复前页、输入和焦点；恢复也失败时沿用整组导航隔离规则。候选在焦点等外部回调后、提交前再次校验存活，不能交付已销毁 View 的成功结果。

销毁后的即时 Close 跳过动画并直接清理；已缓存但已销毁的重复 Close 共用清理完成源。缓存入场前及等待 LRU 淘汰后再次检查存活，不将死实例放回缓存。清理移除字典前核对记录引用，迟到的旧清理不能删除同类型新实例。后台恢复故障汇总为诊断 LastRecoveryError，清理失败的记录保留身份，可通过原句柄 Release 重试；该字段记录最近一次后台恢复错误，不表示所有历史错误仍未解决。

## 7. 输入、安全区与动画

ICore 不公开 Transform；UI 创建独立 DontDestroyOnLoad Root，不扩 ICore、不访问内部字段。背景和模态遮罩全屏，正文位于安全区。模块持有表现回调票据检测尺寸和 Screen.safeArea 变化，关闭注销。

EventSystem 由宿主显式提供，不自动搜索或创建。Demo 建议采用 StandaloneInputModule 并匹配设置；包不硬依赖 Input System，不同时启用两个输入后端。

宿主必须保证注入的 EventSystem 覆盖 UI 的完整运行周期；跨场景使用时由宿主保留它并避免新场景重复创建。当前构造函数不支持运行中替换 EventSystem，Root 常驻不代表框架接管 EventSystem 的生存期。

过渡采用不受 timeScale 影响的有限时长淡入淡出、位移和缩放，不逐帧修改布局规则、字体大小或实例材质。只操作独立视觉容器，不修改全屏和安全区锚点。

保存基准 alpha/位置/缩放，取消后明确复位；过渡期阻断受影响窗口输入，结束停止任务。窗口持有过渡对象及独立完成源，取消关联本次 Open 的 linked token 或 Close 的运行 Lifetime，不另建业务取消通道。复用 TimeMachine 表现票据，完成必须释放。

Core 关闭和退出 Play 跳过动画，不能等待已停止派发的 TimeMachine。双页转场期间额外 draw 和透明 overdraw 属于暂态峰值，必须测量。

### Task 5 实施收敛

绑定前校验 enabled 的根 CanvasGroup、无 ignoreParentGroups 的子组，以及直接位于窗口根下的 SafeArea 和其独立 Visual 后代。SafeArea 不能与 Visual 共用同一节点。业务不得在运行中关闭根组或改写框架输入策略。额外 Canvas 输出风险警告，不修改作者的 sortingOrder、材质或 Canvas 层级；在已有 Canvas 节点附加局部射线过滤，读取窗口根组策略，避免 overrideSorting 使 uGUI 提前停止祖先过滤。此处理覆盖绑定时存在的 Canvas；运行中新增 Canvas 或改变输入层级须由宿主遵守同样约束，当前不提供自动扫描修复。

焦点候选须通过 Unity 对象存活、activeInHierarchy、Selectable.IsActive/IsInteractable、窗口归属及当前输入许可检查。默认 Selectable 已销毁时不通过 CLR 空值运算符访问 gameObject；没有合法候选则清空选择。HUD/Overlay 关闭同样刷新焦点。输入测试使用真实 GraphicRaycaster、EventSystem.RaycastAll 与 ExecuteEvents，覆盖双模态遮罩、点击/提交路由、Overlay 穿透与过渡阻断；这些证明事件路由，不能冒充真实鼠标键盘后端验收。

每个运行持有一张 `UI.SafeArea` Update 票据，比较 Screen.width、height、safeArea；只有变化时遍历当前已绑定窗口更新正文 anchors 和 offsets，缓存窗口同样更新。新窗口在激活前应用当前安全区，窗口背景及共用全屏遮罩不受影响。归一化边界裁剪到 0..1，零尺寸退回有限的全屏区域；尺寸回调可能触发 Core 关闭，几何写入之间检查取消。正常关闭和强退都同步注销票据。旋转/恢复在测试中以横竖尺寸和安全矩形验证，不宣称真机刘海屏已验收。

`UITransitionOptions.DurationSeconds == 0`（含 default）不产生动画票据。正时长时在 Visual 上取得或创建 CanvasGroup，仅动画该组 alpha、Visual.anchoredPosition 和 localScale；不改变窗口根组 alpha、安全区锚点或布局参数。进入从基准加 EnterOffset、基准缩放乘 EnterScale 和可选零 alpha 线性恢复；退出反向播放，终止时均恢复完整基准，便于缓存复用。每个过渡使用独立 `UI.Transition` Update 票据，忽略 scaled dt，累加 Unity Time.unscaledDeltaTime；完成或取消释放票据及取消注册，完成源在最外层同步提交保护区外交付。

进入动画先激活候选但不提交导航历史或交互权，旧页保持可见；动画成功后才更新导航、遮挡和焦点。退出动画先恢复将要露出的页面，历史与退出窗口保留到动画成功，再执行逻辑关闭。非顶模态退出期间保持原顶模态与遮罩的 sibling 顺序。导航动画期间暂停 UI 窗口输入并清空选择，独立 HUD/Overlay 加载完成也不能解除阻断；非导航动画仅禁用自身。TryGet 不返回正在进入/退出的候选。取消或失败沿用原事务回滚及缓存所有权规则；Core 停收使过渡立即复位和结束，不依赖下一次 TimeMachine 派发，资源销毁仍按原异步清理流程等待。

## 8. uGUI 文档与源码结论

已核实三个示例的本地缓存为 `com.unity.ugui@27635d171b1a`、版本 2.0.0，工程 Unity 6000.3.23f1。深入阅读基于 AssetsDemo 的 PackageCache 副本；没有逐字节比较三份源码。

本地源码相对 AssetsDemo 的 `Library/PackageCache/com.unity.ugui@27635d171b1a/Runtime/UGUI/UI/Core/`：

| 文件与区段 | 已核实含义 |
| --- | --- |
| Graphic.cs:644–716 | C# rebuild、材质/纹理与 Mesh 提交，不是最终原生 batching 算法 |
| CanvasUpdateRegistry.cs:162–230 | 布局、裁剪、Graphic rebuild 顺序 |
| Mask.cs:146–190；MaskableGraphic.cs:102–129 | stencil 写入/恢复及最终材质变化 |
| RectMask2D.cs:205–284 | 裁剪矩形和剔除；无额外遮罩 draw 不等于不同 clip rect 可以合批 |
| Graphic.cs:547–576 | Disable 清理 renderer；Enable 全脏，冷缓存恢复仍需重建 |
| Image.cs:687–701 | 实际纹理来自 Sprite.texture，逻辑同组不是同纹理保证 |

另检查 TMP 多材质、fallback、多 atlas 与 SubMeshUI 路径，不能承诺全部文字一批。Canvas 原生 batcher 不在本地 uGUI C# 包中，公开 binding 也不是完整实现；不能从 C# 队列推导全部合批规则。

### 框架策略

- 默认共享 Canvas，不每窗口创建 Canvas；热点拆分必须依据 CPU rebuild 与 batch 的综合测量。
- 完全遮挡页面不保留无意义渲染、输入、动画或展示刷新。
- 框架不私自复制材质，不反复 reparent，不为合批破坏遮挡顺序。
- 装饰性 Graphic 关闭 raycastTarget 是输入 CPU 优化，不冒充 draw call 优化。
- 检测并报告额外 Canvas、overrideSorting、实例材质、实际纹理、遮罩/裁剪、TMP submesh 等风险；诊断不是原生批次预测器。
- 内容按共同显示概率与生命周期组织图集，共享字体材质预设；矩形裁剪优先 RectMask2D，减少大面积透明重叠与深层动态布局。不要求中文永不 fallback。
- alpha=0 不替代隐藏契约，也不声称必然全绘制或必然零成本。

**不承诺自动最低 draw call。** 降低 draw call、减少重建 CPU 和降低 GPU overdraw 可能互相冲突，最终目标是可验证的总帧时间和内存改善。

### 官方来源

- [Unity UI optimization tips](https://unity.com/how-to/unity-ui-optimization-tips)
- [uGUI 2.0 UI Profiler](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/ProfilerUI.html)
- [uGUI 2.0 Canvas](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/UICanvas.html)
- [uGUI 2.0 RectMask2D](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/script-RectMask2D.html)
- [UnityCsReference 6000.3 Canvas binding](https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/6000.3/Modules/UI/ScriptBindings/UICanvas.bindings.cs)

## 9. UnityCore 生命周期、退出与重启

显式先登记 Assets，再登记 UI；相同默认 ModuleOrder 下保留注册顺序，逆序退出时 UI 先清理。没有依赖自动拓扑排序，须验证实际顺序。

- OnRegister：只登记 IUI。
- Initialize：本地初始化，不调用 Assets；Core 会共同等待各模块 Initialize。
- AfterInitialize：取得并保存 IAssets。
- Start：创建本轮上下文、scope 和 Root，成功后就绪。
- ShutdownAsync：停收请求、终止过渡、取消打开；逐一清理可见、覆盖、缓存和故障窗口，继续其他所有者并汇总错误，关闭 UI scope。
- Shut：同步幂等，支持部分初始化；失效代际、禁用 Root、隔离和解绑回调、释放自有票据与 Root，不等下一帧。

Core 退休时可能完全跳过 ShutdownAsync，所以 Shut 不能依赖此前已执行异步清理。Shut 对剩余窗口尝试独立 Suspend/Unbind，继续其余窗口并汇总错误；后续 Assets.Shut 强制回收资源。正常关闭仍等待窗口 scope 排空，失败会经 Core 报告并阻止自动重启。

Assets 的 ForceRelease 非公开，不扩大 API；UI 同步隔离可见性，后续 Assets.Shut 强制回收其所属资源。不得声称所有原生异步操作已被立即取消。

每轮 generation、Root、scope、记录和取消源独立。旧任务仅清理捕获的旧上下文，不重取 Core.Inst，不改新轮字典，不发布迟到成功。Restart 复用模块实例但不复用运行上下文。

### Task 6 Clear 与诊断收敛

Clear 复用 Shutdown 的停收、取消、禁用 Root、等待 Pending 和逐实例清理，再关闭旧 UI scope。挂起的重复 Clear 共用完成源；清理失败继续尝试其他窗口，保留失败所有权且不恢复接收。成功后退休旧 Root，创建新的 UI generation、Root、scope 和票据，但保留登记的窗口定义与模块事件订阅。宿主只有 await Clear 成功后才能释放/替换旧 Model；UI 不持有或释放业务 Model。工厂可读取宿主持有的当前依赖对象，无需运行中重新登记类型。

Clear 收尾在创建新运行前核对捕获的 run 仍为当前对象且模块没有进入 Core 关闭；旧 Clear 与旧原生加载/清理即使在新 Core 会话后完成，也不能覆盖新运行或清掉新 Clear 的完成源。强退不承诺取消底层原生加载，旧上下文继续收集迟到结果。受控后端测试通过真实 Core/Assets/UI 装配验证此边界，真实 Bundle 后端仍属于 Task 7。

成功清理存在模态的会话后发送 ModalStateChanged(false)。通知失败则返回 Clear 操作异常并停收，允许重试；重试可能再次调用此前已成功的订阅者，因此该失败路径要求通知处理幂等，不保证 exactly-once。通知回调触发 Core 关闭时不能发布 Clear 成功。受保护回调内调用 Clear 返回 ReentrantCall，包括 Core 关闭中的 Unbind 回调。

UIDiagnostics 提供只读 Windows、StateCounts 及组件快照；状态计数覆盖 Opening、Entering、Visible、Covered、Exiting、CachedHidden、Releasing、CleanupFailed，等待缓存入场的挂起 Close 计入 Exiting。每窗口携带不透明句柄、View 类型、IsAlive 和只读组件记录，不暴露 Unity 对象引用。Graphic/Canvas/Mask/RectMask2D 总数统计当前 Root 的完整层级（含 inactive）；窗口组件列表中的 Canvas 均为额外 Canvas，记录 overrideSorting。实例 ID 只标识采样时的本地对象，不是跨运行稳定资源键。

Graphic 的 MaterialId 来自已配置/默认的 Graphic.material，TextureId 来自 Graphic.mainTexture；不读取可能创建 stencil 材质的 materialForRendering，因此不声称已经取得最终渲染材质。材料来源统一标记 Unknown。TMP submesh/fallback、实例材质归因及批次需专项人工检查，不通过这些计数自动判断。新诊断类型用于表达这些已观察事实，不扩展为性能预测接口。

### Task 7 独立 UIDemo 的宿主与资源边界

`examples/unity/UIDemo` 使用明确的窗口类型和 Presenter 工厂，Assets 先于 UI 注册；宿主保存 Core、Model 和 EventSystem，场景以 Single 切换时保留这些对象及 UI Root。Bootstrap 默认直接创建 AssetsModule，始终读取部署 Bundle，不隐式回退 AssetDatabase；测试使用 EditorAssets.CreateConfigured 并断言配置为 AssetBundle。UIDemoBuilder 克隆配置、构建到新目录并通过 AssetDeployment 部署，不改变开发配置版本。

动态图片由宿主 await LoadAsync 后交给 Model 持有 lease，再打开窗口；同步 Presenter 绑定 Text、图片及按钮，不派生不受跟踪的加载任务。Prefab 原始图片引用与动态 lease 直接比较身份，分别验证缓存最后持有和 Model 最后持有的卸载顺序。Clear 成功之后才释放旧 Model 并替换宿主依赖；既有工厂读取当前 Model，运行中不重新 Register。失败保留旧 Model，独立宿主控制面板允许重试，错误摘要显示到界面且完整异常进入 Console。

Toast 到期关闭使用不透明 handle，计时记录归属 Model 会话；Clear 换会话后不操作旧展示。退出沿宿主持有的 Core 关闭，禁止 OnDestroy 重查全局实例。缓存容量 1 用于直观验证实例命中、展示句柄更新和 LRU；根页 Back 明确拒绝，不静默删除页面。所有演示逻辑仍是应用表现，不引用模拟内核或裁定游戏规则。

原生鼠标/键盘与自动化射线测试分开记录；Screen Space Overlay 从 Game View screen 捕获。截图暂停辅助入口仅在真实入场开始后暂停 Editor，恢复 Play 后继续等待原操作，不设置伪造的动画几何。Demo 的可复现操作与人工性能采样步骤见 [README](../examples/unity/UIDemo/README.md)。

## 10. 后续实施与验证

### 实施阶段

1. 同步总设计、边界规范，编写正式 UI 契约和包 README，不修改历史证据。
2. 新建 UI 包及必要类型：IUI、UIModule、UIView、UIRoot、UIHandle、窗口定义、Presenter 与过渡契约；按实际职责拆分，不建设泛化管理器体系。
3. 新增包内 EditMode / PlayMode 测试，参考现有 UnityCore AsyncShutdownTests、Assets AssetCancellationTests / AssetOwnershipTests。
4. 新建 `examples/unity/UIDemo`，相对 file: 引用仓库包，通过 OpenUPM 获取 UniTask，不依赖 Jolt。演示多页面、HUD、双模态、提示、缓存命中/LRU、Back、动画取消。
5. 增加聚焦的隔离验证入口，输出全新证据目录，避免顺带重构现有验证脚本。

### 契约与资源验证

- 注册唯一性、非法角色/层级组合、句柄 owner/代际/会话隔离。
- 重复 Open、缓存重开、栈内同类型拒绝、非栈顶关闭、根页 Back、NavigationBusy。
- 取消、加载/Bind 失败回滚、回滚失败清理句柄、解绑失败重试。
- Unity 激活、焦点、模态通知重入；提交后通知故障的正确身份与提交标记。
- 过渡取消/复位、timeScale=0、退出跳过动画、重复 Close 多等待者。
- LRU、缓存数量上限、栈深上限、Trim、旧句柄不能影响重开实例。
- 资源释放后 diagnostics 回基线；缓存中的资源持有符合预期，不误断言为零。
- 部分启动失败、正常/强制退出、同实例 Restart、迟到任务和外部 Destroy，双 Domain Reload。
- 实际鼠标/键盘输入、遮罩顺序、焦点恢复、安全区、切场景和业务会话替换。
- 真实 Bundle 缓存持有与最终卸载，不以 AssetDatabase 验证替代。

### 性能对照

比较共享/热点子 Canvas、冷隐藏/alpha 隐藏、Mask/RectMask2D、共享/实例材质、实际同/异纹理、TMP fallback/多 atlas、首次打开/缓存恢复/双页转场。

Editor UI / UI Details Profiler 用于解释 Batch Breaking Reason；uGUI 2.0 这些模块是 Editor-only，不能写成 Development Build 能力。Frame Debugger 检查实际 draw、材质、纹理、stencil、裁剪和 Canvas 边界。

目标 Player 另测 CPU 布局/Graphic/Canvas batching、Rendering batches/SetPass、GC、缓存内存、GPU 时间与 overdraw，同时记录稳定帧和过渡峰值。不把 draw call 下降等同于总帧时间改善，不假定 marker 名称跨平台固定。

## 11. 当前完成与未完成范围

设计存档时仅完成文档/源码阅读和独立设计审阅。当前已创建 UI 包及独立测试宿主，Task 3–7 与基础生命周期已有本机 Windows Editor 验证，实际结果与剩余范围以 [实施状态](unitycore-ui.md) 为准。独立 UIDemo 已包含显式 Model/View/Presenter、真实 Bundle 与 Windows 原生输入演示；性能和其他平台不能由设计、截图或事件路由测试推断为完成。

后续首期基线为 Windows Editor / Unity 6000.3.23f1。Player、IL2CPP、移动端与跨平台分别验收，不能借用已有其他模块的历史证据扩大 UI 的验证范围。
