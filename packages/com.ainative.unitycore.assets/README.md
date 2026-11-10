# AI Native UnityCore Assets

Local AssetBundle ownership for Unity 6000.3, Windows x64. Depends on UnityCore and UniTask 2.5.11. Register one `AssetsModule` explicitly before starting Core. Access it through `IAssets` during composition or dependency binding; retain that reference while running.

```csharp
var assets = new AssetsModule(); // StreamingAssets/AiNativeAssets
Core.Inst.RegisterModule(assets);
await Core.Inst.RunAsync();
var screen = assets.CreateScope("inventory");
var instance = await screen.InstantiateAsync("Assets/UI/InventoryPanel");
// Closing the owner destroys its instances and drains pending operations.
await screen.CloseAsync();
await Core.Inst.CloseAsync();
```

All calls and cancellation requests are main-thread only. A resource lease owns its Unity object; never use it after Dispose. Objects instantiated outside this API require the caller to retain the prefab lease. The module does not discover arbitrary external references. CloseAsync waits for normal cleanup; Shut isolates forced retirement and observes late native completions. Forced application termination cannot guarantee a native operation finishes before the process exits.

Normal instance cleanup waits until all component destruction callbacks have completed. A failed scope close retains any remaining owners and rejects new operations; retry CloseAsync after resolving the failure. Scene unload can also be retried. Existing callers still receive the original failure. Keep the bootstrap scene loaded: Unity cannot unload its last scene. Native loading errors include the public address, the actual failing bundle, the operation stage and the original cause; cancellation and disposed-owner errors retain their standard exception semantics.

## Resource authoring

Create **AI Native → Assets → Resource Groups**. Each group has a stable ID and main-asset entries or folders. One global address mode generates case-sensitive addresses for every entry. Scene groups contain scenes only. The Inspector validates and displays packing or builds Windows bundles into a new directory. Full-path addresses change when assets move; group/name addresses change when the group or file name changes. Implicit dependencies have no public address.

Explicit ownership wins. Otherwise dependencies referenced by multiple original groups are grouped by that sorted group set into deterministic shared bundles. Scene external dependencies always go into ordinary bundles. Cycles fail with a dependency chain. Sprite and other subasset names must be unique per exact type within a source file.

Builds use LZ4 and produce catalog.json, file checksums, backend-specific raw evidence, planned/actual ownership TSVs, exclusions and errors. Duplicate source ownership, incomplete ordinary-resource evidence, unmappable report files, or differing dependencies reject publication. Scene paths are separately verified from real bundles. Generated scene preload metadata, scripts and Unity built-ins are listed as exclusions, not ordinary source assets. Builds stage under the project's BuildArtifacts/Assets directory; add BuildArtifacts to the consuming project's ignore rules. Publication uses a fresh destination on the same volume and never overwrites an existing build.

`AssetBuilder.Build(settings, output)` and `AssetBuilder.BatchBuild` provide scripted and batch entrypoints. Batch requires `-assetGroups Assets/ResourceGroups.asset`; optional `-assetOutput <new-directory>` overrides the configured output template. Keep the content root unchanged throughout a module run. Rebuild for a different Unity version, target or changed script identity. Use AssetPlayerBuildSupport.BuildPlayer(contentRoot, options) to preserve bundle-only script types using the native manifest or SBP linker XML.

Loading validates file existence, size and Unity CRC. Because Unity interprets CRC zero as disabled, zero-CRC entries additionally verify the full SHA-256 before loading. This rare fallback scans the file on the main thread; no runtime frame budget is promised. A missing CRC field therefore cannot silently disable content verification.

## Loading, pooling and scenes

`LoadAsync<T>` and `LoadSubAssetAsync<T>` return independent disposable leases. Concurrent requests share underlying operations; cancelling one waiter does not cancel others. No runtime LRU, global unload, Resources fallback or blocking wait is used. Scope shutdown waits for cancelled native work to settle. Retain a lease for intentional residency.

`CreatePool(address, new AssetPoolOptions(maxInactive))` returns an owned prefab pool. PrewarmAsync fills inactive capacity. RentAsync returns a disposable rental; Dispose returns it, excess idle instances are destroyed. A rental cannot return a successor rental. `IPooledInstanceCallbacks.OnRent/OnReturn` reset application-specific state; callback failures destroy the instance. External destruction invalidates rentals and removes pool records. Closing a pool destroys outstanding rentals as well as idle instances.

Scene loading is Additive and automatically activates. `AssetScene.SetActive` selects the active scene; `UnloadAsync` unloads it. Keep a separate bootstrap scene. Same-path scene loads are rejected if already tracked or loaded externally. Do not concurrently load the same path through external SceneManager calls while this module owns a load operation; Unity's API does not associate sceneLoaded events with an operation handle. External unloading of a managed scene is supported.

The `AssetGroups` Inspector exposes **Editor Load Mode**, defaulting to **AssetBundle** (including old configurations). In Editor startup code, use `EditorAssets.CreateConfigured(groups)` to honor it, or `EditorAssets.Create(groups, EditorAssetLoadMode.AssetDatabase)` to select explicitly. Bundle mode accepts an optional `contentRoot`, defaulting to StreamingAssets/AiNativeAssets; direct mode ignores that path and needs no built content. Selection is captured when the module is created; stop and create a new module to change modes. The Inspector field only affects callers using `CreateConfigured`.

Keep these factory calls under `#if UNITY_EDITOR`, with an Editor assembly reference where needed. Player startup continues to use `new AssetsModule()` and always loads bundles. Existing `EditorAssets.Create(groups)` remains an unconditional AssetDatabase entry point for compatibility. Direct mode uses the same ownership API and never destroys source assets; it cannot verify actual bundle unloading. Use real bundles for lifetime and Player acceptance. `AssetDiagnosticsWindow.Inspect(assets)` displays live ownership; `GetDiagnostics()` returns copied read-only information. File sizes are not runtime memory measurements.

Both modes share reference counting, scope cancellation and shutdown. AssetDatabase requests can complete synchronously, as can requests for already loaded Bundle resources. Cancelling after completion does not revoke the returned lease: dispose it or close its scope. Pre-cancelled requests never acquire ownership; cancelled in-flight operations remain observed and are drained by close. Normal pool close waits for borrowed objects, inactive objects and the pool root to be actually destroyed before reporting completion. Source objects remaining alive after direct-mode close belong to the Editor and are not evidence of outstanding module ownership.

See the repository's `docs/unitycore-assets.md` for acceptance evidence and current limits. No remote updates, content deduplication across distinct GUIDs, or cross-platform compatibility is promised.

### 目录收集、地址和 Bundle 命名

`AssetGroups.BundleNaming` 对本次配置内所有包生效：`StableHash` 保留旧的 `group_<sha>.bundle` / `shared_<sha>.bundle`；`GroupName` 使用 `group_a.bundle`，A、B 共享依赖使用 `share_a_b.bundle`。共享名称按组名称的 Ordinal 顺序排列。`ResourceGroup.Name` 为空时沿用稳定 `Id`；可读包名允许字母、数字、下划线、连字符，大小写冲突及拼接歧义均拒绝构建。不要通过修改 Id 来重命名显示名称。

每组的 `Folders` 可拖入目录，验证、Editor 初始化和构建时递归收集最新内容；目录本身、脚本、程序集定义及 Unity 无法识别的 `DefaultAsset` 文件不收集；可导入的 TextAsset（如 `.txt`）正常收集。显式配置不支持的文件则报错。目录中被跳过的非目录文件列在分析窗口的 Excluded sources 和构建报告 `excluded.txt` 中。重叠目录在同组内去重，同一文件跨组收集仍报重复归属。目录里的场景与普通资源不能混组，沿用原有场景边界。

`AssetGroups.AddressMode` 是全局配置，统一应用于所有组、目录和单项资源，仅提供以下两种模式。资源为 `Assets/Probe/A.prefab`、组名称为 A 时：

| 模式 | 地址 | 调用示例 |
| --- | --- | --- |
| FullPath（默认） | Assets/Probe/A | `InstantiateAsync("Assets/Probe/A")` |
| GroupAndAssetName | A/A | `InstantiateAsync("A/A")` |

自动地址只去掉最后一个扩展名并保留大小写。没有手动地址或单项覆盖；不同目录的同名文件、不同扩展名产生的相同地址均报错，请调整资源名称或使用全路径模式。同组显式资源与目录重叠时只收集一次。

迁移：旧的组级 AddressMode 和手填 Address 不再读取；没有全局字段的旧配置默认 FullPath。必须重新构建 catalog/Bundle 并更新调用处，运行时不会为旧地址生成别名。更改全局模式、资源路径或组名会影响公开地址。

Inspector 的 `Validate and inspect packing` 可预览最终公开地址、源路径和物理 Bundle 所有权，再执行构建。可读 Bundle 文件名遵循 Unity 的规则统一转为小写，公开地址不受影响。

地址冲突报告包含两个源路径和所属组；跨组重复收集报告双方组 ID；Bundle 名冲突报告双方组集合。共享名称拼接冲突（例如场景组 A_B 的外部依赖与 A/B 共享资源）拒绝发布，可调整组名或切换 StableHash 模式。

## 构建后端与缓存

全局 `AssetGroups.BuildBackend` 支持 `ScriptableBuildPipeline`（默认）和 `Builtin`。旧配置没有该字段时也默认 SBP；已发布内容仍由相同运行时加载，不自动转换。Assets 直接依赖 SBP 2.6.2，仅 Editor 程序集引用其 API；不同版本会明确拒绝，避免在未经验证的写入证据格式下发布。

两种后端共用收集、自动地址和分包计划。`AssetBuilder.Build(settings, output)` 默认使用 SBP 本地缓存；`AssetBuilder.Build(settings, output, new AssetBuildOptions { UseCache = false })` 为干净构建。本次不读取缓存，不删除全工程缓存；原生后端始终强制重建。Inspector 提供两个构建按钮，批处理使用 `-assetCleanBuild` 关闭缓存。远程缓存始终禁用。

SBP 构建期间关闭 `slimWriteResults`，在 finally 恢复；该设置参与 SBP 缓存键。`sbp-write-results.json` 记录实际序列化对象及文件→Bundle 映射，`buildlogtep.json` 保留缓存记录。普通源 GUID 缺失、重复、未知输出或映射不完整均拒绝发布；原生后端仍保留 `assets.buildreport`。场景生成对象单列，不冒充独立源资源。SBP 依赖结果包含传递依赖：比较双方可达关系后，目录存储约简后的直接依赖。

原生 CRC 从原生构建结果查询；SBP 使用归档结果的 CRC，并在构建验证时用它实际打开 Bundle。文件大小、CRC、SHA-256 和目录格式对两个后端一致。校验后复制到未被 Unity 加载的发布暂存目录，再原子改名；验证暂存目录保留供追溯，因此成功构建也会占用额外磁盘空间，可在无构建运行时归档清理 `BuildArtifacts/Assets`。

`build-metadata.json` 记录格式版本、后端、Unity/SBP 版本、缓存开关、裁剪文件及其 SHA-256。`AssetPlayerBuildSupport.BuildPlayer` 是显式 Windows Player 构建入口：原生使用 Manifest，SBP 使用生成的 `link.xml`；linker 回调仅在本次调用期间生效，finally 清理，不改用户的 link.xml。缺失、越界或损坏裁剪证据会失败。旧内容缺少构建元数据时仍可运行，但通过新辅助入口构建 Player 前须重建内容。

完整验收命令见仓库 `scripts/verify_unity_assets.py --help`。`--backend both --players` 先验原生、再验 SBP，最后使用 SBP 内容构建 Mono Development 和 IL2CPP Player。`--baseline-fixture` 只在隔离副本使用已提交的示例组配置，保留本地编辑。

Windows 发布重命名若遇短暂 I/O 拒绝，只在源仍存在、目标完全不存在时最多额外尝试两次（总等待 200ms）。最终失败保留现场并抛出异常；该处理来自独立文件系统复现，不依赖假设的 Unity 锁占用。

## 构建版本与输出路径

`AssetGroups.Version` 是下一次构建的版本，默认 1。全局 `OutputDirectory` 默认 `{projectDir}/BundleBuildOutput/{version}`；必须包含 `{version}`，仅支持区分大小写的 `{projectDir}` 与 `{version}`。相对路径以 Unity 工程根目录解析，输出必须同盘且目录尚不存在。将 `BundleBuildOutput/` 加入消费工程的忽略规则。

Inspector 显示本次实际输出路径；普通构建、干净构建和菜单构建直接使用配置，不再选择目录。`AssetBuilder.Build(settings)` / `Build(settings, options)` 使用模板；原有显式传入输出目录的重载优先使用该路径。批处理省略 `-assetOutput` 时使用模板。

成功发布版本 1 到 `/1` 后，配置自动保存为版本 2；构建或校验失败不递增。`build-metadata.json.ContentVersion` 记录本次版本，catalog 和运行时加载协议不变。新旧配置缺少字段时均从默认值开始，不自动重写旧资产。显式路径重载也遵循版本递增；临时配置只更新内存。

持久配置必须可写；仅保存当前配置资产，不保存全工程其他脏资产。若发布成功后配置保存失败，会报告已发布路径并保留产物与内存中的下一版本；修复写权限后保存该配置。再次启动若磁盘版本仍旧，已有目录检查会阻止覆盖，需核对已发布版本后修正配置。不会自动覆盖、跳号或清理旧内容。

## 一键部署到 StreamingAssets

选中 ResourceGroups，点击 **Copy latest build to StreamingAssets**（同名菜单位于 AI Native/Assets）。最近成功构建的路径、版本和 catalog 摘要随配置保存；不会扫描最高版本目录，也不会从下一版本猜测来源。旧配置先成功构建一次，或使用 `AssetDeployment.CopyToStreamingAssets(sourceDirectory)` 部署已有完整产物。复制不构建、不递增版本。

目标固定为 `Assets/StreamingAssets/AiNativeAssets`，仅包含 catalog.json 和 catalog 列出的全部 Bundle。先验证来源和暂存副本的大小及 SHA-256，再完整替换目标；旧包及其 meta 被清理，目录自身 meta 与其他 StreamingAssets 内容不动。仅 Edit Mode 可部署，来源重叠或目录链接会拒绝。异常替换尝试恢复旧目录；如果恢复也失败，错误明确列出保留的备份路径。进程崩溃不保证跨目录事务恢复。

裁剪证据保存在 `BuildArtifacts/Assets/StreamingAssetsDeployment/<catalogSha256>`，不会进入 Player 的 StreamingAssets。SBP 保存 link.xml，Builtin 保存根及逐包 Manifest；删除原始构建输出后仍能使用。不同摘要快照保留；重复部署同一摘要会重新验证并替换其辅助快照，能够修复损坏的证据。不要仅复制运行时目录到另一工程后直接打包，需同时重新部署完整构建源以建立证据。

SBP 精简部署支持标准 Build Profiles 和 `BuildPipeline.BuildPlayer`，构建前验证实际文件与证据，linker 回调只使用对应的 link.xml。Builtin 使用 `AssetPlayerBuildSupport.BuildDeployedPlayer(options)`（或显式提供匹配的部署 Manifest），不具备保护信息的标准构建会失败。原有 `BuildPlayer(fullContentRoot, options)` 保持兼容，但其来源必须与实际部署内容一致；旧式完整 StreamingAssets 目录仍可能包含报告，需重新部署才能精简。

```powershell
unity run <Unity工程路径> -- -executeMethod AiNative.UnityCore.Assets.Editor.AssetDeployment.BatchCopy -assetGroups Assets/ResourceGroups.asset
unity run <Unity工程路径> -- -executeMethod AiNative.UnityCore.Assets.Editor.AssetDeployment.BatchCopy -assetSource <完整构建输出目录>
```

两个来源参数必须且只能提供一个。结果日志显示来源、版本、目标、运行时文件数和总字节数。
