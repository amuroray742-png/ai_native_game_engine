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

## 操作与所有权

- Home → Next → Third 展示三页；Transparent 保留前页绘制，Back 返回。根页 Back 显示 NoBackTarget，Duplicate 显示 AlreadyOpen。
- Modal → SecondModal 展示双模态及遮罩；Back 逐层关闭并恢复焦点。HUD 可关闭再打开，Toast 穿透输入并在两秒后关闭。页面默认选择支持方向键与 Submit。
- CancelTransition 在入场期间取消，前页保持可用。缓存容量固定为 1；关闭再开命中同一实例，依次关闭不同窗口触发 LRU，Trim 释放缓存。
- 顶部宿主控制面板独立于 UI Root，提供 Clear、FaultClear、Trim、Scene、Hud；UI 故障停收时仍可重试 Clear。面板显示拒绝结果、异常摘要及页面/模态/缓存/挂起/失败/Bundle 数量；完整异常写入 Console。
- FaultClear 注入一次解绑失败，保留旧 Model；Clear 重试成功后才 Dispose 旧 Model、创建新 Model 并打开 Home/HUD。工厂预先显式注册，每次 Bind 读取宿主持有的当前 Model，运行中不重新注册定义。
- Model 持有异步加载的图片 lease，宿主先等待加载完成再打开窗口；Prefab 也引用同一图片，测试比较真实 Bundle 依赖与动态 lease 的身份。Presenter 只绑定/解绑同步事件，不启动不受跟踪的异步任务。
- Scene 使用 Single 加载 Other 场景，宿主、EventSystem、Core 和 UI Root 保留。宿主退出使用保存的 Core 引用关闭，不在 OnDestroy 访问 Core.Inst。

## 验证边界与采样

自动化集成测试使用部署 Bundle 和真实 Core/Assets/UI，点击采用 EventSystem.RaycastAll 命中后 ExecuteEvents；这证明事件路由，不能代替鼠标键盘输入后端验收。实际执行结果、独立审查、截图及输入记录见[UI 验收](../../../docs/unitycore-ui.md)。

截图使用 Game View/screen 捕获 Screen Space Overlay：Home 状态执行菜单 **UIDemo → Pause gallery transition for screenshot** 可在真实入场约 100 ms 暂停，截图后恢复 Play；保存 Home 稳定页与两页同时存在的入场中间帧，禁止用 Camera RenderTexture 代替。可见 Editor 验证鼠标点击 Next、键盘 Submit、双模态返回以及 Clear/Scene；核对页面计数、实际选择与异常日志。

性能另行人工采样：在相同 Game View 分辨率分别打开 Home、透明页和双模态，用 Frame Debugger 检查 draw call、Canvas 与材质身份；Profiler 比较静止/动画的 UI rebuild、CPU/GPU 及 overdraw。诊断计数不是批次或性能预测。Player、IL2CPP、移动端和设备安全区未由 Editor 测试替代。
