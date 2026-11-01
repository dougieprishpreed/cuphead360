using System;
using UnityEngine;

namespace Cuphead360.Engine
{
    public static partial class Icalls
    {
        /// <summary>The ScriptableObject constructor registers the object; Unity then sends Awake and OnEnable.</summary>
        public static void UnityEngine_ScriptableObject__Internal_CreateScriptableObject(ScriptableObject self)
        {
            if (Objects.Of(self) != null) return;
            Objects.Register(self, new NAsset(), "");
            if (AssetDb.Loading > 0) return;   // a loaded asset gets its messages after its fields
            ScriptableMessages(self);
        }

        public static void ScriptableMessages(ScriptableObject so)
        {
            var t = so.GetType();
            foreach (var name in new[] { "Awake", "OnEnable" })
            {
                var m = MessageSet.Find(t, name);
                if (m == null) continue;
                try { m.Invoke(so, null); }
                catch (System.Reflection.TargetInvocationException e) { Exceptions.Report(e.InnerException, Objects.Of(so), name); }
            }
        }

        public static ScriptableObject UnityEngine_ScriptableObject__CreateInstanceFromType(Type type)
        {
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract) return null;
            return (ScriptableObject)Lifecycle.NewManaged(type);
        }

        public static ScriptableObject UnityEngine_ScriptableObject__CreateInstance(string className)
            => UnityEngine_ScriptableObject__CreateInstanceFromType(Find.TypeByName(className));

        public static void UnityEngine_ScriptableObject__INTERNAL_CALL_SetDirty(ScriptableObject self) { }
    }
}
