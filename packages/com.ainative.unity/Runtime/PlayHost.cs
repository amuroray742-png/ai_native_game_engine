#nullable enable
using System;
using AiNative.Core;
using AiNative.Hosting;

namespace AiNative.Unity
{
    /// <summary>场景独占的推进器。场景在 OnEnable 装配、Update 调用 Pump、OnDisable 释放。</summary>
    public sealed class PlayHost<TCommand, TObservation> : IDisposable
    {
        private IPlaySession<TCommand, TObservation>? _session;
        private double _pendingSteps;
        private bool _disposed;
        private bool _faulted;
        public SequentialPresentationQueue<TObservation>? Sequential { get; private set; }
        public RealtimePresentationBuffer<TObservation>? Realtime { get; private set; }
        public double Interpolation => Math.Min(1, _pendingSteps);
        public bool IsFaulted => _faulted;

        /// <summary>工厂交付新会话的所有权。初始观察或队列创建失败时保留旧对局。</summary>
        public void Replace(Func<IPlaySession<TCommand, TObservation>> create)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlayHost<TCommand, TObservation>));
            }
            var next = create() ?? throw new InvalidOperationException("Missing candidate session.");
            if (ReferenceEquals(next, _session))
            {
                throw new InvalidOperationException("Replacement requires a new session.");
            }
            SequentialPresentationQueue<TObservation>? sequential = null;
            RealtimePresentationBuffer<TObservation>? realtime = null;
            try
            {
                var initial = next.CaptureObservation();
                if (next.Timing.TicksPerSecond == 0)
                {
                    sequential = new SequentialPresentationQueue<TObservation>(initial);
                }
                else
                {
                    realtime = new RealtimePresentationBuffer<TObservation>(initial);
                }
            }
            catch
            {
                next.Dispose();
                throw;
            }
            var previous = _session;
            InvalidateFrames();
            try
            {
                previous?.Dispose();
            }
            catch (Exception cleanup)
            {
                // The previous session may already be partially released. Neither session can be published.
                _session = null;
                Sequential = null;
                Realtime = null;
                _faulted = true;
                try
                {
                    next.Dispose();
                }
                catch (Exception candidateCleanup)
                {
                    throw new AggregateException("Failed to release both sessions.", cleanup, candidateCleanup);
                }
                throw new InvalidOperationException("Previous session cleanup failed; create a new match.", cleanup);
            }
            _session = next;
            Sequential = sequential;
            Realtime = realtime;
            _pendingSteps = 0;
            _faulted = false;
        }

        public long Submit(TCommand command) => Current.Submit(command);

        public TickResult<TObservation> AdvanceManual()
        {
            if (Current.Timing.TicksPerSecond != 0)
            {
                throw new InvalidOperationException("Fixed sessions advance in Pump.");
            }
            return Step();
        }

        public void Pump(double deltaSeconds, bool paused)
        {
            int frequency = Current.Timing.TicksPerSecond;
            if (frequency == 0 || paused)
            {
                return;
            }
            if (deltaSeconds < 0 || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            }
            _pendingSteps += deltaSeconds * frequency;
            for (int count = 0; count < 4 && _pendingSteps >= 1; count++)
            {
                Step();
                _pendingSteps -= 1;
            }
        }

        private IPlaySession<TCommand, TObservation> Current
        {
            get
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(PlayHost<TCommand, TObservation>));
                }
                if (_faulted)
                {
                    throw new InvalidOperationException("Faulted session must be replaced.");
                }
                return _session ?? throw new InvalidOperationException("No active session.");
            }
        }

        private TickResult<TObservation> Step()
        {
            try
            {
                var frame = Current.AdvanceTick();
                Sequential?.Enqueue(frame);
                Realtime?.Push(frame);
                return frame;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        private void InvalidateFrames()
        {
            Sequential?.Reset(Sequential.Presented);
            Realtime?.Reset(Realtime.Current);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            InvalidateFrames();
            Sequential = null;
            Realtime = null;
            var session = _session;
            _session = null;
            session?.Dispose();
        }
    }
}
