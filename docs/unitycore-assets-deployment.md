# UnityCore Assets：精简部署到 StreamingAssets

## 使用方式与边界

资源配置 Inspector 和 `AI Native/Assets/Copy latest build to StreamingAssets` 菜单部署当前配置最近成功发布的产物。记录包括实际路径、版本和 catalog SHA-256，与下一构建版本一起保存。工程内路径相对工程根目录；外部输出保留绝对路径。旧配置未记录来源时要求先构建，或显式调用 `AssetDeployment.CopyToStreamingAssets(sourceDirectory)`。不扫描目录、不猜测 Version - 1，不构建或递增版本。

目标固定为 `Assets/StreamingAssets/AiNativeAssets`。仅复制 catalog.json 与其中列出的 Bundle（含共享依赖），不复制报告、元数据、Manifest、link.xml、缓存或源 meta。Unity 自行生成的 meta 不计入运行时清单。Inspector 和日志显示来源、版本、目标、文件数与总字节数。

来源和暂存内容均校验平台、Unity 版本、文件大小与 SHA-256。替换前拒绝目录重叠和 reparse point；旧目标内部的 junction 同样拒绝。先完成暂存校验，再移动旧目录并安装新目录；失败尝试恢复旧目录，恢复失败明确报告备份路径。成功清理旧包及其内部 meta，保留模块目录自身 meta、其他 StreamingAssets 文件。不能在 Play、即将进入 Play 或 Player 构建期间部署，也不会主动卸载用户对象。

## 裁剪证据与 Player

辅助证据位于 `BuildArtifacts/Assets/StreamingAssetsDeployment/<catalogSha256>`，包括 catalog、构建元数据、文件摘要以及 SBP link.xml 或 Builtin 根/逐包 Manifest。它们不会进入 Player 的 StreamingAssets。不同摘要快照保留；同摘要重新部署会以有效来源替换辅助快照，可修复损坏记录。原构建目录删除后仍可验证和构建 Player。

标准 Player 构建预处理检查当前部署内容与快照一致。SBP 的 linker 回调从对应快照取 link.xml，支持直接 BuildPipeline 和标准构建流程，不保存跨标准构建的临时静态路径。Builtin 需要匹配的 Manifest：推荐 `AssetPlayerBuildSupport.BuildDeployedPlayer(options)`；未提供保护信息时标准构建拒绝。本次不将原生 Manifest 转换成 linker XML。

原有 `BuildPlayer(fullContentRoot, options)` 仍支持完整内容目录，且有已部署内容时必须匹配其 catalog。旧式完整 StreamingAssets 目录兼容路径可能仍包含报告，需要重新执行部署才会精简。示例 `BuildContent*` 改为独立构建后精简部署；`BuildMono`/`BuildIl2Cpp` 使用部署辅助入口，`BuildMonoStandard`/`BuildIl2CppStandard` 用于 SBP 标准入口验证。

## 复现命令

```powershell
unity run <project> -- -executeMethod AiNative.UnityCore.Assets.Editor.AssetDeployment.BatchCopy -assetGroups Assets/ResourceGroups.asset
unity run <project> -- -executeMethod AiNative.UnityCore.Assets.Editor.AssetDeployment.BatchCopy -assetSource <full-build-directory>
python scripts/verify_unity_assets.py TestResults/deploy-new --backend both --baseline-fixture --players
```

两个复制来源参数只能选一个。验证脚本只修改隔离副本；每个内容后端分别开关 Domain Reload 跑测试并构建两种 Player。Builtin 使用部署辅助入口，SBP 使用标准 BuildPipeline 入口。Player 输出清单必须严格等于 catalog 与 Bundle 集合。

## 2026-11-10 实际验收

| 检查 | 结果 |
| --- | --- |
| Builtin / SBP × Domain Reload 开/关 | 每组 EditMode 89/89、PlayMode 45/45，共 536 项通过 |
| Builtin Mono Development / IL2CPP | 辅助入口构建、运行均通过 |
| SBP Mono Development / IL2CPP | 标准 BuildPipeline 入口构建、运行均通过 |
| 每个 Player 运行时目录 | 恰好 8 个文件：catalog + 7 个 Bundle |
| 批处理显式源 / 最近记录 | 实际执行通过 |
| Builtin 未提供 Manifest 的标准构建 | 实际被拒绝 |
| 菜单 | Unity ExecuteMenuItem 实际调用通过 |
| Inspector 鼠标点击 | 未完成：桌面窗口捕获连续超时，Unity Inspector 截图为空白 |

9 项新增部署测试覆盖两后端白名单、旧包/meta 清理、其他 StreamingAssets 内容不变、目录 meta 保留、原构建源删除后的证据独立性、无记录、失败构建保留记录、源文件丢失/损坏/锁定、证据损坏与重新部署恢复、目录替换失败回滚及 junction 外部目录不受影响。既有版本测试补充了最近构建记录持久化重载断言。4 个 Player 验证共享材质/贴图、池、场景、Bundle-only 脚本类型、重启和清理，截图已人工检查。88 个实现/测试文件与隔离被测副本逐字节一致；独立静态审查完成。

首轮复现 Unity Mono 长路径读取失败（File.Copy 已成功，但摘要读取失败），随后缩短临时目录并为已复现的摘要、存在性和属性检查加 Windows 扩展路径。第二轮仅余路径分隔符比较差异，规范化后专项测试 9/9 通过，再执行最终回归。失败 XML/日志一并归档，不宣称任意深度 Windows 工程路径均受支持。

证据：[机器摘要](evidence/unitycore-assets/deployment/summary.json)、[原始归档](evidence/unitycore-assets/deployment/deployment-evidence.zip)、[被测源码指纹](evidence/unitycore-assets/deployment/sources.json)。归档包含最终 XML/日志、两后端运行时内容及独立证据、Player 文件清单与指纹、批处理和菜单探针、实现期间失败记录及复现脚本。Player 二进制留在本机 TestResults。GUI 未通过时没有猜测点击，也未操作用户原有 Editor；标准 Build Profiles 的鼠标流程未单独验收。进程崩溃时不保证跨目录事务恢复，未新增 CI。
