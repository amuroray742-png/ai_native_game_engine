namespace Wepie.Core.Pool
{
    /// <summary>
    /// 封装Unity的ObjectPool, 因为Unity的pool默认CollectionChecks是true的
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class FastGenericPool<T> where T : class, new()
    {
        internal const int DefaultCapacity = 32;
        internal static readonly UnityEngine.Pool.ObjectPool<T> s_Pool = new UnityEngine.Pool.ObjectPool<T>(() => new T(), collectionCheck: false, defaultCapacity: DefaultCapacity);

        public static T Get()
        {
            return s_Pool.Get();
        }

        public static UnityEngine.Pool.PooledObject<T> Get(out T value)
        {
            return s_Pool.Get(out value);
        }

        public static void Release(T toRelease)
        {
            s_Pool.Release(toRelease);
        }
    }
}
