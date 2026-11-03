using System;

namespace AiNative.GridGame.Hosting
{
    /// <summary>结构、版本或内容校验失败；代码和路径用于机器诊断，不决定进程退出码。</summary>
    public sealed class ProtocolException : Exception
    {
        internal ProtocolException(string code, string message, string? path = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Path = path;
        }

        public string Code { get; }
        public string? Path { get; }
    }
}
