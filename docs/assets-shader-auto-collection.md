# Shader 自动收集与运行采集

入口在 ResourceGroups Inspector；先创建 `Create > AI Native > Assets > Shader Settings` 并绑定 `Shaders`。旧的 `Generate declared shader variant collection` 保持只生成 Required Variants 的行为。

## 静态扫描与合并生成

点击 **Generate merged shader variant collection**，选择一个新的 `Assets/*.shadervariants` 路径。工具按资源组构建计划扫描实际纳入的材质（含依赖和子资产），将材质 Shader 与启用关键字组合交给 Unity 的标准 SVC 校验，收集有效 PassType。它不按 Shader 名称猜测 URP 类型，也不枚举所有关键字排列。

随后合并 Shader Settings 的 **Runtime Collections** 与 **Required Variants**。关键字去重、排序后，按 Shader 对象身份、PassType 和关键字集合去重。Required Variants 仍是人工补充入口，自动生成不会改写它。Additional Shaders 是扫描上下文线索；只列出 Shader 不会展开其所有变体。

输出旁的 `.generation.json` 记录静态、录制、手动来源条数、重复数、最终条数和未能生成静态候选的材质。来源条数允许重复，最终条数才是集合中的唯一变体数。Unity 原生录制条目可能在重载或 Shader 修改后失效（本轮在 Shader Graph ShadowCaster 上复现）；此类条目记入 `RejectedRecordedVariants` 和 Notes，包含来源文件、Shader、Pass、关键字与校验原因，不计入输出或覆盖证明。Required Variants 的无效声明仍直接失败。生成失败不发布半成品；已有集合、meta 或报告不会被覆盖。

将生成集合加入资源组，构建后通过 `WarmupShadersAsync` 加载预热。更新输入后选择新路径生成，并替换资源组内旧集合引用；当前入口不就地更新已有集合 GUID，也不会自动部署内容。

## Editor Play 运行采集

1. 点击 **Begin shader recording (clear Unity tracking)**。此操作清空 Unity Editor 全局的当前变体跟踪列表，但不删除已保存的集合。
2. 进入 Play，实际运行需要覆盖的场景、材质关键字、灯光和质量配置。AssetsDemo 可分别使用 Forward 与 ForwardPlus 管线。
3. 退出 Play，点击 **Save shader recording and attach to original settings**，选择新路径保存。记录自动加入开始采集时那个 Shader Settings 的 Runtime Collections；切换 Inspector 选中对象不会改变归属。
4. 再点击 **Generate merged shader variant collection**。多次录制集合可以一起合并；重复组合只保留一份。

Begin 与 Save/Cancel 之间只能有一个工具会话；会话使用 SessionState 保存，可跨 Play Domain Reload，但不跨 Editor 进程重启。取消文件选择不会丢弃会话。Cancel 只放弃本工具会话，Unity 自身仍持续跟踪渲染。录制可能包含 Scene view、预览窗口以及资源组外的 Shader；合并会保留所选录制集合中通过标准 SVC 校验的条目，便于包含动态加载的材质。如果不需要某次录制，可从 Runtime Collections 移除引用，原始文件不会被删除。

记录来自实际 Editor 绘制，不是遍历 Renderer 后推测关键字。目前没有 Player 端记录导出功能。尚未执行的场景、平台、质量配置和运行分支不会因此获得覆盖；静态扫描也不补全全部管线关键字。

## 边界与兼容

- 标准 SVC 只存 Shader、PassType、Keywords，不存 PassName。不同 PassName 对应同一组合时会合并。
- 自动结果不成为裁剪白名单，不改变 Graphics 的 Preloaded Shaders，不开启自定义裁剪或自动预热。
- 自动或录制组合与显式 Exclusions 冲突时停止生成并报告 Shader/Pass/Keywords。对于限定 PassName 的规则采用保守冲突检查，因为 SVC 无法表达 PassName。调整规则或输入后重新生成，不会静默剔除并宣称覆盖。
- `Enable Processing` 仍仅控制受管 Bundle 构建中的自定义处理；扫描、录制和集合生成不要求开启。
- 公共包不依赖 URP 程序集。运行采集桥接 Unity Editor 内部 `ShaderUtil.ClearCurrentShaderVariantCollection` / `SaveCurrentShaderVariantCollection`；集合读取使用与 Unity Inspector 相同的序列化布局。原因是该版本没有对应公开录制/枚举接口，依赖集中在 `AssetShaderRecording` 与 `AssetShaderAutoCollection.Read`。接口不可用时明确失败，升级 Editor 后必须重验，不能把空输出当成功降级。
- Unity 参考实现：[ShaderUtil bindings](https://github.com/Unity-Technologies/UnityCsReference/blob/6000.3/Editor/Mono/ShaderUtil.bindings.cs)、[SVC Inspector](https://github.com/Unity-Technologies/UnityCsReference/blob/6000.3/Editor/Mono/Inspector/ShaderVariantCollectionInspector.cs)。

## AssetsDemo 验证

自动化用例位于 Assets 包的 `AssetShaderAutoCollectionTests` 与 `AssetShaderRecordingTests`。后者在 AssetsDemo 内创建独立配置，在没有 Required Variants 的情况下实际渲染 Keyword Probe、Lit、Unlit、Shader Graph，检查 Forward/Forward+ 图像像素、运行时 PROBE_GREEN 被录制、跨 Play 转换后可保存和合并，并使用 Builtin/SBP 两种后端构建后从 Bundle 加载集合执行 WarmUp。临时资产使用唯一目录；交互 Editor 中遇到用户未保存场景、或任何模式下已有录制时跳过；batch 验证只应在隔离副本执行，不破坏现场。

复现入口（建议仅隔离副本运行）：

```powershell
python scripts/verify_shader_collection.py TestResults/shader-collection-new
```

### 本轮验收（2026-11-11）

最终代码经独立 Reviewer 审查后重新验证，Unity 6000.3.23f1 / URP 17.3.0 / SBP 2.6.2，Windows Editor D3D11：

| 检查 | 实际结果 |
| --- | --- |
| 完整 EditMode / PlayMode（Domain Reload on） | 110 / 55 项通过 |
| 新增收集测试，Domain Reload on / off | 各 6 项通过，无跳过 |
| Forward / Forward+ 实际渲染 | 红→绿关键字像素与 Lit / Unlit / Shader Graph 像素断言通过 |
| 自动集合经 Builtin / SBP 构建 | 从真实 Bundle 加载并 WarmUp 成功，Bundle 已卸载 |
| 最终生成结果（两个重载设置） | 静态 23 条、原生录制 21 条、拒绝失效录制 1 条、重复 8 条，合并 35 条；手动补充为 0 |
| 完整进程日志 | 无 JobTempAlloc / TLS Allocator ALLOC_TEMP 警告 |
| 隔离复现脚本 | 全新副本双重载流程通过；末次路径安全修正后另行重跑上述完整回归与双重载收集测试 |

拒绝项为 Unity 原生记录的 `Hidden/Lit/ProbeGraph`、ShadowCaster、`_CASTING_PUNCTUAL_LIGHT_SHADOW _EMISSION` 组合；标准 SVC 校验判定不存在，因此没有宣称它获得预热覆盖。录制数量受实际运行的渲染路径和 Editor 状态影响，不要求每次都得到同样条数。

证据：[摘要](evidence/assets-shader-auto-collection/summary.json)、[源码指纹](evidence/assets-shader-auto-collection/sources.json)、[XML／脱敏日志／生成报告／构建分析／截图归档](evidence/assets-shader-auto-collection/evidence.zip)、[归档 SHA-256](evidence/assets-shader-auto-collection/files.json)。本地工作记录位于 `TestResults/shader-auto-20261111`，脚本首次复现位于 `TestResults/shader-collection-final-20261111`。

本轮没有重跑 Player／IL2CPP 矩阵，也没有验证 Player 端采集或通过鼠标操作 Inspector。截图与像素检查来自实际 Editor 渲染，构建后验证的是集合加载和预热；不将其描述为新一轮 Player 全变体渲染验收。保留原有配置和 StreamingAssets，不部署测试产物。
