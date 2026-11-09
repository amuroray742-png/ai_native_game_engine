using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Tests
{
    public sealed class LoggerTests
    {
        [TestCase(StackTraceLogType.None)]
        [TestCase(StackTraceLogType.ScriptOnly)]
        public void Error_UsesUnityStackTraceSettingWithoutAddingStackToMessage(StackTraceLogType setting)
        {
            const string marker = "Logger stack trace regression 42";
            StackTraceLogType previous = Application.GetStackTraceLogType(LogType.Error);
            string capturedMessage = null;
            string capturedStack = null;
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Error && message.Contains(marker))
                {
                    capturedMessage = message;
                    capturedStack = stack;
                }
            };

            Application.logMessageReceived += capture;
            try
            {
                Application.SetStackTraceLogType(LogType.Error, setting);
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(marker)));
                new Logger.Logger().Error("Logger stack trace regression {0}", 42);

                Assert.That(capturedMessage, Does.EndWith(marker + "</color>"));
                Assert.That(capturedMessage, Does.Not.Contain("\n"));
                if (setting == StackTraceLogType.None)
                {
                    Assert.That(capturedStack, Is.Null.Or.Empty);
                }
                else
                {
                    Assert.That(capturedStack, Does.Contain(nameof(Error_UsesUnityStackTraceSettingWithoutAddingStackToMessage)));
                }
            }
            finally
            {
                Application.logMessageReceived -= capture;
                Application.SetStackTraceLogType(LogType.Error, previous);
            }
        }
    }
}
