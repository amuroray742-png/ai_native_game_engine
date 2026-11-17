# UnityCore UI

Unity 6000.3.23f1 的 uGUI 应用服务；单向依赖 UnityCore、Assets、UniTask 和 uGUI，不持有模拟状态或推进 Tick。

宿主提供覆盖整个运行周期的 EventSystem，先登记 AssetsModule，再登记 UIModule。停止时显式 Register 窗口定义，Presenter 工厂通过闭包取得业务依赖；不自动扫描或绑定。

- UIOptions 可显式配置 referenceResolution、matchWidthOrHeight、modalMaskColor；默认 1920×1080、0.5、黑色 alpha 0.5。分辨率有限且为正，匹配值及颜色各分量有限且位于 0..1。
- 子 Canvas 必须继承 Root 排序（overrideSorting=false）；绑定前、激活/恢复后及打开提交前拒绝独立排序并回滚。运行期间不得改写此约束，框架不逐帧扫描修复。
- Page 用 PushAsync，HUD/Modal/Overlay 用 OpenAsync；同一 View 类型只允许一个实例。
- HUD/Overlay 的稳定层级按成功提交顺序排列，缓存重开同样置于层内最上方。入场中候选暂置顶，取消不会重排已有窗口；BringToFront 可显式调整已打开窗口。Page/Modal 顺序由导航管理。
- Open token 只控制提交前，成功后取消不关窗。Close 成功表示按策略缓存或实际释放；Release 仅用于已关闭窗口和失败清理。
- Clear 成功后才可释放或替换旧 Model；失败保持停收，可重试。清理回调须幂等、支持部分完成和 View 已销毁后的解绑。
- UIOperationException.Committed 表示请求是否提交，提交后故障不可盲目重试；CleanupHandle 用于释放失败的重试。
- 仅支持 Unity 主线程。外部生命周期/焦点/模态回调内修改 UI 返回 ReentrantCall。Overlay 不交互。
- GetDiagnostics 是按需完整组件快照，不是每帧轻量计数器，也不预测渲染批次。Demo 每 0.25 秒按 unscaled 时间采样；框架的稳定屏幕/外部销毁观察路径不逐帧创建空列表或安全区闭包。

完整[设计](../../docs/unitycore-ui-design.md)、[契约与验收](../../docs/unitycore-ui.md)和[UIDemo](../../examples/unity/UIDemo/README.md)。

仓库根执行 `python scripts/verify_unity_ui.py TestResults/ui-editor-<唯一后缀>`，在独立短路径副本生成 Demo、构建真实 SBP Bundle，运行双 Domain Reload 的精确 UI/Demo 矩阵；保存 XML、完整日志、部署内容及源码指纹。输出目录必须不存在，失败材料不覆盖。可见 Game View 截图与性能采样边界见 UIDemo README。
