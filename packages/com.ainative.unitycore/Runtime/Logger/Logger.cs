using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AiNative.UnityCore.Utils;


namespace AiNative.UnityCore.Logger
{
    public enum ELogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    public struct LogInfo
    {
        ELogLevel Level { get; }
        string Message { get; }

        public LogInfo(ELogLevel level, string message)
        {
            Level = level;
            Message = message;
        }
    }

    public sealed class Logger
    {
        private volatile ELogLevel _level;
        public ELogLevel Level { get => _level; set => _level = value; }

        public Logger(ELogLevel level = ELogLevel.Debug)
        {
            Level = level;
        }

        public void SetLogLevel(ELogLevel level)
        {
            Level = level;
        }

        public ELogLevel GetLogLevel()
        {
            return Level;
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Debug(string message)
        {
            if (!IsEnabled(ELogLevel.Debug)) { return; }
            Log(ELogLevel.Debug, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Debug(string message, UnityEngine.Object context)
        {
            if (!IsEnabled(ELogLevel.Debug)) { return; }
            Log(ELogLevel.Debug, message, context);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Debug(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Debug)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Debug, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Debug(string format, UnityEngine.Object context, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Debug)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Debug, message, context);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string message)
        {
            if (!IsEnabled(ELogLevel.Info)) { return; }
            Log(ELogLevel.Info, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string message, UnityEngine.Object context)
        {
            if (!IsEnabled(ELogLevel.Info)) { return; }
            Log(ELogLevel.Info, message, context);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Info)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Info, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string format, UnityEngine.Object context, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Info)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Info, message, context);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string message)
        {
            if (!IsEnabled(ELogLevel.Warn)) { return; }
            Log(ELogLevel.Warn, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string message, UnityEngine.Object context)
        {
            if (!IsEnabled(ELogLevel.Warn)) { return; }
            Log(ELogLevel.Warn, message, context);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Warn)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Warn, message, null);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Warn)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Warn, message, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string message)
        {
            if (!IsEnabled(ELogLevel.Error)) { return; }
            Log(ELogLevel.Error, message, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Error)) { return; }
            Log(ELogLevel.Error, message, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Error)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Error, message, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Error)) { return; }
            string message = FormatMessage(format, args);
            Log(ELogLevel.Error, message, obj);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(Exception exception)
        {
            if (!IsEnabled(ELogLevel.Error))
            { return; }
            LogException(exception, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(Exception exception, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Error)) { return; }
            LogException(exception, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEnabled(ELogLevel level)
        {
            return Level <= level;
        }

        private string FormatMessage(string format, object[] args)
        {
            StringBuilder messageBuilder = new StringBuilder();
            messageBuilder.AppendFormat(format, args);
            return messageBuilder.ToString();
        }

        private void Log(ELogLevel level, string message, UnityEngine.Object? obj)
        {
            string logStr;
            StringBuilder infoBuilder = GetFormatString(level, message);
            if (level == ELogLevel.Error)
            {
                StackFrame[] stackFrames = new StackTrace().GetFrames();
                for (int i = 0; i < stackFrames.Length; i++)
                {
                    StackFrame frame = stackFrames[i];
                    string declaringTypeName = frame.GetMethod()?.DeclaringType?.FullName ?? "<unknown>";
                    string methodName = frame.GetMethod().Name;

                    infoBuilder.AppendFormat("[{0}::{1}\n", declaringTypeName, methodName);
                }
            }

            logStr = infoBuilder.ToString();
            switch (level)
            {
                case ELogLevel.Debug:
                    UnityEngine.Debug.Log(logStr, obj);
                    break;
                case ELogLevel.Info:
                    UnityEngine.Debug.Log(logStr, obj);
                    break;
                case ELogLevel.Warn:
                    UnityEngine.Debug.LogWarning(logStr, obj);
                    break;
                case ELogLevel.Error:
                    UnityEngine.Debug.LogError(logStr, obj);
                    break;
            }
        }

        private void LogException(Exception exception, UnityEngine.Object? context)
        {
            UnityEngine.Debug.LogException(exception, context);
        }

        private StringBuilder GetFormatString(ELogLevel logLevel, string message)
        {
            var builder = new StringBuilder();
            switch (logLevel)
            {
                case ELogLevel.Debug:
#if UNITY_EDITOR
                    var color = ColorUtil.White;
#else
                    var color = ColorUtil.Blue;
#endif
                    builder.AppendFormat("<color=#0099bc><b>[debug]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, color);
                    break;
                case ELogLevel.Info:
                    builder.AppendFormat("<color=#0099bc><b>[info]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Green);
                    break;
                case ELogLevel.Warn:
                    builder.AppendFormat("<color=#0099bc><b>[warn]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Orange);
                    break;
                case ELogLevel.Error:
                    builder.AppendFormat("<color=#ff7f50><b>[error]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Red);
                    break;
            }

            return builder;
        }

        private string GetNowFormatDate(DateTime date)
        {
            return date.ToString("yyyy-MM-dd HH:mm:ss fff");
        }
    }
}
