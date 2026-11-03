using System;
using System.Collections.Generic;

namespace AiNative.GridGame.Hosting
{
    /// <summary>保存待播放步骤和已落稳状态；不执行规则，不读取时间，不调用动画回调。</summary>
    public sealed class GridPresentationQueue
    {
        private readonly Queue<GridStep> _pending = new Queue<GridStep>();
        private GridStep? _active;
        private long _lastQueuedStep;

        public GridPresentationQueue(GridObservation initial)
        {
            Presented = initial ?? throw new ArgumentNullException(nameof(initial));
            _lastQueuedStep = initial.CompletedStepCount;
        }

        public GridObservation Presented { get; private set; }
        public int PendingCount => _pending.Count + (_active == null ? 0 : 1);

        /// <summary>仅接受当前对局连续的已完成步骤；业务拒绝也占一个步骤。</summary>
        public void Enqueue(GridStep step)
        {
            if (step == null)
            {
                throw new ArgumentNullException(nameof(step));
            }
            if (!Presented.IsFromSameGame(step.State)
                || _lastQueuedStep == long.MaxValue || step.Step.Sequence != _lastQueuedStep + 1)
            {
                throw new InvalidOperationException("表现步骤必须属于当前对局且连续有序。");
            }
            _pending.Enqueue(step);
            _lastQueuedStep = step.Step.Sequence;
        }

        /// <summary>取得下一步开始播放；当前步未完成时不允许开始另一帧。</summary>
        public GridStep? BeginNext()
        {
            if (_active != null)
            {
                throw new InvalidOperationException("当前表现步骤尚未完成。");
            }
            _active = _pending.Count == 0 ? null : _pending.Dequeue();
            return _active;
        }

        /// <summary>动画回调写画面前检查；检查、写入和 Complete 须在同一主线程同步调用内完成。</summary>
        public bool IsActive(GridStep step) => step != null && ReferenceEquals(step, _active);

        /// <summary>宿主先检查 IsActive、应用本步最终画面，再确认；旧动画或重复回调返回 false。</summary>
        public bool Complete(GridStep step)
        {
            if (!IsActive(step))
            {
                return false;
            }
            Presented = step.State;
            _active = null;
            return true;
        }

        /// <summary>重载/恢复时宿主先取消动画，再清空队列并按 initial 重建画面；旧 Complete 失效。</summary>
        public void Reset(GridObservation initial)
        {
            if (initial == null)
            {
                throw new ArgumentNullException(nameof(initial));
            }
            _pending.Clear();
            _active = null;
            Presented = initial;
            _lastQueuedStep = initial.CompletedStepCount;
        }

        /// <summary>指定逻辑检查点已落稳且无待播步骤；调用方还须完成相机/UI刷新后截图。</summary>
        public bool IsSettledAt(GridObservation checkpoint) => checkpoint != null
            && Presented.IsFromSameGame(checkpoint) && checkpoint.PendingCount == 0
            && Presented.CompletedStepCount == checkpoint.CompletedStepCount && PendingCount == 0;
    }
}
