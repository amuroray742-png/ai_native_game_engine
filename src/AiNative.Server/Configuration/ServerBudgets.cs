using System;
using AiNative.Server.Transport;

namespace AiNative.Server.Configuration
{
    /// <summary>待测初值，不代表容量承诺；所有额度由拥有资源的宿主执行。</summary>
    public sealed record ServerBudgets
    {
        public int MaxConnections { get; init; } = 256;
        public int MaxHandshakes { get; init; } = 16;
        public int MaxRooms { get; init; } = 100;
        public int MaxRoomThreads { get; init; } = 100;
        public int MaxDirectoryEntries { get; init; } = 200;
        public int MaxFrameBytes { get; init; } = 256 * 1024;
        public int ReceiveFrames { get; init; } = 1024;
        public long ReceiveBytes { get; init; } = 8 * 1024 * 1024;
        public int HandshakeSeconds { get; init; } = 5;
        public int ReadSeconds { get; init; } = 10;
        public int FrameSeconds { get; init; } = 5;
        public int WriteSeconds { get; init; } = 5;
        public int ShutdownSeconds { get; init; } = 10;
        public SendQueueLimits Send { get; init; } = new SendQueueLimits();

        internal void Validate()
        {
            if (MaxConnections <= 0 || MaxHandshakes <= 0 || MaxHandshakes > MaxConnections || MaxRooms <= 0 ||
                MaxRoomThreads <= 0 || MaxRoomThreads > MaxRooms || MaxDirectoryEntries <= 0 ||
                MaxFrameBytes <= 0 || MaxFrameBytes > 256 * 1024 || ReceiveFrames <= 0 || ReceiveBytes < MaxFrameBytes ||
                HandshakeSeconds <= 0 || HandshakeSeconds > 60 || ReadSeconds <= 0 || ReadSeconds > 3600 ||
                FrameSeconds <= 0 || FrameSeconds > 3600 || WriteSeconds <= 0 || WriteSeconds > 3600 || ShutdownSeconds <= 0 || ShutdownSeconds > 300 || Send == null)
            {
                throw new ArgumentException("无效的服务器容量或期限配置。");
            }
            Send.Validate();
            if (Send.RouteBytes < MaxFrameBytes || Send.ControlBytes < MaxFrameBytes)
            {
                throw new ArgumentException("接收、路由发送与控制预算必须容纳最大合法帧。");
            }
        }
    }
}
