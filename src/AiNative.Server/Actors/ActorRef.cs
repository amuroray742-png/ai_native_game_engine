using System;

namespace AiNative.Server.Actors
{
    public enum PostResult
    {
        Accepted,
        Full,
        Stopped
    }

    /// <summary>只允许投递不可变消息；Accepted 不代表业务执行成功。</summary>
    public sealed class ActorRef<T> where T : notnull
    {
        private readonly ActorMailbox<T> _mailbox;

        internal ActorRef(string actorId, ActorMailbox<T> mailbox)
        {
            ActorId = actorId;
            Incarnation = Guid.NewGuid();
            _mailbox = mailbox;
        }

        public string ActorId { get; }
        public Guid Incarnation { get; }

        /// <summary>调用方按消息拥有的正文计算 byteCount；预算包含正在执行的消息。</summary>
        public PostResult TryPost(T message, int byteCount) => _mailbox.TryPost(message, byteCount);

        /// <summary>可信宿主的生命周期消息使用控制预留；仍与普通消息保持同一 FIFO，不自动绕过屏障。</summary>
        public PostResult TryPostControl(T message, int byteCount) => _mailbox.TryPost(message, byteCount, isControl: true);
    }
}
