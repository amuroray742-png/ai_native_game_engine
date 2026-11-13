namespace AiNative.UnityCore.UI
{
    /// <summary>
    /// Synchronous window bindings. Suspend and Unbind are independent, idempotent cleanup steps.
    /// Either may be retried after partial failure or after the View has been destroyed;
    /// Suspend may be retried after a successful Unbind. Neither may require a future frame.
    /// </summary>
    public abstract class UIPresenter
    {
        public abstract void Bind();
        public abstract void Resume();
        public abstract void Suspend();
        public abstract void Unbind();
    }
}
