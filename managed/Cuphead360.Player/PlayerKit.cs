using System;
using System.Collections;
using System.Reflection;

namespace Cuphead360.Player
{
    /// <summary>
    /// What the player's buttons make in a level, read ahead at its hold (ConsoleHost): the equipped weapons' shots,
    /// their EX shots and effects (the weapons the weapon manager made for the loadout), and the equipped super. The
    /// game makes these from prefabs at the press, and a first EX or super waited for its pages from the drive (the
    /// forty-fifth run: the chaser's EX hitched the first time each level). Found by reflection on the game's own
    /// classes (AbstractLevelWeapon, AbstractPlaneWeapon, LevelPlayerWeaponManager), as the bot finds them.
    /// </summary>
    static class PlayerKit
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static Type levelWeapon, planeWeapon, levelManager;
        static bool looked;

        static Type GameType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
            }
            return null;
        }

        static object Member(object o, string name)
        {
            for (var t = o?.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, Any);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            }
            return null;
        }

        /// <summary>The sprites and clips asked for (0 when there is no player yet).</summary>
        public static int Ahead()
        {
            if (!looked)
            {
                looked = true;
                levelWeapon = GameType("AbstractLevelWeapon");
                planeWeapon = GameType("AbstractPlaneWeapon");
                levelManager = GameType("LevelPlayerWeaponManager");
            }
            int n = 0;
            try
            {
                foreach (var wt in new[] { levelWeapon, planeWeapon })
                {
                    if (wt == null) continue;
                    foreach (var w in UnityEngine.Object.FindObjectsOfType(wt))
                        for (var t = w.GetType(); t != null && t != typeof(UnityEngine.MonoBehaviour); t = t.BaseType)
                            foreach (var f in t.GetFields(Any | BindingFlags.DeclaredOnly))
                                if (f.Name.EndsWith("Prefab", StringComparison.Ordinal) && typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                                    n += Cuphead360.Engine.RenderList.AheadHierarchy(f.GetValue(w) as UnityEngine.Object);
                }
                if (levelManager != null)
                    foreach (var m in UnityEngine.Object.FindObjectsOfType(levelManager))
                    {
                        // the super the loadout has: LevelPlayerWeaponManager.superPrefabs.GetPrefab(player.stats.Loadout.super)
                        var supers = Member(m, "superPrefabs");
                        var super = Member(Member(Member(Member(m, "player"), "stats"), "Loadout"), "super");
                        var get = supers?.GetType().GetMethod("GetPrefab", Any);
                        if (get != null && super != null)
                            n += Cuphead360.Engine.RenderList.AheadHierarchy(get.Invoke(supers, new[] { super }) as UnityEngine.Object);
                    }
            }
            catch (Exception e)
            {
                Console.WriteLine("PLAYER kit read-ahead: " + e.GetType().Name + ": " + e.Message);
            }
            return n;
        }
    }
}
