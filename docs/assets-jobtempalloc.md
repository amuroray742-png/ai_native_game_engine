# AssetsDemo JobTempAlloc 定位与规避

2026-11-11，Windows / Unity 6000.3.23f1 / URP 17.3.0 / SBP 2.6.2。此记录补充[资源增强与 URP 验收](assets-enhancement-urp.md)，不改写旧日志。

## 原因与边界

以 `-diag-job-temp-memory-leak-validation` 重跑完整 EditMode，104 项通过，但进程退出仍报告 JobTempAlloc。555 个分配块全部包含 `ShaderCompilation::CompileVariantJob`；其中 114 个包含完整调用链：

```text
BuildAssetBundles
AssetBundleBuilder::BuildCustomAssetBundle
ComputeBuildUsageTagOnObjects
ComputeRendererSupportedMeshChannelsAndReadable
CalculateRequiredVertexComponentsForRenderer
ShaderCompilation::CalculateUsedVertexComponents
ShaderCompilation::CompileVariants / ProcessCompilationResults
ShaderCompilation::CompileVariantJob
```

栈中可见 `CompleteFenceInternal`。未发现 `GetPlayerDependenciesForObjects` / `GetPlayerObjectIdentifiersInAsset` 分配栈；这些接口返回普通托管数组，SBP 自身也没有对返回值执行 Dispose。

单独运行 `RealDuplicatedDependenciesFailArtifactVerification` 同样复现。该测试直接调用 Unity 的 `BuildPipeline.BuildAssetBundles`，没有创建资源模块、Scope 或资源句柄，也未开启自定义 Shader 处理。仅在隔离测试 SetUp 中关闭 `PlayerSettings.stripUnusedMeshComponents`、TearDown 恢复原值后，同一测试通过且退出无 JobTempAlloc。临时对照修改没有进入产品或正式测试代码。

因此，本次已复现警告来自 Unity 原生构建期 Shader 顶点通道分析路径，不能归因于资源模块的资源句柄未释放。此结论限定于这组警告，不代表整个 Unity Editor 所有原生分配都不存在泄漏。

## 修复方式

AssetsDemo 的 `StripUnusedMeshComponents` 通过 Editor API 设为 0 并保存；初始化与 URP 迁移入口同步保持该设置。GridGame、PhysicsSandbox 不更改此选项，公共资源包也不覆盖消费者的 PlayerSettings。

这是锁定 Editor 版本下的工程规避方案，没有声称修复 Unity 的原生实现。它保留未使用的网格顶点通道，可能增加包体，但不改变网格形状、Shader 变体裁剪或运行时所有权。Unity 对该选项语义的说明见[官方 API](https://docs.unity.com/ja-jp/engine/6000.3/script-reference/unityeditor/playersettings/stripunusedmeshcomponents)。未来升级 Editor 后，应在隔离副本重新开启选项、重跑同一原生构建及退出检查，再决定是否撤销规避。

`verify_unity_assets.py` 现在在每个 Editor / Player 进程退出后检查完整日志。即使 NUnit XML 或 Player JSON 已报告成功，只要日志有 JobTempAlloc / TLS 临时分配警告，验收仍失败；不通过屏蔽日志、强制 GC 或延迟退出来隐藏问题。

## 证据

本地原始材料保存在 `TestResults/jobtempalloc-20261111`：`diagnostic` 为完整原始复现，`native-only` 为单测试复现，`no-strip-r2` 为唯一开关对照。前两次启动未完成测试，保留为失败尝试，不计入验收。

| 本轮实际检查 | 结果 |
| --- | --- |
| 原设置完整 EditMode / 单原生构建测试 | 104 / 1 项通过，各有 3 次退出 JobTempAlloc |
| 仅关闭网格通道裁剪的单测试对照 | 1 项通过，0 次警告 |
| 正式设置下完整 EditMode / PlayMode | 104 / 55 项通过，0 次警告 |
| 新 Builtin / SBP Bundle + Windows Mono D3D11 Player | 两组通过严格 Shader 匹配、像素、共享资源、池、场景、重启及清理检查 |
| 完整日志检测 | 上述最终 8 份测试／构建／Player 日志均无 JobTempAlloc / TLS 临时分配警告；旧失败日志能被新检测器拒绝 |

本轮补测使用 Forward、已有隔离副本的关闭 Domain Reload 设置；没有将这次补测扩大表述为重跑 IL2CPP、Forward+ 或两种 Domain Reload 全矩阵。那些范围的先前通过记录仍单独保留。新两组 Bundle 文件总大小分别为 438371 / 438462 bytes，仅表示本轮标准夹具文件成本。新产物仅部署到隔离工程与验证 Player；用户工程的本地 StreamingAssets 保留此前已验证的部署。

归档：[结果摘要](evidence/assets-jobtempalloc/summary.json)、[证据 ZIP](evidence/assets-jobtempalloc/evidence.zip)、[文件摘要](evidence/assets-jobtempalloc/files.json)、[源码指纹](evidence/assets-jobtempalloc/sources.json)。ZIP 保留诊断栈、XML、构建／Player 日志、截图与 catalog；删除临时认证参数及原生内存的十六进制内容转储，原始日志只留本地。

独立生产审查者只读复核了分配栈、开关对照、持久化设置、模板一致性及日志检测，无剩余阻断项；最终测试由主 Agent 实际执行。此前未完成的原生鼠标 Inspector 验收仍按原文保留，不据本次修复宣称完成。
