using System;

namespace AiNative.UnityCore.Exceptions
{
    public class UniCoreException : Exception
    {
        public int ErrorCode { get; protected set; }
        public string Module { get; protected set; }
        public UniCoreException(string msg) : base(msg)
        {
            InitModule();
        }

        public UniCoreException(string message, Exception innerException) : base(message, innerException)
        {
            InitModule();
        }

        public UniCoreException(string msg, int errorCode) : base(msg)
        {
            InitModule();
            ErrorCode = errorCode;
        }

        public UniCoreException(int errorCode)
        {
            InitModule();
            ErrorCode = errorCode;
        }

        public UniCoreException()
        {
            InitModule();
        }

        protected virtual void InitModule()
        {
            Module = "AiNative.UnityCore";
        }
    }
}
