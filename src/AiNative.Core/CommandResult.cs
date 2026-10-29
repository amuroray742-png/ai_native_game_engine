using System;
using System.Collections.Generic;

namespace AiNative.Core
{
    /// <summary>规则提交后的通知数据；具体事件定义在玩法模块。</summary>
    public abstract class GameEvent
    {
    }

    public sealed class CommandResult
    {
        private CommandResult(string? error, GameEvent[] events)
        {
            Error = error;
            Events = Array.AsReadOnly((GameEvent[])events.Clone());
        }

        public bool Succeeded => Error == null;
        public string? Error { get; }
        public IReadOnlyList<GameEvent> Events { get; }

        public static CommandResult Accepted(params GameEvent[] events) => new CommandResult(null, events);

        public static CommandResult Rejected(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("拒绝结果必须包含原因。", nameof(error));
            }
            return new CommandResult(error, Array.Empty<GameEvent>());
        }
    }
}
