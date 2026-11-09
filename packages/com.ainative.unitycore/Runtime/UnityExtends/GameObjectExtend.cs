using UnityEngine;

namespace AiNative.UnityCore.UnityExtends
{
    public static class GameObjectExtend
    {
        public static GameObject FindOrCreateGameObject(string name)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                go = new GameObject(name);
            }
            return go;
        }
    }
}
