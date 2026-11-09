using System;

namespace AiNative.UnityCore.TimeMachine
{
    public interface ITimeTicket : IDisposable
    {
        int? Id { get; }
        TicketType Type { get; }
        void Unregister();
    }
}
