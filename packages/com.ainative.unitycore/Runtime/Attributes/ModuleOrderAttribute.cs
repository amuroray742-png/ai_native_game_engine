using System;

namespace AiNative.UnityCore.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ModuleOrderAttribute : Attribute
    {
        public int Order = 100;
        public ModuleOrderAttribute(int order = 100)
        {
            Order = order;
        }
    }

}
