# AI Native UnityCore Assets

Local AssetBundle ownership for Unity 6000.3, Windows x64. Depends on UnityCore and UniTask 2.5.11. Register one `AssetsModule` explicitly before starting Core. Access it through `IAssets` during composition or dependency binding; retain that reference while running.

```csharp
var assets = new AssetsModule(); // StreamingAssets/AiNativeAssets
Core.Inst.RegisterModule(assets);
await Core.Inst.RunAsync();
var screen = assets.CreateScope("inventory");
var instance = await screen.InstantiateAsync("inventory-panel");
// Closing the owner destroys its instances and drains pending operations.
await screen.CloseAsync();
await Core.Inst.CloseAsync();
```

All calls and cancellation requests are main-thread only. A resource lease owns its Unity object; never use it after Dispose. Objects instantiated outside this API require the caller to retain the prefab lease. The module does not discover arbitrary external references. CloseAsync waits for normal cleanup; Shut isolates forced retirement and observes late native completions. Forced application termination cannot guarantee a native operation finishes before the process exits.

Normal instance cleanup waits until all component destruction callbacks have completed. A failed scope close retains any remaining owners and rejects new operations; retry CloseAsync after resolving the failure. Scene unload can also be retried. Existing callers still receive the original failure. Keep the bootstrap scene loaded: Unity cannot unload its last scene. Native loading errors include the public address, the actual failing bundle, the operation stage and the original cause; cancellation and disposed-owner errors retain their standard exception semantics.

## Resource authoring

Create **AI Native → Assets → Resource Groups**. Each group has a stable ID and main-asset entries with exact case-sensitive addresses. Scene groups contain scenes only. The Inspector validates and displays packing or builds Windows bundles into a new directory. Addresses remain stable when assets move. Implicit dependencies have no public address.

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
