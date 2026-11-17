using System;
using System.Reflection;

namespace Cuphead360.Player
{
    /// <summary>
    /// Switches for playing the whole game through as a test. <c>--invincible 1</c> (on the console:
    /// <c>invincible.txt</c> next to default.xex) keeps the game's own debug invincibility on: its
    /// PlayerStatsManager.DebugInvincible, which its damage path checks before taking a heart (hits, pits
    /// and all); every player's Awake clears it, so it is set again each frame, after the frame's scene
    /// loading and before its physics and updates, where damage is dealt.
    /// </summary>
    static class TestSwitches
    {
        static PropertyInfo invincible;
        static MethodInfo setInvincible;
        static readonly object[] On = { true };
        static bool looked;

        public static void Invincible()
        {
            Cuphead360.Engine.PlayerLoop.InputUpdate += KeepInvincible;
            Console.WriteLine("TEST invincible: the player takes no damage (the game's PlayerStatsManager.DebugInvincible, kept on)");
        }

        static void KeepInvincible()
        {
            if (!looked)
            {
                looked = true;
                Type stats = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) stats ??= asm.GetType("PlayerStatsManager");
                invincible = stats?.GetProperty("DebugInvincible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                setInvincible = invincible?.GetSetMethod(true);
                if (setInvincible == null) Console.WriteLine("TEST invincible: PlayerStatsManager.DebugInvincible not found");
            }
            if (setInvincible != null && !(bool)invincible.GetValue(null)) setInvincible.Invoke(null, On);
        }
    }
}
