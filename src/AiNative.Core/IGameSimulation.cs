using System;
using System.Collections.Generic;

namespace AiNative.Core
{
    /// <summary>实例由一个运行时独占。拒绝输入无副作用；观察和状态必须隔离内部可变数据。</summary>
    public interface IGameSimulation<in TCommand, out TObservation, out TState> : IDisposable
        where TCommand : class
    {
        CommandResult Execute(TCommand command, DeterministicRandom random);
        IReadOnlyList<GameEvent> Advance(StepContext context);
        TObservation GetState();
        TState ExportState();
    }
}
