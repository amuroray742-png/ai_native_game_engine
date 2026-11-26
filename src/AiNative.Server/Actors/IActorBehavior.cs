namespace AiNative.Server.Actors
{
    /// <summary>由执行器串行调用。处理器不得阻塞等待其他 Actor 或执行 I/O。</summary>
    public interface IActorBehavior<in T> where T : notnull
    {
        void OnStart();
        void OnMessage(T message);
        void OnStop();
    }
}
