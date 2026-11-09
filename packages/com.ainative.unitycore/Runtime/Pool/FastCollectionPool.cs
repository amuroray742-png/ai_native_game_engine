using System.Collections.Generic;
using UnityEngine.Pool;

namespace AiNative.UnityCore.Pool
{
    public class FastCollectionPool<TCollection, TItem> where TCollection : class, ICollection<TItem>, new()
    {
        internal static readonly ObjectPool<TCollection> s_Pool = new ObjectPool<TCollection>(
            () => new TCollection(), 
            null, 
            l => l.Clear(), 
            collectionCheck: false);

        public static TCollection Get()
        {
            return s_Pool.Get();
        }

        public static PooledObject<TCollection> Get(out TCollection value)
        {
            return s_Pool.Get(out value);
        }

        public static void Release(TCollection toRelease)
        {
            s_Pool.Release(toRelease);
        }
    }

    public class FastHashSetPool<T> : FastCollectionPool<HashSet<T>, T> { }
    public class FastListPool<T> : FastCollectionPool<List<T>, T> { }
    public class FastDictionaryPool<TKey, TValue> : FastCollectionPool<Dictionary<TKey, TValue>, KeyValuePair<TKey, TValue>> { }
}
