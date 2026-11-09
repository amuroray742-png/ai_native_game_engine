#nullable enable
using System;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetException : Exception
    {
        public string Code { get; }
        public string Address { get; }
        public string Bundle { get; }
        public string Stage { get; }

        public AssetException(string code, string message, string address = "", string bundle = "", string stage = "", Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Address = address;
            Bundle = bundle;
            Stage = stage;
        }
        internal static Exception WithContext(Exception error, string address, string bundle, string stage)
        {
            if (error is OperationCanceledException || error is ObjectDisposedException) { return error; }
            var known = error as AssetException;
            return new AssetException(known?.Code ?? "AssetOperationFailed", error.Message, address,
                string.IsNullOrEmpty(known?.Bundle) ? bundle : known!.Bundle,
                string.IsNullOrEmpty(known?.Stage) ? stage : known!.Stage, error);
        }
    }
}
