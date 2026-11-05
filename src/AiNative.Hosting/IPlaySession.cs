using System;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>场景宿主独占的强类型对局。Submit 只排队，每次 AdvanceTick 推进一次。</summary>
    public interface IPlaySession<TCommand, TObservation> : IDisposable
    {
        SimulationTiming Timing { get; }
        long Submit(TCommand command);
        Observation<TObservation> CaptureObservation();
        TickResult<TObservation> AdvanceTick();
    }
}
