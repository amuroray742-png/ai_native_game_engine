#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore
{
    // One run owns its cancellation, completion, resources and cleanup record.
    internal sealed class CoreRun
    {
        internal readonly IModule[] Modules;
        internal readonly List<IModule> Entered = new List<IModule>();
        internal readonly List<Exception> CleanupErrors = new List<Exception>();
        internal readonly CancellationTokenSource Cancellation;
        internal readonly CancellationToken Token;
        internal readonly UniTaskCompletionSource Started = new UniTaskCompletionSource();
        internal readonly UniTaskCompletionSource Settled = new UniTaskCompletionSource();
        internal GameObject? Root;
        internal CoreLifetime? Lifetime;
        internal AlphaTimeEngine? Alpha;
        internal OmegaTimeEngine? Omega;
        internal bool Cleaned;
        internal readonly CancellationTokenSource ForcedExit = new CancellationTokenSource();
        internal readonly UniTaskCompletionSource CleanupCompleted = new UniTaskCompletionSource();
        internal bool Cleaning;
        private bool _cancelRequested;
        private CancellationTokenRegistration _startupCancellation;

        internal CoreRun(IModule[] modules, CancellationToken cancellationToken)
        {
            Modules = modules;
            Cancellation = new CancellationTokenSource();
            Token = Cancellation.Token;
            _startupCancellation = cancellationToken.Register(Cancel);
        }

        internal void FinishStartup()
        {
            _startupCancellation.Dispose();
            Token.ThrowIfCancellationRequested();
        }

        internal void DisposeCancellation()
        {
            _startupCancellation.Dispose();
            Cancellation.Dispose();
            ForcedExit.Dispose();
        }

        internal void Cancel()
        {
            if (_cancelRequested || Cleaned) { return; }
            _cancelRequested = true;
            try { Cancellation.Cancel(); }
            catch (Exception error) { CleanupErrors.Add(error); }
        }
    }
}
