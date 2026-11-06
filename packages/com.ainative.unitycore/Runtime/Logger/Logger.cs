using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AiNative.UnityCore.Single;
using AiNative.UnityCore.Utils;


namespace AiNative.UnityCore.Logger
{
    public enum ELogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Exception = 4
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

    public class Logger : Singleton<Logger>
    {
        private volatile ELogLevel _level;
        public ELogLevel Level { get => _level; set => _level = value; }

        [ThreadStatic]
        private readonly StringBuilder _stringBuilder;
        [ThreadStatic]
        private readonly StringBuilder _stringBuilderFormat;

        private Logger(ELogLevel level = ELogLevel.Debug)
        {
            _stringBuilder = new StringBuilder();
            _stringBuilderFormat = new StringBuilder();
            Level = level;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEnabled(ELogLevel level)
        {
            return Level <= level;
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
        public void Debug(string message)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            Log(ELogLevel.Debug, message);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Debug, message);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string message)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            Log(ELogLevel.Warn, message);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(Exception e)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            Log(ELogLevel.Warn, e);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Warn, message);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Warn, message, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string message)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            Log(ELogLevel.Error, message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(Exception exception)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            Log(ELogLevel.Error, exception);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Error, message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Error, message, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exception(string message)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            Log(ELogLevel.Exception, message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exception(Exception exception)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            Log(ELogLevel.Exception, exception);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exception(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Exception, message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exception(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Exception, message, obj);
        }

        // Keep explicit object[] calls compatible. Fixed argument calls use these generic
        // overloads so arrays and boxing happen only after the level check.
        // Specify Unity object context with the named argument obj: to distinguish it from a format value.
#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0>(string format, T0 arg0, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1>(string format, T0 arg0, T1 arg1, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5, T6>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5, T6, T7>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5, T6, T7, T8>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
            Log(ELogLevel.Debug, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Debug<TState>(TState state, Func<TState, string> messageFactory, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Debug))
                return;
            Log(ELogLevel.Debug, messageFactory(state), obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0>(string format, T0 arg0, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1>(string format, T0 arg0, T1 arg1, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5, T6>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5, T6, T7>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5, T6, T7, T8>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
            Log(ELogLevel.Info, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Info<TState>(TState state, Func<TState, string> messageFactory, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            Log(ELogLevel.Info, messageFactory(state), obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0>(string format, T0 arg0, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1>(string format, T0 arg0, T1 arg1, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5, T6>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5, T6, T7>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5, T6, T7, T8>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
            Log(ELogLevel.Warn, message, obj);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        public void Warning<TState>(TState state, Func<TState, string> messageFactory, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Warn))
                return;
            Log(ELogLevel.Warn, messageFactory(state), obj);
        }

        public void Error(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0>(string format, T0 arg0, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1>(string format, T0 arg0, T1 arg1, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5, T6>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5, T6, T7>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5, T6, T7, T8>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
            Log(ELogLevel.Error, message, obj);
        }

        public void Error<TState>(TState state, Func<TState, string> messageFactory, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Error))
                return;
            Log(ELogLevel.Error, messageFactory(state), obj);
        }

        public void Exception(string message, UnityEngine.Object obj)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0>(string format, T0 arg0, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1>(string format, T0 arg0, T1 arg1, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5, T6>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5, T6, T7>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5, T6, T7, T8>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(string format, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            string message = FormatMessage(format, new object[] { arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
            Log(ELogLevel.Exception, message, obj);
        }

        public void Exception<TState>(TState state, Func<TState, string> messageFactory, UnityEngine.Object obj = null)
        {
            if (!IsEnabled(ELogLevel.Exception))
                return;
            Log(ELogLevel.Exception, messageFactory(state), obj);
        }

        private string FormatMessage(string format, object[] args)
        {
            _stringBuilderFormat.Clear();
            return _stringBuilderFormat.AppendFormat(format, args).ToString();
        }

        private void Log(ELogLevel level, string message, UnityEngine.Object obj = null)
        {
            string logStr;
            StringBuilder infoBuilder = GetFormatString(level, message);
            if (level == ELogLevel.Error || level == ELogLevel.Exception)
            {
                StackFrame[] stackFrames = new StackTrace().GetFrames();
                for (int i = 0; i < stackFrames.Length; i++)
                {
                    StackFrame frame = stackFrames[i];
                    string declaringTypeName = frame.GetMethod().DeclaringType.FullName;
                    string methodName = frame.GetMethod().Name;

                    infoBuilder.AppendFormat("[{0}::{1}\n", declaringTypeName, methodName);
                }
            }

            logStr = infoBuilder.ToString();
            // CacheLog(level, logStr);
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
                case ELogLevel.Exception:
                    UnityEngine.Debug.LogError(logStr, obj);
                    break;
            }
        }
        private void Log(ELogLevel level, Exception message)
        {
            // CacheLog(level, logStr);
            switch (level)
            {
                case ELogLevel.Debug:
                    UnityEngine.Debug.Log(message);
                    break;
                case ELogLevel.Info:
                    UnityEngine.Debug.Log(message);
                    break;
                case ELogLevel.Warn:
                    UnityEngine.Debug.LogWarning(message);
                    break;
                case ELogLevel.Error:
                    UnityEngine.Debug.LogError(message);
                    break;
                case ELogLevel.Exception:
                    UnityEngine.Debug.LogError(message);
                    break;
            }
        }
#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string message)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            Log(ELogLevel.Info, message);
        }
#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string format, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Info, message);
        }

#if WEPIE_RELEASE
        [Conditional("FALSE")]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Info(string format, UnityEngine.Object obj, params object[] args)
        {
            if (!IsEnabled(ELogLevel.Info))
                return;
            string message = FormatMessage(format, args);
            Log(ELogLevel.Info, message, obj);
        }

        private StringBuilder GetFormatString(ELogLevel logLevel, string message)
        {
            _stringBuilder.Clear();
            switch (logLevel)
            {
                case ELogLevel.Debug:
#if UNITY_EDITOR
                    var color = ColorUtil.White;
#else
                    var color = ColorUtil.Blue;
#endif
                    _stringBuilder.AppendFormat("<color=#0099bc><b>[debug]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, color);
                    break;
                case ELogLevel.Info:
                    _stringBuilder.AppendFormat("<color=#0099bc><b>[info]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Green);
                    break;
                case ELogLevel.Warn:
                    _stringBuilder.AppendFormat("<color=#0099bc><b>[warn]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Orange);
                    break;
                case ELogLevel.Error:
                    _stringBuilder.AppendFormat("<color=#ff7f50><b>[error]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Red);
                    break;
                case ELogLevel.Exception:
                    _stringBuilder.AppendFormat("<color=#ffc0cb><b>[error]</b></color>[{0}] - <color=#{2}>{1}</color>",
                        GetNowFormatDate(System.DateTime.Now), message, ColorUtil.Exception);
                    break;
            }

            return _stringBuilder;
        }

        private string GetNowFormatDate(DateTime date)
        {
            return date.ToString("yyyy-MM-dd HH:mm:ss fff");
        }
    }
}
