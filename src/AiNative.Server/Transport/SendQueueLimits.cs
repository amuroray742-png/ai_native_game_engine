using System;

namespace AiNative.Server.Transport
{
    public sealed record SendQueueLimits(int MaxFrames = 1024, long MaxBytes = 8 * 1024 * 1024,
        int ControlFrames = 128, long ControlBytes = 1024 * 1024, int RouteFrames = 64,
        long RouteBytes = 256 * 1024, int QuantumBytes = 32 * 1024, int ControlBurst = 4)
    {
        internal void Validate()
        {
            if (MaxFrames <= 1 || MaxBytes <= 1 || ControlFrames <= 0 || ControlFrames >= MaxFrames ||
                ControlBytes <= 0 || ControlBytes >= MaxBytes || RouteFrames <= 0 || RouteFrames > MaxFrames - ControlFrames ||
                RouteBytes <= 0 || RouteBytes > MaxBytes - ControlBytes || QuantumBytes <= 0 || ControlBurst <= 0)
            {
                throw new ArgumentException("发送预算必须为正，且保留独立的控制与路由容量。");
            }
        }
    }
}
