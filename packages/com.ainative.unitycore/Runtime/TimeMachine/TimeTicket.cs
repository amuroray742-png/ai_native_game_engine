namespace AiNative.UnityCore.TimeMachine
{
    public class TimeTicket : ITimeTicket
    {
        public int? Id { get; internal set; }

        public TicketType Type { get; internal set; }

        public TimeTicket(int id, TicketType t)
        {
            Id = id;
            Type = t;
        }

        public void Dispose()
        {
            Unregister();
        }

        public void Unregister()
        {
            if (Id == null) return;
            switch (Type)
            {
                case TicketType.Update:
                    Core.Inst.TimeMachine.RemoveUpdate(Id.Value);
                    break;
                case TicketType.LateUpdate:
                    Core.Inst.TimeMachine.RemoveLateUpdate(Id.Value);
                    break;
                case TicketType.FixedUpdate:
                    Core.Inst.TimeMachine.RemoveFixedUpdate(Id.Value);
                    break;
                case TicketType.OmegaLateUpdate:
                    Core.Inst.TimeMachine.RemoveOmegaLateUpdate(Id.Value);
                    break;
                case TicketType.Timer:
                    Core.Inst.TimeMachine.RemoveTimer(Id.Value);
                    break;
            }
        }

        /// <summary>
        /// 消除警告用(重载Equals和== !=)
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            int hashCode = -1056084179;
            hashCode = hashCode * -1521134295 + Id.GetHashCode();
            hashCode = hashCode * -1521134295 + Type.GetHashCode();
            return hashCode;
        }

        public override bool Equals(object obj)
        {
            return obj is TimeTicket ticket &&
                   Id == ticket.Id &&
                   Type == ticket.Type;
        }

        public static bool operator ==(TimeTicket left, TimeTicket right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TimeTicket left, TimeTicket right)
        {
            return !(left == right);
        }
    }
}
