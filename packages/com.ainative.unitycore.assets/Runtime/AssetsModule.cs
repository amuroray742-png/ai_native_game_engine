using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    /// <summary>One local catalog per run; register before Core.RunAsync. Only main-thread access is supported.</summary>
    public sealed class AssetsModule : IModule, IAsyncShutdownModule, IAssets
    {
        private readonly Func<(AssetCatalog catalog, IAssetBackend backend)> _factory;
        private readonly AssetRuntimeOptions _options;
        private AssetRuntime _runtime;
        private AssetScope _root;
        public AssetsModule(string contentRoot = null) : this(contentRoot, new AssetRuntimeOptions()) { }
        public AssetsModule(string contentRoot, AssetRuntimeOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            string root = contentRoot ?? Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
            _factory = () =>
            {
                try
                {
                    var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(root, "catalog.json")));
                    if (catalog == null) { throw new AssetException("InvalidCatalog", "Catalog is empty.", stage: "initialize"); }
                    catalog.Validate();
                    if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                    { throw new AssetException("CatalogIncompatible", "This content supports Windows only.", stage: "initialize"); }
                    foreach (var bundle in catalog.Bundles)
                    {
                        if (bundle.Sha256 == null || bundle.Sha256.Length != 64 || bundle.Sha256.Any(c => !Uri.IsHexDigit(c)))
                        { throw new AssetException("InvalidCatalog", "Missing or invalid content digest.", bundle: bundle.Name, stage: "initialize"); }
                    }
                    return (catalog, new BundleBackend(root));
                }
                catch (AssetException) { throw; }
                catch (Exception error) { throw new AssetException("CatalogReadFailed", "Cannot read local catalog.", stage: "initialize", inner: error); }
            };
        }
        internal AssetsModule(Func<(AssetCatalog, IAssetBackend)> factory, AssetRuntimeOptions options = null)
        { _factory = factory; _options = options ?? new AssetRuntimeOptions(); }
        public string GetModuleName() => "AiNative.UnityCore.Assets";
        public void OnRegister(ICore core) { core.RegisterInterface<IAssets, AssetsModule>(this); }
        public UniTask<UniCoreException> Initialize(ICore core, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _factory();
            _runtime = new AssetRuntime(source.catalog, source.backend, _options);
            _root = new AssetScope(_runtime, "application");
            return UniTask.FromResult<UniCoreException>(null);
        }
        public void AfterInitialize(ICore core) { }
        public UniTask<UniCoreException> Start(ICore core, CancellationToken cancellationToken) => UniTask.FromResult<UniCoreException>(null);
        public void AfterStart(ICore core) { }
        public async UniTask ShutdownAsync(ICore core, CancellationToken forcedExit)
        {
            if (_runtime == null) { return; }
            var runtime = _runtime;
            runtime.Closed = true;
            runtime.Lifetime.Cancel();
            runtime.Scheduler.Pump();
            await _root.CloseAsync().AttachExternalCancellation(forcedExit);
            while (runtime.Pending != 0) { await UniTask.Yield(PlayerLoopTiming.Update, forcedExit); }
        }
        public void Shut(ICore core)
        {
            if (_runtime == null) { return; }
            _runtime.Closed = true;
            _runtime.Lifetime.Cancel();
            _runtime.Scheduler.Pump();
            _root.ForceRelease();
        }
        private AssetScope Root
        {
            get
            {
                if (_root == null) { throw new InvalidOperationException("Assets module has not initialized."); }
                _root.EnsureOpen();
                return _root;
            }
        }
        public AssetScope CreateScope(string name) => Root.CreateScope(name);
        public UniTask<AssetBatch> PreloadAsync(IReadOnlyList<AssetLoadRequest> requests, AssetLoadOptions options = null,
            IProgress<AssetLoadProgress> progress = null, CancellationToken cancellationToken = default)
            => Root.PreloadAsync(requests, options, progress, cancellationToken);
        public UniTask<AssetBatch> WarmupShadersAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
            => Root.WarmupShadersAsync(addresses, cancellationToken);
        public UniTask<AssetLease<T>> LoadWithOptionsAsync<T>(string address, AssetLoadOptions options, CancellationToken cancellationToken = default) where T : UnityEngine.Object
            => Root.LoadWithOptionsAsync<T>(address, options, cancellationToken);
        public UniTask<AssetLease<T>> LoadSubAssetWithOptionsAsync<T>(string address, string name, AssetLoadOptions options, CancellationToken cancellationToken = default) where T : UnityEngine.Object
            => Root.LoadSubAssetWithOptionsAsync<T>(address, name, options, cancellationToken);
        public UniTask<AssetLease<T>> LoadAsync<T>(string address, CancellationToken cancellationToken = default) where T : UnityEngine.Object => Root.LoadAsync<T>(address, cancellationToken);
        public UniTask<AssetLease<T>> LoadSubAssetAsync<T>(string address, string name, CancellationToken cancellationToken = default) where T : UnityEngine.Object => Root.LoadSubAssetAsync<T>(address, name, cancellationToken);
        public UniTask<AssetInstance> InstantiateAsync(string address, Transform parent = null, CancellationToken cancellationToken = default) => Root.InstantiateAsync(address, parent, cancellationToken);
        public AssetPool CreatePool(string address, AssetPoolOptions options) => Root.CreatePool(address, options);
        public UniTask<AssetScene> LoadSceneAsync(string address, CancellationToken cancellationToken = default) => Root.LoadSceneAsync(address, cancellationToken);
        public AssetDiagnostics GetDiagnostics() => _runtime == null ? new AssetDiagnostics(new(), new(), new(), 0) : _runtime.Diagnostics();
        public void RefreshDiagnosticsMemory() => Root.RefreshDiagnosticsMemory();
    }
}
