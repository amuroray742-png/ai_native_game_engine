#nullable enable
using System;

namespace AiNative.UnityCore.TimeMachine
{
    /// <summary>只注销创建此票据的注册；重复注销及过期票据均无副作用。</summary>
    public sealed class TimeTicket : ITimeTicket
    {
        private readonly int _id;
        private Action<int>? _remove;
        private readonly Func<int, bool> _isActive;
        public int? Id => _remove != null && _isActive(_id) ? _id : (int?)null;
        public TicketType Type { get; }

        internal TimeTicket(int id, TicketType type, Action<int> remove, Func<int, bool> isActive)
        {
            _id = id;
            Type = type;
            _remove = remove;
            _isActive = isActive;
        }

        public void Dispose() { Unregister(); }

        public void Unregister()
        {
            Action<int>? remove = _remove;
            _remove = null;
            remove?.Invoke(_id);
        }
    }
}
