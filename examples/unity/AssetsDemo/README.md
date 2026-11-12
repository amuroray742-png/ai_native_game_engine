# AssetsDemo

当前渲染配置为 URP 17.3.0 的 3D Forward（Editor 6000.3.23f1）；`Assets/Rendering/ForwardPlus.asset` 提供 Forward+ 验收。材质 GUID、共享贴图与地址保持不变。Shader 探针初始化用 `ShaderProbeSetup.Create`，集合和探针随 `BuildContent*` 的配置克隆分包。不要复用迁移前 Built-in 渲染内容作为 URP 验收材料。

本轮接口、生产规则、重建与验收范围见 [资源增强与 URP 示例](../../../docs/assets-enhancement-urp.md)。完整新矩阵加 `--renderers both --baseline-fixture`；Builtin/SBP 在此表示构建后端。历史验收不能代表本轮结果。

Unity 6000.3.23f1，Windows x64，直接引用仓库中的 UnityCore 与 Assets 包。打开 `Assets/Bootstrap.unity`，示例显示 A/B 两个共享材质、贴图的对象；点击 **Run cleanup checks** 执行池、场景、重启与关闭检查。`Assets/ResourceGroups.asset` 是可编辑配置。

Editor 中选中 `Assets/ResourceGroups.asset`，通过 **Editor Load Mode** 切换：`AssetDatabase` 直接读取源资源，无需构建或部署；默认的 `AssetBundle` 读取已部署内容。示例启动时调用 `EditorAssets.CreateConfigured(groups)`，切换后需退出并重新进入 Play。此开关不改变打包后端，Player 始终加载 Bundle。

以默认 Bundle 模式首次运行先构建内容（关闭这个工程的 Editor 后执行）：

```powershell
unity run examples/unity/AssetsDemo -- -executeMethod AssetsDemoSetup.BuildContent
unity open examples/unity/AssetsDemo
```

内容先构建到独立的 `BuildArtifacts/DemoContent/<id>`，再精简部署到被 Git 忽略的 `Assets/StreamingAssets/AiNativeAssets`。重新执行会校验后替换模块专用目录；请先退出 Play。原始报告和裁剪证据留在 StreamingAssets 外。

`AssetsDemoSetup.BuildMono` 构建 Development Mono；`BuildIl2Cpp` 构建 IL2CPP Player。需要安装当前 Editor 的 Windows IL2CPP 模块及兼容的 Visual C++ 工具链。构建入口调用 AssetPlayerBuildSupport.BuildDeployedPlayer，按部署快照使用原生 Manifest 或 SBP link.xml 保留 Bundle 引用的脚本类型。SBP 还可用 BuildMonoStandard / BuildIl2CppStandard 验证标准 BuildPipeline 入口。示例以 ScriptableObject 加载并反射验证 DemoData，不直接引用该运行时类型。输出在仓库 `TestResults/assets-players`。

完整的隔离验收命令：

```powershell
python scripts/verify_unity_assets.py TestResults/assets-verification-new --backend both --players
```

目标目录必须不存在。脚本复制工程和两个源码包，重新构建内容，在开关 Domain Reload 两种设置下分别运行 EditMode/PlayMode 测试，再构建并运行两种 Player。保留源文件摘要、命令、XML、日志、原始 Bundle 报告、Player 结果与渲染截图。测试会生成故意重复或损坏的内容，仅在隔离副本/临时目录使用。

`BundleReportProbe.Run` 是原生 A/B/shared 最小报告探针，输出 `TestResults/assets-probe/bundles`。完整构建的严格归属检查由 `AssetBuilder` 执行。该样例不参与模拟内核或 PlayHost 的玩法装配。

示例使用全局 `FullPath` 地址，如 `Assets/Probe/A`、`Assets/Probe/Content`。从旧版本升级时必须归档旧内容后重新构建，原来的 `a`、`scene` 等手填地址不再生成。若改为全局 `GroupAndAssetName`，也需同步调整示例调用。

默认 SBP 2.6.2。`AssetsDemoSetup.BuildContentBuiltin` 和 `BuildContentSbp` 可只覆盖本次构建后端，不改配置资产；输出目录仍必须是新目录。需要排除本地示例配置试验对标准验收的影响时，加 `--baseline-fixture`，仅隔离副本恢复已提交的 ResourceGroups 配置。

Inspector 手工构建：选中 ResourceGroups，设置 Version（默认下一版本 1）与 Output Directory（默认 `{projectDir}/BundleBuildOutput/{version}`），预览路径后点击构建，无需选择目录。成功后版本加 1 并保存。示例 `BuildContent*` 使用配置克隆，独立构建后调用 CopyLatestToStreamingAssets，只递增克隆的内存版本，不修改原配置。手工构建的产物通过 Inspector 的 Copy latest build to StreamingAssets 按钮部署，不再手动复制整个目录。

`--backend both --players` 现在对每个内容后端分别验证 Mono 与 IL2CPP Player：Builtin 走部署辅助入口，SBP 走标准 BuildPipeline 入口，并检查 Player 运行时目录仅有 catalog 和 Bundle。

本示例关闭 `PlayerSettings.stripUnusedMeshComponents`（Optimize Mesh Data），规避 Unity 6000.3.23f1 在 Builtin Bundle 的 URP Shader 顶点通道分析中遗留 JobTempAlloc 分配的问题。保留未使用的网格通道可能增加包体；不关闭 Shader 变体裁剪、不修改公共资源包的释放逻辑。设置已提交，`AssetsDemoSetup.Create` 与 URP 迁移入口会重现它；已有副本可执行 `AssetsDemoSetup.ConfigureBuildSettings`。验收脚本检查完整进程日志，包括测试报告写出后的退出阶段，发现原生临时分配警告即失败。定位与对照见 [JobTempAlloc 记录](../../../docs/assets-jobtempalloc.md)。

Shader 自动收集入口与操作步骤见 [Shader 自动收集与运行采集](../../../docs/assets-shader-auto-collection.md)。ResourceGroups 的 Generate merged shader variant collection 合并材质扫描、Editor Play 录制和手动 Required Variants；生成的集合需要加入资源组后重新构建。录制与测试不更新本地 StreamingAssets。

Shader 变体保留／裁剪对照使用独立场景 `Assets/ShaderVariantDemo/Demo.unity`，菜单入口为 `AI Native/Shader Variant Demo`。从 Bundle 持续显示 ShaderProbe，通过手动修改配置观察保留绿色、真实缺失粉色及恢复绿色；[操作步骤与验收边界](../../../docs/assets-shader-variant-demo.md)。
