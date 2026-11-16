# UIDemo

独立 Unity 6000.3.23f1 工程，使用本仓库 Core、Assets、UI 三包及 uGUI Text。打开 `Assets/Bootstrap.unity`，先执行菜单 **UIDemo → Build and deploy SBP content**，再进入 Play。运行宿主始终使用真实 AssetBundle；未部署内容时明确报错，不回退到 AssetDatabase。

场景、Prefab、共享图片、ResourceGroups 和 meta 已保存。需要重新生成时先执行 **UIDemo → Create scenes and content**（会重建本 Demo 的场景及 Prefab），再构建。构建克隆 ResourceGroups 到独立的新目录，使用 AssetBuilder + AssetDeployment，不递增开发配置的版本。输出在 `BuildArtifacts/UIContent/<唯一目录>`，部署在 `Assets/StreamingAssets/AiNativeAssets`；生成的 Bundle 不纳入源码。

仓库根目录命令示例：

```powershell
unity run examples/unity/UIDemo -- -executeMethod AiNative.UIDemo.Editor.UIDemoBuilder.Create
unity run examples/unity/UIDemo -- -executeMethod AiNative.UIDemo.Editor.UIDemoBuilder.BuildContentSbp
unity run examples/unity/UIDemo -- -executeMethod AiNative.UIDemo.Editor.UIDemoBuilder.ReloadOn
unity test examples/unity/UIDemo --mode PlayMode --filter AiNative.UIDemo.Tests.UIDemoTests --output TestResults/uidemo.xml
```

`ReloadOff` 关闭 Domain Reload。实际验收在仓库外隔离副本执行，不更改用户正在打开的工程；上述输出必须改成新的路径，保留失败材料。完整包回归过滤 `AiNative.UnityCore.UI.Tests`，不要用宽过滤拉入需要 AssetsDemo Probe 的其他测试。

推荐在仓库根使用统一隔离入口（需要 Python 3.10+、Unity CLI 和已安装的工程指定 Editor）：

```powershell
python -m unittest discover -s scripts -p test_verify_unity_ui.py -v
python scripts/verify_unity_ui.py TestResults/ui-editor-<唯一后缀>
```

脚本拒绝已有输出目录；通过 unity_packages 复制三包与工程到独立短路径，重新生成 Demo 并构建/部署 SBP Bundle，再运行 Domain Reload 开/关各 UI EditMode、UI PlayMode 和 Demo PlayMode。源码复制指纹、生成后的 fixture 指纹分开保存；不使用开发目录已有 Bundle。XML 必须非空、无失败/跳过、满足已验基线最小数量，双重载测试名称集合须一致；实际数量由 XML 汇总。逐个校验完整日志、实际 Editor 版本、构建/部署哈希及工作区源码未在运行中变化。故障注入日志仅按对应 suite 的精确文本和出现次数放行。

`summary.json` 的 Passed 只代表自动矩阵；截图字段保持待可见 Editor 检查，不把测试成功冒充视觉验收。打开 `workspace.txt` 指向副本中的 `examples/unity/UIDemo`，按下文操作取得 screen 截图并独立记录；Pipeline 只作为该副本的观察工具。Task 8 已完成的矩阵和独立截图见[最终验收](../../../docs/unitycore-ui.md#task-8-层级顺序隔离验证与交付2026-11-13)。脚本失败保留 commands.jsonl、stdout/stderr、完整日志、已有 XML 和 failure.json，不发布成功摘要，也不清理失败现场。

## Windows Player 验收入口

在 Windows 上额外需要工程指定 Editor 的 Windows IL2CPP 模块、兼容的 Visual C++ 工具链，以及 Python Pillow（本机验证版本 10.4.0，用于完整解码截图）：

```powershell
python -m unittest discover -s scripts -p "test_verify_unity_ui*.py"
python scripts/verify_unity_ui_players.py TestResults/ui-players-<唯一后缀>
```

脚本在新隔离工程重新生成场景与 SBP 内容，依次构建 Windows x64 Release Mono、IL2CPP Player，采用 Minimal managed stripping，实际运行 Direct3D11。构建通过 Assets 的 BuildDeployedPlayer 保留 Bundle 引用类型；构建目录已存在时拒绝覆盖。正常交互启动不会创建验收对象，只有 `-uiSmoke -uiResult <新JSON路径>` 启用驱动。

每个 Player 执行十组检查：共享图片真实 Bundle、页面栈、双模态阻挡与焦点、缓存会话/LRU、过渡取消、Toast 到期/输入穿透、Clear 故障重试、Single 切场景/暂停动画、Trim 最后持有者、显式关闭资源归零。事件输入使用 RaycastAll 后 ExecuteEvents，不能冒充 OS 鼠标键盘；退出证据为 CloseAsync 后 Application.Quit，不包含任务管理器强杀。

输出保存命令/退出码、完整构建与 Player 日志、运行后端/版本报告、源码和脚本指纹、Player 文件哈希、内容哈希与 Home/双模态屏幕截图。实际可执行文件保留在 `workspace.txt` 指向的隔离目录 `TestResults/ui-players`。运行时后端必须与磁盘 Mono/IL2CPP 产物一致，部署 Bundle 必须逐个匹配本次构建。截图执行 PNG 完整性/解码检查，视觉正确性另行查看；不能仅凭 PNG 存在宣布通过。失败保留现场并仅写 failure.json。

## 原生输入与窗口关闭验收入口

```powershell
python scripts/verify_unity_ui_native.py prepare TestResults/ui-native-<唯一后缀>
python scripts/verify_unity_ui_native.py run TestResults/ui-native-<唯一后缀> --backend Mono
python scripts/verify_unity_ui_native.py run TestResults/ui-native-<唯一后缀> --backend Il2Cpp
```

追加 `--screenshots` 会传入 `-uiNativeScreenshots`：只读探针在真实输入后的状态稳定0.35秒、无挂起UI操作时，于帧末捕获实际Player客户端画面。PNG与同帧状态写入本轮目录，门禁校验Home、Gallery、双模态、Session 2/Other/Gallery四个有序视觉检查点、前序输入帧、图片尺寸和完整解码；仍需独立查看图像内容。截图包含Screen Space Overlay，不能用Camera RenderTexture或上一轮图片代替；它不包含Windows标题栏。标题栏专项必须真实点击右上角×并保存操作人确认，不能由截图或退出码单独推断，也不能以Alt+F4替代。

`prepare` 复用双后端 Release 构建及自动 smoke 门禁；`run` 启动对应 Player 并等待外部操作，**不会注入输入或调用退出**。用 Windows Computer Use 或人工执行：鼠标 Duplicate 确认 AlreadyOpen → 鼠标 Next → Return 到第三页 → Right 选择 Modal 再 Return → Return 开第二模态 → Return 返回第一模态 → Right 选择 Back → Return 关闭模态 → 鼠标 Clear → 鼠标 Scene → 鼠标 Next → 系统关闭窗口。每步等过渡结束并记录当前焦点和状态；截图能力可用时另存图像。系统关闭可分别验收标题栏按钮和原生 Alt+F4，必须记录实际执行方式，不能互相冒充。

只读 `UIDemoNativeProbe` 仅在传入 `-uiNativeResult <新JSONL路径>` 时启用；记录旧 Input Manager 的原始输入、焦点、UI/Assets 诊断及 Unity 退出回调，不调用 UI 命令、CloseAsync 或 Application.Quit。脚本核对后端和版本、完整状态顺序、输入计数、关闭前活会话与销毁后归零、完整日志和真实退出码。输入与退出的触发来源仍需独立核对操作记录及可用的截图，不能单凭 JSONL 证明。默认等待20分钟；超时杀进程仅为失败清理，绝不视作窗口退出验收成功。每个后端输出目录必须不存在，失败现场保留。

2026-11-16 实际执行采用用户鼠标点击、CUA 原生 Return/Right 和 Alt+F4，两个后端均通过。Windows 抓图持续超时，AX 标题栏关闭按钮点击返回 `coordinate input geometry is unavailable`，所以本轮没有原生交互截图，也不声明关闭按钮点击通过；完整范围见 [原生验收记录](../../../docs/unitycore-ui.md#windows-player-原生输入与系统窗口关闭2026-11-16)。

同日后续已用 `--screenshots` 补齐两后端真实原生会话的客户端截图，以及用户实际点击标题栏×退出。共34张PNG通过完整解码，8张代表图逐张检查；两进程均正常退出、资源归零。OS窗口抓图和自动点击标题栏的工具故障未据此视作修复；截图来源、用户确认和实际退出证据见[截图与标题栏验收](../../../docs/unitycore-ui.md#原生交互截图与标题栏关闭2026-11-16)。

## 加载与退出动画中关闭 Player

```powershell
python scripts/verify_unity_ui_native.py prepare TestResults/ui-shutdown-<唯一后缀>
python scripts/verify_unity_ui_shutdown.py TestResults/ui-shutdown-<唯一后缀> --backend Mono --scenario loading
python scripts/verify_unity_ui_shutdown.py TestResults/ui-shutdown-<唯一后缀> --backend Mono --scenario exiting
# 同样分别执行 --backend Il2Cpp
```

独立 `UIDemo.Acceptance` 程序集只在 Player 收到 `-uiShutdownScenario <loading|exiting> <新JSONL路径>` 时启用，与 smoke/native 驱动互斥。夹具通过宿主 API 建立场景，不声称这些准备步骤来自原生输入；退出由外部操作人点击系统窗口×，保留其确认。没有 Application.Quit、提前 CloseAsync 或 wantsToQuit 拦截。

- `loading`：Home/HUD 正常打开后请求 Gallery。包装真实 BundleBackend，原生资源成功返回后暂缓向 Assets/UI 交付，保持 Gallery Opening、UI 与 Assets pending。此处验证“等待加载结果交付时退出”，不声称物理磁盘请求仍在读取。Core 退休后才同步交付迟到结果，检查其被回收、候选不提交、所有权归零。Assets 的 internal factory 只向独立验收程序集开放，不向常规 Demo 或产品公共 API 开放。
- `exiting`：Gallery 入场/退出时长都为120秒，仅此验收配置生效。等待入场真正提交后保持 Gallery，由操作人点击 Back，等约1秒保存退出画面，再点击系统窗口×；收到关闭请求时必须仍为 Exiting，alpha 已下降且介于0和1之间。默认 Demo 仍使用0.35秒。

等待目标阶段的 `screenshot` 记录写出后再关闭窗口。`wantsToQuit` 记录实际关闭请求瞬间并始终返回 true；门禁拒绝在阶段之前/之后关闭、缺少挂起状态、迟到结果错误发布、残留所有权、错误日志和异常进程退出。截图为当轮帧末客户端视口，不含标题栏。runner最多等待3600秒；若超时被杀进程，只保留失败，不计作退出成功。由操作人发起Back避免异步消息往返消耗退出动画的操作时间。

## 操作与所有权

- Home → Next → Third 展示三页；Transparent 保留前页绘制，Back 返回。根页 Back 显示 NoBackTarget，Duplicate 显示 AlreadyOpen。
- Modal → SecondModal 展示双模态及遮罩；Back 逐层关闭并恢复焦点。HUD 可关闭再打开，Toast 穿透输入并在两秒后关闭。页面默认选择支持方向键与 Submit。
- CancelTransition 在入场期间取消，前页保持可用。缓存容量固定为 1；关闭再开命中同一实例，依次关闭不同窗口触发 LRU，Trim 释放缓存。
- 顶部宿主控制面板独立于 UI Root，提供 Clear、FaultClear、Trim、Scene、Hud；UI 故障停收时仍可重试 Clear。面板显示拒绝结果、异常摘要及页面/模态/缓存/挂起/失败/Bundle 数量；完整异常写入 Console。状态提示每帧更新，完整诊断快照按 unscaled 时间每 0.25 秒采样一次，不逐帧遍历组件；计数最多延迟一个采样周期，Toast 到期仍每帧检查。
- FaultClear 注入一次解绑失败，保留旧 Model；Clear 重试成功后才 Dispose 旧 Model、创建新 Model 并打开 Home/HUD。工厂预先显式注册，每次 Bind 读取宿主持有的当前 Model，运行中不重新注册定义。
- Model 持有异步加载的图片 lease，宿主先等待加载完成再打开窗口；Prefab 也引用同一图片，测试比较真实 Bundle 依赖与动态 lease 的身份。Presenter 只绑定/解绑同步事件，不启动不受跟踪的异步任务。
- Scene 使用 Single 加载 Other 场景，宿主、EventSystem、Core 和 UI Root 保留。宿主退出使用保存的 Core 引用关闭，不在 OnDestroy 访问 Core.Inst。

## 验证边界与采样

自动化集成测试使用部署 Bundle 和真实 Core/Assets/UI，点击采用 EventSystem.RaycastAll 命中后 ExecuteEvents；这证明事件路由，不能代替鼠标键盘输入后端验收。实际执行结果、独立审查、截图及输入记录见[UI 验收](../../../docs/unitycore-ui.md)。

截图使用 Game View/screen 捕获 Screen Space Overlay：Home 状态执行菜单 **UIDemo → Pause gallery transition for screenshot** 可在真实入场约 100 ms 暂停，截图后恢复 Play；保存 Home 稳定页与两页同时存在的入场中间帧，禁止用 Camera RenderTexture 代替。可见 Editor 验证鼠标点击 Next、键盘 Submit、双模态返回以及 Clear/Scene；核对页面计数、实际选择与异常日志。

性能另行人工采样：在相同 Game View 分辨率分别打开 Home、透明页和双模态，用 Frame Debugger 检查 draw call、Canvas 与材质身份；Profiler 比较静止/动画的 UI rebuild、CPU/GPU 及 overdraw。诊断计数不是批次或性能预测。Player、IL2CPP、移动端和设备安全区未由 Editor 测试替代。
