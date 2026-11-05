#nullable enable
using UnityEngine;

namespace AiNative.Unity
{
    /// <summary>具体游戏继承后作为场景组件。一个场景组件串行采样输入、推进并呈现。</summary>
    public abstract class PlaySceneHost<TCommand, TObservation> : MonoBehaviour
    {
        protected PlayHost<TCommand, TObservation> Host { get; private set; } = null!;

        protected abstract void ConnectScene();
        protected abstract void DisconnectScene();
        protected virtual void SampleInput() { }
        protected abstract void PresentFrame();

        protected void OnEnable()
        {
            Host = new PlayHost<TCommand, TObservation>();
            try
            {
                ConnectScene();
            }
            catch
            {
                enabled = false;
                throw;
            }
        }

        protected void Update()
        {
            if (Host.Sequential == null && Host.Realtime == null)
            {
                return;
            }
            try
            {
                SampleInput();
                Host.Pump(Time.deltaTime, Time.timeScale == 0);
                PresentFrame();
            }
            catch
            {
                enabled = false;
                throw;
            }
        }

        protected void OnDisable()
        {
            try
            {
                DisconnectScene();
            }
            finally
            {
                Host?.Dispose();
            }
        }
    }
}
