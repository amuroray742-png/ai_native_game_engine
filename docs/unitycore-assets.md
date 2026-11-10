# UnityCore Assets：资源所有权与零冗余分包

提交前进一步复审发现并修复了回调取消、销毁时序、失败关闭失去所有者和 CRC 零值校验等问题，最新结果见 [提交前复审](unitycore-assets-review.md)。下述首次验收保留为历史记录。

## 边界与任务

`com.ainative.unitycore.assets` 是依赖 UnityCore 的 Unity 应用资源包，版本 0.1.0，Unity 6000.3.23f1、Windows x64。沿用 UniTask，不引入 Addressables、YooAsset 或第三方构建库。具体使用见[包文档](../packages/com.ainative.unitycore.assets/README.md)。

资源包不依赖模拟内核、Hosting、Jolt 或 Example，不持有权威游戏状态。资源异步操作、实例与场景的生命周期独立于 PlayHost 的模拟 Tick。目标是本地资源、自动共享依赖、可追踪所有权和真实 Player 闭环；远端更新、加密、跨平台及完整场景切换系统不在本轮。

主 Agent 实现和集成，独立 Reviewer 只读审查，审查修复后重跑受影响检查。单一失败遵循三轮假设/修改/验证预算；失败报告保留，不覆盖旧证据。

## 零冗余契约

一次受管 Bundle 构建集合内，可独立打包的同一源 GUID 只有一个物理包所有者；同文件子资源整体归属。地址由全局规则从源路径或组名/资源名生成，与物理分包分离；相关路径或名称变化时地址也变化。显式归属优先，否则按原始引用组集合提取共享包。单一场景组的外部依赖也单独进入普通包。不同 GUID 内容相同不自动合并。

构建前生成完整依赖闭包和归属，检测循环。构建后通过 BuildReport.packedAssets 的 shortPath 与 GetFiles 文件名唯一匹配，定位实际 Bundle；Unity 6000.3 不再允许使用旧的 packed.file 索引。实际依赖与计划比对，源资产重复或证据缺失阻止发布。

场景内嵌对象不作为独立源 GUID 去重。场景外部资源必须有归属证据，实际场景路径由 GetAllScenePaths 校验。脚本、Unity 内置对象、AssetBundle 元数据，以及零 GUID 且类型明确的场景 PreloadData/AssetBundle 元数据单列排除，不用未知源路径兜底。这里不承诺 Player 内置数据与 Bundle 之间的全局零重复。

实际构建产物包含 catalog、原生 Manifest、原始 BuildReport、计划/实际归属 TSV、排除项、错误和文件摘要。构建暂存目录位于 BuildArtifacts/Assets，避免导入半成品；Unity 不允许直接构建到 Library。成功后才在同卷发布到不存在的目标目录。

## 运行与清理

每轮模块持有固定目录及独立运行状态。每个消费者持有独立句柄，共享底层请求。资源、在途操作、实例、场景和池内对象共同持有 Bundle 依赖；先释放依赖使用者，后释放其依赖。不使用 Unload(false)、全局卸载或隐式缓存。

作用域等待已取消但不能中断的底层请求收尾。句柄释放立即解除所有者登记；Unity 实例在实际销毁后才释放资源。未激活对象通过额外销毁观察补足 Unity 不发送 OnDestroy 的情况。池按实际销毁移除活跃/闲置对象，租约绑定一次借用身份，回调与激活后重新检查有效性。

场景句柄从 sceneLoaded 获取实际身份，拒绝已加载或在途的同路径请求；外部卸载同步释放登记。不得在受管加载期间另外用 SceneManager 并发加载同一路径。取消已发起的加载时，调用者取消等待，内部接收迟到场景并卸载。

UnityCore 新增可选 IAsyncShutdownModule，正常关闭按逆序异步收尾后同步 Shut，失败也继续其他模块清理。强制退出令牌独立于运行取消令牌，打断等待并执行同步隔离；旧操作不得修改新一轮。应用强制终止不能保证引擎原生操作完成，只保证不再发布旧结果。纯 C# 内核和 v2 协议不变。

## 验证

AssetsDemo 是独立、真实的资源示例，避免为资源验收引入 Jolt 依赖。测试覆盖真实 Bundle 共享身份、取消与迟到操作、池预热/归还/外部销毁、场景、脚本类型、音频及 Sprite 子资源。构建负例包括真实重复依赖产物、缺失报告、重复地址、循环目录、发布目标已存在等。

2026-11-09 首次交付候选在独立源码副本、Unity 6000.3.23f1、Windows x64 执行：

| 检查 | 实际结果 |
| --- | --- |
| Domain Reload 开启 | 40 项 PlayMode、34 项 EditMode 全部通过 |
| Domain Reload 关闭 | 40 项 PlayMode、34 项 EditMode 全部通过 |
| 原生内容构建 | 7 个 Bundle、6 个公开地址；报告覆盖 7 个普通源 GUID，无重复所有者；场景路径独立核验 |
| 文件摘要复核 | 所有目录 Bundle 的 SHA-256 与产物一致 |
| Windows Mono Development Player | 构建成功，真实 Bundle 冒烟退出码 0，截图已检查 |
| Windows IL2CPP Player | 构建成功，真实 Bundle 冒烟退出码 0，截图已检查 |

Player 验证 A/B 的材质与贴图对象身份、释放 A 后 B 的依赖存活、Bundle 中 ScriptableObject 脚本类型、池、场景共享材质、重启及关闭后的登记归零。测试还覆盖真实损坏 Bundle 的依赖回滚、错误类型重试、场景外部卸载、同类型同名子资源拒绝、回调失败与重入、强退创建候选同步回收、迟到结果代际隔离、预热取消、异步关闭故障，以及带借出对象的连续退出 Play。

独立 agent 多轮静态审查后的问题和修复见[审查记录](evidence/unitycore-assets/review.md)。最后一轮完整回归在修复后执行，源码指纹复核无差异。Python 验证脚本语法检查、源码包路径检查、文档链接检查和 `git diff --check` 均通过。没有运行无关模拟内核测试，也没有把这些本机检查描述为 CI。

复现命令（目标目录必须不存在）：

```powershell
python scripts/verify_unity_assets.py TestResults/assets-verification-new --players
```

需要 Unity CLI、Unity 6000.3.23f1、Windows IL2CPP 模块和兼容 Visual C++ 工具链。脚本会保留隔离工程、构建与失败现场；不会覆盖已有产物。示例启动步骤见 [AssetsDemo](../examples/unity/AssetsDemo/README.md)。

证据：[机器摘要](evidence/unitycore-assets/summary.json)、[源码指纹](evidence/unitycore-assets/sources.json)、[Player 文件指纹](evidence/unitycore-assets/player-files.json)、[原始证据包](evidence/unitycore-assets/assets-evidence.zip)、[Mono 截图](evidence/unitycore-assets/Mono-smoke.png)、[IL2CPP 截图](evidence/unitycore-assets/Il2Cpp-smoke.png)。证据包包含原始 BuildReport、产物与归属表、负例报告、四份 NUnit XML、日志、构建设置及复现脚本。Player 可执行文件保留在本机 `TestResults/assets-final-20261109-r3/workspace/TestResults/assets-players`，不将大体积二进制纳入源码仓库。

验证边界：这里只证明本次受管构建集合内的源资源唯一归属，不证明 Player 内置数据与 Bundle 的全局去重，也不把场景内嵌对象算作独立源资源。未执行其他平台、远端更新、长期压力或性能预算验收；未接入 CI、分析器或自动格式检查。诊断的 `fileBytes` 是文件大小，`objectMemoryBytes` 是 Unity Profiler 对该资源对象的测量值，不代表整个 Bundle 或其依赖的完整内存成本。

## 目录与自动命名扩展（2026-11-10）

资源组现支持递归目录收集、全局选择自动地址（去扩展名全路径、组名/资源名），所有组和资源使用同一规则。全局 Bundle 命名可选择原哈希模式或可读名称模式，后者共享包为 `share_a_b.bundle`。旧序列化配置的地址规则默认使用全路径，原手填地址与组级模式不再生效；需重建内容并迁移调用处。哈希名称默认保持不变。完整使用方式与冲突规则见 [包说明](../packages/com.ainative.unitycore.assets/README.md)。本次只扩展 Editor 收集与构建配置，不改变运行时所有权和取消协议。

可读 Bundle 文件名遵循 Unity 的规则统一转为小写，公开地址不受影响。

本次验证：Unity 6000.3.23f1 隔离工程中 EditMode 42/42、PlayMode 45/45 通过，包含可读包名真实构建、递归收集、单项覆盖、跨组归属与共享拼接冲突。首次实验揭示 Unity 自动小写 Bundle 名，修复后全量通过；原始 XML 和源码指纹保存在本机 `TestResults/assets-collection-20261110`。独立只读审查完成。本次未重新构建 Player 或重复双 Domain Reload 矩阵，沿用先前记录仅作历史证据。

全局地址规则调整后重新构建真实内容，EditMode 43/43、PlayMode 45/45 通过；新增旧手填字段/组级模式被忽略与多组统一切换测试，独立审查完成。原始日志、XML、内容和源码指纹在本机 `TestResults/assets-global-address-20261110`。本次未重跑 Player 构建和双 Domain Reload 矩阵。
