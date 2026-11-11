# 资源增强与 URP 示例

本轮不改变模拟、Tick、存档及回放协议。Windows 示例统一使用 Unity 6000.3.23f1、URP/Core/Shader Graph 17.3.0、SBP 2.6.2。`Builtin` 是 Bundle 构建后端名称，与 Built-in 渲染管线不同。公共 Assets 包没有 URP 程序集依赖。

## 加载与所有权

```csharp
var assets = new AssetsModule(contentRoot, new AssetRuntimeOptions(4, 2));
// 注册并启动模块后：
var scope = assets.CreateScope("screen");
var batch = await scope.PreloadAsync(new[] {
    new AssetLoadRequest("Assets/Probe/A", typeof(GameObject)),
    new AssetLoadRequest("Assets/Probe/SpriteSet", typeof(Sprite), "icon")
}, new AssetLoadOptions(AssetPriority.High));
GameObject prefab = batch.Get<GameObject>(0);
Sprite sprite = batch.Get<Sprite>(1);
await batch.CloseAsync();
await scope.CloseAsync();
```

批次按输入索引保存结果，重复项共享底层请求；空批次合法。关闭批次或父 Scope 后禁止访问。失败／取消撤销本批消费者，保留其他消费者，等待已启动工作收尾后报告首个失败及清理错误。进度报告在主线程调用 `IProgress.Report`；请使用同步实现（例如 UniTask 的 Progress），不要使用会自行异步投递并抛出异常的回调实现。同步通知异常进入诊断，不改变加载结果。

旧 `LoadAsync`、`LoadSubAssetAsync` 及构造入口保留。优先级入口命名为 `LoadWithOptionsAsync`、`LoadSubAssetWithOptionsAsync`，避免 `LoadAsync(address, default)` 的源码歧义。Editor 工厂接受相同运行配置：`CreateConfigured(..., options: options)` 或直读工厂 `CreateWithOptions(groups, options)`；旧 `Create(groups, default)` 仍表示默认加载模式。配置在创建时固定，默认最多 4 个原生加载并发、每帧 2 次受管实例创建；池预热共用额度。依赖等待不占原生加载额度，依赖可并行。优先级不抢占已开始工作，同级 FIFO；连续优先调度 8 次后让最旧等待项获得一次机会。

正常关闭等待消费者及底层工作；强退隔离代际并继续观察原生完成。无消费者但尚未收尾的请求不会被新请求复活：新请求等待旧记录清理再重新获取，避免同一原生 Bundle 并行重复加载。所有调用和取消均遵守原有主线程契约。

## 诊断

`GetDiagnostics()` 保留字符串列表，并新增不可变 `Ownership` 快照。每个运行代际、Scope、消费者请求、底层请求、句柄、实例、场景、池及租借具有稳定标识。资源键与 Bundle 依赖连接持有链，不扫描 API 之外的任意对象引用。

快照包含排队／加载耗时、共享命中、失败与取消操作数、并发和各类记录峰值。失败／取消统计同时涵盖消费者和底层操作，**不是唯一失败资源数**。完成历史上限 256 条，错误上限 32 条；快照只保存值和字符串，不额外持有 Unity 对象。

诊断窗口支持搜索、状态筛选、持有链、基线差分及 JSON 导出。跨代 ID 不直接比较。`RefreshDiagnosticsMemory()` 或窗口刷新按钮显式采样对象内存；窗口每帧绘制不遍历采样。Bundle 文件大小、依赖闭包文件成本与对象内存分开表示。

## 生产规则与报告

每组 `Filter` 提供排除目录与主资源类型过滤（包含派生类型）。`PlatformFilters` 按 BuildTarget 分别覆盖目录或类型字段，未覆盖字段继承通用配置。只过滤目录自动收集；显式条目与必要依赖保留。Inspector 平台预览复用 `AssetBuildPlan.Create(settings, target)`，非 Windows 只预览，构建仍拒绝不支持目标。

完整构建发布 `build-analysis.json`（格式版本 1），含源 GUID、路径、地址、归属、依赖摘要／文件信息、Bundle SHA-256／依赖／大小、过滤及依赖重新纳入原因。地址成本按 Bundle 依赖闭包去重计算，表示文件成本，不代表运行内存。单包和总包体预警阈值默认 0（关闭），超限只警告。

Inspector 的 Compare build reports 或 `AssetBuildAnalysis.BatchCompare -assetBefore <报告> -assetAfter <报告>` 显式比较两次报告。GUID 用于识别移动和归属变化，Bundle SHA-256 判断产物变化；管线指纹、图形 API 和包版本变化单独列出。旧内容仍能运行，缺少分析报告需重建分析材料。报告不进入精简 StreamingAssets。

## Shader 流程

创建独立 `AssetShaderSettings` 并由资源配置显式引用。默认关闭自定义处理，运行时预热也必须显式调用。候选扫描区分材质关键字、管线依赖线索、用户补充与必需声明，不把材质扫描当作完整白名单。通过实际 Shader 身份处理 Lit、Unlit、Shader Graph。

扫描包括 Graphics 默认管线、Standalone 启用的 Quality 管线、显式补充管线及公开依赖；报告包含全局设置与 Graphics／Quality 配置指纹。动态创建或静态不可发现的 Shader/组合需要用户声明。公共包只依赖 RenderPipelineAsset 等通用接口。

`RequiredVariants` 明确 Shader、PassType、可选 PassName 和关键字。Generate shader collection 生成标准 ShaderVariantCollection，将其作为普通资源加入组。标准 SVC 不编码 Pass 名称；名称用于构建观察与裁剪冲突校验。SRP 的 Forward、Shadow、Depth 等实际 Pass 名称分别记录；SRPDefaultUnlit 使用对应的 `ScriptableRenderPipelineDefaultUnlit` 类型。

排除规则只删除明确匹配 Shader／Pass／必须包含与不得包含关键字的变体，未知组合保留。与必需声明冲突时拒绝构建。上下文只在本次受管 Bundle 构建内生效，finally 恢复；不修改全局 Graphics，不绕过 URP 自身裁剪。启用处理时关闭本次 SBP 缓存复用并记录原因，不清空全局缓存。Shader 报告区分观察、保留、剔除与规则命中；没有观察到的必需变体不能宣称保留。

`WarmupShadersAsync(addresses)` 加载并持有集合，集合间让出一帧，返回持有集合的批次。失败或取消释放本次所有权；Scope 关闭也取消并等待预热。单次 `WarmUp()` 不可中断，预热不保证消除所有首次绘制卡顿。

## 示例与重建

三个示例均为 URP 3D Forward。GridGame 保留正交相机、SpriteRenderer 与顶点色透明 Sprite Shader；PhysicsSandbox 显式注入共享 Lit 材质，颜色通过 `_BaseColor` 更新。AssetsDemo 保留共享材质 GUID／贴图，并提供 Lit、Unlit、带关键字 Shader Graph、Forward+ 配置与真实 Bundle 像素探针。

管线资产由 Editor API 创建，不手写序列化 YAML。`scripts/migrate_examples_urp.py` 同步三个工程的包声明与 Editor helper；随后在各工程运行 `ExampleUrpSetup.Apply`。该入口拒绝 Play 或未保存场景，绑定 Graphics 与所有 Quality 配置。AssetsDemo 的夹具初始化为 `ShaderProbeSetup.Create`；`ExampleUrpSetup.ForwardPlus` 切换该示例的验收配置，默认仍用 Forward。从干净 checkout 已有提交的管线资产，无需重新迁移才可打开。

```powershell
python scripts/verify_unity_assets.py TestResults/urp-new --backend both --players --renderers both --baseline-fixture
python scripts/verify_assets_builtin_pipeline.py TestResults/builtin-new
python scripts/unity_grid.py verify --output TestResults/grid-urp-new
python scripts/unity_physics.py verify --output TestResults/physics-urp-new
```

验证工程保留在短路径中，位置写入各输出目录 `workspace.txt`，规避 Unity Windows Shader 导入器长路径问题。Built-in 测试在隔离目录使用固定历史夹具及当前公共包，不增加第四个示例。URP Player 开启严格变体匹配，固定 D3D11，检查错误日志、像素、共享身份与释放；GridGame／PhysicsSandbox 仍仅声明 Windows Editor 范围。

验收结果以本轮 XML、日志、截图和报告为准；执行命令不等于通过。旧阶段证据保留历史含义，不替代本轮验收。当前执行记录位于 `TestResults/assets-enhancement`，本地原始配置与 StreamingAssets 备份位于 `TestResults/assets-enhancement-original`；未通过隔离验证的内容不得部署冒充新验收产物。

## 本轮本地验收记录（2026-11-10）

归档：[结果摘要](evidence/assets-enhancement-urp/summary.json)、[完整证据 ZIP](evidence/assets-enhancement-urp/evidence.zip)、[归档文件摘要](evidence/assets-enhancement-urp/files.json)、[各隔离工程的包源码指纹](evidence/assets-enhancement-urp/package-sources.json)、[交付源码指纹](evidence/assets-enhancement-urp/current-sources.json)。ZIP 保留 XML、命令、日志、锁定版本、管线配置、完整构建分析和 Shader 报告、Player 文件清单与截图；日志中的临时启动认证参数已脱敏，原始本地日志仍在 TestResults。

| 范围 | 实际结果 |
| --- | --- |
| AssetsDemo URP Forward / Forward+ × Builtin / SBP × Domain Reload on / off | 8 组各 EditMode 102、PlayMode 55，全部通过 |
| 上述两种渲染配置 × 两种构建后端 × Mono / IL2CPP | 8 个 Windows D3D11 Player 全部通过；严格变体匹配、错误日志、Lit / Unlit / Shader Graph、关键字切换、真实 Bundle 像素与清理验证 |
| 补充生产／诊断检查 | 7 项通过，含包体预警、去重依赖成本、快照导出／差分、Shader 上下文恢复和未观察变体 |
| 最终 Editor 工厂兼容性修正后 | 完整 EditMode 104 项通过，包含新增 `Create(groups, default)` 编译回归；无 URP 夹具以当前源码再次编译并通过 55 项 PlayMode |
| 隔离 Built-in 渲染夹具 | 未安装 URP，Builtin / SBP 均构建成功，最终各 55 项 PlayMode 通过；较早的 54 项记录保留历史 |
| GridGame Windows Editor | 双重载各 7 项、同域重入通过；棋盘颜色像素通过；四条快慢表现轨迹与 CLI 的 8 Tick / 16 检查点一致 |
| PhysicsSandbox Windows Editor | 双重载各 27 项及退出／重入检查通过；暂停输入、90 / 91 Tick 续跑与十组 CLI 比较一致 |
| 当前本地 ResourceGroups | 在隔离工程以 SBP 重建并通过 Mono 严格变体 Player；随后通过 AssetDeployment 部署 version 4，10 个运行文件共 442093 bytes，逐文件 SHA-256 已核对 |

矩阵以每个目录 `sources.json` 的固定源码为准。矩阵副本创建后增加包体预警测试、诊断窗口换行显示，并将新增直读 Editor 工厂改名为 `CreateWithOptions`，消除旧 `default` 调用的歧义；运行时及 Shader／构建行为未再变化。这些变更经独立复核，最终完整 EditMode 104 项及无 URP 编译／55 项 PlayMode 通过，证据见 [最终补测 ZIP](evidence/assets-enhancement-urp/final-recheck.zip) 与 [补测文件摘要](evidence/assets-enhancement-urp/final-recheck-files.json)。

运行时与生产／迁移代码分别接受独立只读复审，修复后复核无剩余代码阻断项；审查记录见 [独立审查](evidence/assets-enhancement-urp/review.md)。未新增或声称执行 CI、分析器、其他平台、Deferred、2D Renderer、XR 或 HDRP 验收。

实际图片：[GridGame](evidence/assets-enhancement-urp/GridGame.png)、[PhysicsSandbox](evidence/assets-enhancement-urp/PhysicsSandbox.png)、[AssetsDemo](evidence/assets-enhancement-urp/AssetsDemo.png)、[Forward+ SBP IL2CPP Shader 探针](evidence/assets-enhancement-urp/ShaderProbe.png)。修复了截图暴露的 Sprite tint 漏乘问题；Shader Graph 探针使用较暗底色验证模板 `_EMISSION` 选择贴图分支后的绿通道变化，避免白色饱和掩盖关键字行为。

Editor 工具已实际显示 Inspector／分包窗口，并通过 Editor 菜单执行配置验证；诊断已执行加载、显式内存采样、JSON 导出与关闭归零。示例采样 GameObject 为 344 bytes、Sprite 为 1468 bytes，仅指这两个对象的 Profiler 测量，不代表整个依赖的内存。**原生鼠标点击验收仍未完成**：界面工具的窗口捕获超时／输入几何不可用；不把 Editor API 操作冒充鼠标交互通过。

2026-11-10 的部分批量构建测试退出日志含 `JobTempAlloc`，当时未定位；2026-11-11 后续原生栈与开关对照将其定位到 Unity Builtin Bundle 构建的 Shader 顶点通道分析，工程规避与新回归另见 [JobTempAlloc 记录](assets-jobtempalloc.md)。旧日志原样保留，测试及受管资源所有权检查通过不等于声明整个 Editor 进程没有原生内存泄漏。

本地部署来源和新旧摘要见 ZIP 中 `local-config-validation/deployment-provenance.json`。部署前额外备份位于 `TestResults/assets-enhancement/local-config-validation/streamingassets-before`。原 `AiNativeAssets~` 保留，ResourceGroups、PackageManagerSettings 和本地 SBP 配置与初始备份逐字节一致；部署未改写原配置的下一版本／最近构建记录，也未改变其 AssetDatabase 调试模式。
