using System;

namespace AiNative.UnityCore.Single
{
    /// <summary>
    /// 普通C#类单件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class Singleton<T> where T : Singleton<T>
    {
        protected Singleton() { }

        class Nested
        {
            static Nested() { }
            internal static readonly Lazy<T> Inst = new Lazy<T>(() => Activator.CreateInstance(typeof(T), true) as T);
        }

        public static T Inst
        {
            get
            {
                return Nested.Inst.Value;
            }
        }
    }
}
