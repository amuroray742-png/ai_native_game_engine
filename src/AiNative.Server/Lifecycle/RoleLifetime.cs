using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Lifecycle
{
    public enum RoleState { Starting, Live, Ready, Draining, Stopping, Stopped, Faulted }
    public sealed record RoleStopFailure(string Resource, bool TimedOut);
    public sealed record RoleStopResult(bool Clean, ReadOnlyCollection<RoleStopFailure> Failures);

    /// <summary>
    /// 进程监督者拥有的生命周期。按注册逆序关闭，全部资源共享同一宽限期限。
    /// stop 回调必须立即返回 Task 且响应取消；不在这里同步等待 Actor 或跨线程释放模拟。
    /// </summary>
    public sealed class RoleLifetime
    {
        private readonly object _gate = new object();
        private readonly List<(string Name, Func<CancellationToken, Task> Stop)> _resources = new List<(string, Func<CancellationToken, Task>)>();
        private RoleState _state = RoleState.Starting;
        private bool _criticalFailure;
        private Task<RoleStopResult>? _stop;

        public RoleState State { get { lock (_gate) { return _state; } } }

        public void Register(string name, Func<CancellationToken, Task> stop)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(stop);
            lock (_gate)
            {
                if (_state != RoleState.Starting || _resources.Count >= 1024) { throw new InvalidOperationException("只能在启动阶段登记有界资源组。"); }
                foreach (var resource in _resources)
                {
                    if (resource.Name == name) { throw new ArgumentException("资源名称重复。", nameof(name)); }
                }
                _resources.Add((name, stop));
            }
        }

        public void MarkLive() => Transition(RoleState.Starting, RoleState.Live);
        public void MarkReady() => Transition(RoleState.Live, RoleState.Ready);

        public void BeginDrain()
        {
            lock (_gate)
            {
                if (_state == RoleState.Live || _state == RoleState.Ready) { _state = RoleState.Draining; }
            }
        }

        public void ReportCriticalFailure()
        {
            lock (_gate)
            {
                _criticalFailure = true;
                _state = RoleState.Faulted;
            }
        }

        /// <summary>幂等：第一个调用确定关闭宽限。返回不干净结果时部署层仍须终止未退出资源。</summary>
        public Task<RoleStopResult> StopAsync(TimeSpan grace)
        {
            if (grace <= TimeSpan.Zero || grace > TimeSpan.FromMinutes(5)) { throw new ArgumentOutOfRangeException(nameof(grace)); }
            TaskCompletionSource<RoleStopResult> completion;
            lock (_gate)
            {
                if (_stop != null) { return _stop; }
                _state = RoleState.Stopping;
                completion = new TaskCompletionSource<RoleStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stop = completion.Task;
            }
            _ = StopCoreAsync(grace, completion);
            return completion.Task;
        }

        private void Transition(RoleState expected, RoleState next)
        {
            lock (_gate)
            {
                if (_state != expected) { throw new InvalidOperationException("非法角色生命周期转换。"); }
                _state = next;
            }
        }

        private async Task StopCoreAsync(TimeSpan grace, TaskCompletionSource<RoleStopResult> completion)
        {
            using var deadline = new CancellationTokenSource(grace);
            var failures = new List<RoleStopFailure>();
            for (int index = _resources.Count - 1; index >= 0; index--)
            {
                var resource = _resources[index];
                Task? stopping = null;
                try
                {
                    stopping = resource.Stop(deadline.Token);
                    await stopping.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    failures.Add(new RoleStopFailure(resource.Name, deadline.IsCancellationRequested));
                    if (stopping != null) { _ = ObserveLateAsync(stopping); }
                }
            }
            lock (_gate)
            {
                bool clean = failures.Count == 0 && !_criticalFailure;
                _state = clean ? RoleState.Stopped : RoleState.Faulted;
                completion.SetResult(new RoleStopResult(clean, failures.AsReadOnly()));
            }
        }

        private static async Task ObserveLateAsync(Task stopping)
        {
            try { await stopping.ConfigureAwait(false); }
            catch (Exception) { /* 超时资源已记录失败；只观察迟到异常，不改写为正常退出。 */ }
        }
    }
}
