# UnityCore Assets：SBP 双后端构建

## 契约

`AssetGroups.BuildBackend` 默认 `ScriptableBuildPipeline`（枚举 0），`Builtin` 为 1。旧配置缺少字段时也默认 SBP。Assets 包固定依赖 Unity 官方 SBP 2.6.2，API 仅由 Editor 程序集引用；运行时仍读取既有 catalog、通过原生 AssetBundle 加载。已发布原生内容继续可运行，不生成地址别名，不静默切换失败后端。

两后端共用目录收集、全局地址、共享依赖与命名计划。`AssetBuildOptions.UseCache` 默认 true；Inspector 干净构建按钮和批处理 `-assetCleanBuild` 关闭本次 SBP 缓存复用，不删除工程缓存。显式禁用远程缓存；原生后端继续强制重建。范围为 Unity 6000.3.23f1、Windows x64、LZ4。

## 实际产物证据

SBP 2.6.2 的 `ArchiveAndCompressBundles` 输出包含传递依赖。两个后端均校验无环图与计划的可达关系，目录保存约简后的直接依赖。无需字节级跨后端一致，但要求源资源归属、公开地址及依赖语义一致。

SBP 默认 `slimWriteResults` 会只保留每个序列化文件的部分对象，不能作为零冗余完整证据。构建期间关闭该标志并在 finally 恢复；此标志参与 SBP 写入缓存键，已有精简缓存不会被当作完整证据使用。最后一个显式构建任务捕获本轮 WriteResults 的真实对象与 FileToBundle 映射，记录到 `sbp-write-results.json`，不读取上次原生 BuildReport，也不复制计划作为实际归属。

每个普通源 GUID 必须与当前路径匹配且只有一个实际 Bundle 所有者；缺失对象、缺失文件映射、未知来源、重复归属或未知输出均失败。脚本、内置资源和零 GUID 的已知生成对象单列排除。场景生成对象不声明为独立源文件；场景路径另外通过实际 Bundle 查询，外部资源共享身份在运行测试中验证。原生后端继续输出 `assets.buildreport`。

首次最小探针的冷构建与缓存命中均记录 37 个序列化对象、7 个普通源 GUID，材质与贴图各只有一个物理所有者。探针同时覆盖场景外部引用与 Sprite 子资源。SBP CRC 使用本轮归档结果；原生 GetCRCForAssetBundle 对这些 SBP 产物返回 false，不能混用。构建验证实际以 SBP CRC 打开 Bundle，再计算文件大小和 SHA-256；运行时协议不变。

## Player 与发布

`build-metadata.json` 记录版本、后端、SBP 版本、缓存设置，以及裁剪文件和 SHA-256。`AssetPlayerBuildSupport.BuildPlayer(contentRoot, options)` 显式提供 Windows Player 构建入口：原生使用 Manifest，SBP 使用生成的 `link.xml`，只在本次构建期间经 linker 回调提供，finally 清理上下文。拒绝缺失、损坏或越界的裁剪证据，不改用户的 link.xml。

示例运行代码改用 `LoadAsync<ScriptableObject>`，通过类型名称和反射检查 DemoData.Value=42，不直接引用 DemoData 类型，以验证 Bundle-only 类型及序列化字段的保留。

Unity 构建可能清理 transient ScriptableObject。构建入口仅对非持久配置临时增加 HideAndDontSave，并在 finally 恢复；持久配置不改 flags。

校验目录与发布目录分离：复制已验证文件到未加载的发布暂存目录，最后原子重命名，目标必须不存在。暂存保留供追溯，成功构建有额外磁盘占用。纯 Python 文件系统压力探针也复现 Windows WinError 5（60 次中 1 次，100ms 后恢复），因此 Windows Editor 在源仍存在且目标完全不存在时最多额外尝试两次原子重命名，总等待 200ms；不覆盖目标、不吞最终错误。不将此现象归因于已确认的 Unity 文件锁。

## 复现与归档

```powershell
python scripts/verify_unity_assets.py TestResults/sbp-verification-new --backend both --baseline-fixture --players
```

脚本仅修改隔离副本。`--baseline-fixture` 使用当前提交中的标准 ResourceGroups 配置，保留用户本地编辑；归档实际 fixture-source。顺序为原生、SBP，各自开关 Domain Reload 运行 EditMode/PlayMode，最终用 SBP 内容构建 Mono Development 和 IL2CPP Player。输出目录必须是新的目录。

实现期间失败证据分别保留在 `TestResults/assets-sbp-probe`（探针编译、CRC 适配、连续构建配置清理）、`TestResults/sbp-final`（原生完整回归中的短暂发布拒绝）、`TestResults/sbp-move-diagnostic`（独立 Windows 重命名复现）。独立审查已覆盖写入证据、缓存键、依赖图、裁剪上下文和有限发布恢复。

## 最终验收（2026-11-10）

| 范围 | 结果 |
| --- | --- |
| Builtin：Domain Reload 开/关 | 每组 EditMode 59/59、PlayMode 45/45 |
| SBP：Domain Reload 开/关 | 每组 EditMode 59/59、PlayMode 45/45 |
| 总计 | 416 次测试通过，0 失败 |
| SBP Mono Development Player | 构建和运行通过，退出码 0 |
| SBP IL2CPP 非 Development Player | 构建和运行通过，退出码 0 |
| 两后端正式内容 | 各 7 Bundle、6 地址、7 普通源 GUID、0 重复归属，文件 SHA-256 核对通过 |

Player 覆盖共享材质/贴图、释放 A 后 B 可用、对象池、场景外部共享身份、Bundle-only ScriptableObject 类型与字段、重启及正常关闭清理。两张截图均已检查。82 个实现文件与隔离被测副本逐字节比对一致；另记录 Git 规范化后源码指纹，避免 Windows 换行差异混淆证据。

证据：[机器摘要](evidence/unitycore-assets/sbp/summary.json)、[原始证据包](evidence/unitycore-assets/sbp/sbp-evidence.zip)、[被测源码指纹](evidence/unitycore-assets/sbp/sources.json)、[Git 源码指纹](evidence/unitycore-assets/sbp/sources-git.json)、[Player 文件指纹](evidence/unitycore-assets/sbp/player-files.json)、[Mono 截图](evidence/unitycore-assets/sbp/Mono-smoke.png)、[IL2CPP 截图](evidence/unitycore-assets/sbp/Il2Cpp-smoke.png)。证据包包含原始报告/写入结果、实际 Bundle、缓存探针、故意重复产物负例、全部最终 XML/日志、实现期间失败证据、构建设置及复现脚本。大体积 Player 文件保留在本机 `TestResults/sbp-pass/workspace/TestResults/assets-players`。

验收不扩大为 Player 内置数据全局去重、其他平台、其他 SBP 版本、远程缓存或性能预算保证；未接入新的 CI。Unity Editor 关闭日志中的 JobTempAlloc 提示在此前原生基线也存在，不以本轮模块所有权检查宣称 Unity 进程全局零内存泄漏。
