using System;

namespace AiNative.Headless
{
    /// <summary>宿主输入、模拟与文件边界的具体失败信息，供宿主映射协议；不依赖异常文本判断分类。</summary>
    internal sealed class HostOperationException : Exception
    {
        internal HostOperationException(string category, string code, string message, string? path = null, Exception? inner = null)
            : base(message, inner)
        {
            Category = category;
            Code = code;
            Path = path;
        }

        internal string Category { get; }
        internal string Code { get; }
        internal string? Path { get; }
    }
}
