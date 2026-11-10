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

Builds use LZ4 and produce catalog.json, the native manifest, file checksums, original BuildReport, planned/actual ownership TSVs, exclusions and errors. Duplicate source ownership, incomplete ordinary-resource evidence, unmappable report files, or differing dependencies reject publication. Scene paths are separately verified from real bundles. Generated scene preload metadata, scripts and Unity built-ins are listed as exclusions, not ordinary source assets. Builds stage under the project's BuildArtifacts/Assets directory; add BuildArtifacts to the consuming project's ignore rules. Publication uses a fresh destination on the same volume and never overwrites an existing build.

`AssetBuilder.Build(settings, output)` and `AssetBuilder.BatchBuild` provide scripted and batch entrypoints. Batch arguments are `-assetGroups Assets/ResourceGroups.asset -assetOutput <new-directory>`. Keep the content root unchanged throughout a module run. Rebuild for a different Unity version, target or changed script identity. The AssetsDemo Player builder supplies the generated native manifest to preserve bundle-only script types from stripping.

Loading validates file existence, size and Unity CRC. Because Unity interprets CRC zero as disabled, zero-CRC entries additionally verify the full SHA-256 before loading. This rare fallback scans the file on the main thread; no runtime frame budget is promised. A missing CRC field therefore cannot silently disable content verification.

## Loading, pooling and scenes

`LoadAsync<T>` and `LoadSubAssetAsync<T>` return independent disposable leases. Concurrent requests share underlying operations; cancelling one waiter does not cancel others. No runtime LRU, global unload, Resources fallback or blocking wait is used. Scope shutdown waits for cancelled native work to settle. Retain a lease for intentional residency.

`CreatePool(address, new AssetPoolOptions(maxInactive))` returns an owned prefab pool. PrewarmAsync fills inactive capacity. RentAsync returns a disposable rental; Dispose returns it, excess idle instances are destroyed. A rental cannot return a successor rental. `IPooledInstanceCallbacks.OnRent/OnReturn` reset application-specific state; callback failures destroy the instance. External destruction invalidates rentals and removes pool records. Closing a pool destroys outstanding rentals as well as idle instances.

Scene loading is Additive and automatically activates. `AssetScene.SetActive` selects the active scene; `UnloadAsync` unloads it. Keep a separate bootstrap scene. Same-path scene loads are rejected if already tracked or loaded externally. Do not concurrently load the same path through external SceneManager calls while this module owns a load operation; Unity's API does not associate sceneLoaded events with an operation handle. External unloading of a managed scene is supported.

Editor-only `EditorAssets.Create(groups)` uses AssetDatabase with the same ownership API. It never destroys source assets. Use real bundles for lifetime and Player acceptance. `AssetDiagnosticsWindow.Inspect(assets)` displays live ownership; `GetDiagnostics()` returns copied read-only information. File sizes are not runtime memory measurements.

See the repository's `docs/unitycore-assets.md` for acceptance evidence and current limits. No remote updates, content deduplication across distinct GUIDs, or cross-platform compatibility is promised.

### 目录收集、地址和 Bundle 命名

`AssetGroups.BundleNaming` 对本次配置内所有包生效：`StableHash` 保留旧的 `group_<sha>.bundle` / `shared_<sha>.bundle`；`GroupName` 使用 `group_a.bundle`，A、B 共享依赖使用 `share_a_b.bundle`。共享名称按组名称的 Ordinal 顺序排列。`ResourceGroup.Name` 为空时沿用稳定 `Id`；可读包名允许字母、数字、下划线、连字符，大小写冲突及拼接歧义均拒绝构建。不要通过修改 Id 来重命名显示名称。

每组的 `Folders` 可拖入目录，验证、Editor 初始化和构建时递归收集最新内容；目录本身、脚本和程序集定义不收集。重叠目录在同组内去重，同一文件跨组收集仍报重复归属。目录里的场景与普通资源不能混组，沿用原有场景边界。

`AssetGroups.AddressMode` 是全局配置，统一应用于所有组、目录和单项资源，仅提供以下两种模式。资源为 `Assets/Probe/A.prefab`、组名称为 A 时：

| 模式 | 地址 | 调用示例 |
| --- | --- | --- |
| FullPath（默认） | Assets/Probe/A | `InstantiateAsync("Assets/Probe/A")` |
| GroupAndAssetName | A/A | `InstantiateAsync("A/A")` |

自动地址只去掉最后一个扩展名并保留大小写。没有手动地址或单项覆盖；不同目录的同名文件、不同扩展名产生的相同地址均报错，请调整资源名称或使用全路径模式。同组显式资源与目录重叠时只收集一次。

迁移：旧的组级 AddressMode 和手填 Address 不再读取；没有全局字段的旧配置默认 FullPath。必须重新构建 catalog/Bundle 并更新调用处，运行时不会为旧地址生成别名。更改全局模式、资源路径或组名会影响公开地址。

Inspector 的 `Validate and inspect packing` 可预览最终公开地址、源路径和物理 Bundle 所有权，再执行构建。可读 Bundle 文件名遵循 Unity 的规则统一转为小写，公开地址不受影响。
