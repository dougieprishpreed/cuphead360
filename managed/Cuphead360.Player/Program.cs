using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cuphead360.Engine;

namespace Cuphead360.Player
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "smoke";
            switch (cmd)
            {
                case "census": return Census(args.Skip(1).ToArray());
                case "smoke": return Smoke();
                case "selftest": return SelfTest.Run();
            }
            Console.Error.WriteLine("usage: Cuphead360.Player census [assembly...] | smoke");
            return 2;
        }

        // Every type loads and every method body compiles against this
        // runtime: the game's 2017 references (mscorlib 2.0, System.Core 3.5)
        // bound to .NET's facades, and Unity's engine methods to the engine.
        static int Census(string[] names)
        {
            if (names.Length == 0)
                names = new[] { "Assembly-CSharp", "Assembly-CSharp-firstpass", "UnityEngine.CoreModule", "UnityEngine.UI",
                                "UnityEngine.Physics2DModule", "UnityEngine.AnimationModule", "UnityEngine.AudioModule",
                                "UnityEngine.TextRenderingModule", "UnityEngine.IMGUIModule", "UnityEngine.UIModule",
                                "UnityEngine.JSONSerializeModule", "UnityEngine.SpriteMaskModule", "UnityEngine.PhysicsModule",
                                "Rewired_Core", "GalaxyCSharp" };
            int typeFail = 0, methodFail = 0, methods = 0, types = 0;
            var reasons = new Dictionary<string, int>();
            foreach (var n in names)
            {
                Assembly asm = Assembly.Load(n);
                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e)
                {
                    ts = e.Types.Where(t => t != null).ToArray();
                    foreach (var le in e.LoaderExceptions.Where(x => x != null))
                    {
                        typeFail++;
                        Count(reasons, "type: " + le.Message);
                    }
                }
                int asmFail = 0;
                foreach (var t in ts)
                {
                    types++;
                    if (t.IsGenericTypeDefinition) continue;
                    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    MethodBase[] ms;
                    try { ms = t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)).ToArray(); }
                    catch (Exception e) { typeFail++; Count(reasons, "members: " + e.GetType().Name + ": " + e.Message); continue; }
                    foreach (var m in ms)
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        methods++;
                        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
                        catch (Exception e)
                        {
                            methodFail++; asmFail++;
                            Count(reasons, e.GetType().Name + ": " + e.Message);
                        }
                    }
                }
                Console.WriteLine($"CENSUS {n}: {ts.Length} types, method failures {asmFail}");
            }
            foreach (var kv in reasons.OrderByDescending(k => k.Value).Take(60))
                Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
            Console.WriteLine($"CENSUS total: {types} types ({typeFail} failed), {methods} methods compiled ({methodFail} failed)");
            return typeFail + methodFail == 0 ? 0 : 1;
        }

        static void Count(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int n); d[k] = n + 1; }

        static int Smoke()
        {
            // Unity's own managed math, unchanged.
            var v = new UnityEngine.Vector3(3, 0, 4);
            Console.WriteLine("Vector3(3,0,4).magnitude = " + v.magnitude + ", normalized = " + v.normalized);
            Console.WriteLine("Mathf.Round(2.5f) = " + UnityEngine.Mathf.Round(2.5f) + ", Mathf.Round(3.5f) = " + UnityEngine.Mathf.Round(3.5f));
            Console.WriteLine("Engine methods reached but missing: " + Icall.MissingCount);
            return 0;
        }
    }
}
