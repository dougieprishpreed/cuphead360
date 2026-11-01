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
                case "run": return Run(args.Skip(1).ToArray());
                case "fields": return Fields(args.Skip(1).ToArray());
                case "resource": return Resource(args.Skip(1).ToArray());
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

        /// <summary>
        /// Boot the original game from converted data: build scene 0, then the
        /// player loop for --frames frames at 60 Hz. Saves and settings go to
        /// --save (port-local; the installed game's saves are never touched).
        /// </summary>
        static int Run(string[] a)
        {
            string data = "work/GameData", save = "work/managed/save";
            int frames = 600, report = 60, seed = 360;
            for (int i = 0; i + 1 < a.Length; i += 2)
                switch (a[i])
                {
                    case "--data": data = a[i + 1]; break;
                    case "--save": save = a[i + 1]; break;
                    case "--frames": frames = int.Parse(a[i + 1]); break;
                    case "--report": report = int.Parse(a[i + 1]); break;
                    case "--seed": seed = int.Parse(a[i + 1]); break;
                }
            // Unity seeds Random from the clock; runs here are reproducible.
            Icalls.SeedRandom(seed);
            Platform.SaveRoot = System.IO.Path.GetFullPath(save);
            Platform.Guard(Platform.SaveRoot);
            AssetDb.Init(System.IO.Path.GetFullPath(data));
            Cuphead360.Engine.SceneLoader.Load(0, false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 1; f <= frames && !Icalls.QuitRequested; f++)
            {
                PlayerLoop.Frame(1f / 60f);
                if (report > 0 && f % report == 0)
                    Console.WriteLine($"FRAME {f} t={UnityEngine.Time.time:0.00} scene={Cuphead360.Engine.Scenes.Active?.Name} objects={Objects.LiveCount} exceptions={Exceptions.Count} missing={Icall.MissingCount}");
            }
            Console.WriteLine($"RUN done: {frames} frames in {sw.Elapsed.TotalSeconds:0.0} s; scene loads {Cuphead360.Engine.SceneLoader.Loads}; active scene {Cuphead360.Engine.Scenes.Active?.Name}; "
                + $"exceptions {Exceptions.Count}; fields decoded {FieldDecoder.Decoded}, mismatched {FieldDecoder.Mismatched}; data problems {AssetDb.Problems.Count}; missing engine methods {Icall.MissingCount}");
            foreach (var kv in Icall.Report()) Console.WriteLine("  missing " + kv.Key + " x" + kv.Value);
            Console.WriteLine($"PROPS: {Props.Count} property accessors served from the property bag (state only)");
            foreach (var kv in Props.Report()) Console.WriteLine("  props " + kv.Key + " (" + kv.Value + ")");
            foreach (var p in AssetDb.Problems) Console.WriteLine("  data " + p);
            return 0;
        }

        /// <summary>Trace the field decode of one converted object: fields FILE PATHID [--data DIR].</summary>
        static int Fields(string[] a)
        {
            string data = a.Length > 3 && a[2] == "--data" ? a[3] : "work/GameData";
            Platform.SaveRoot = System.IO.Path.GetFullPath("work/managed/save");
            AssetDb.Init(System.IO.Path.GetFullPath(data));
            var f = AssetDb.File(a[0]);
            var co = f.Objects[long.Parse(a[1])];
            var raw = Tree.Get(co.Tree, "_raw") as byte[];
            Console.WriteLine($"{co.Type} {Tree.Str(co.Tree, "m_Name")} raw={raw?.Length} script={AssetDb.ScriptType(co)}");
            FieldDecoder.Trace = Console.WriteLine;
            var t = AssetDb.ScriptType(co);
            var inst = typeof(UnityEngine.ScriptableObject).IsAssignableFrom(t) || typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t)
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t) : null;
            FieldDecoder.Apply(inst, co, null);
            Console.WriteLine($"decoded {FieldDecoder.Decoded} mismatched {FieldDecoder.Mismatched}");
            return 0;
        }

        /// <summary>resource PATH [TYPE]: what Resources.Load answers, outside a run.</summary>
        static int Resource(string[] a)
        {
            Platform.SaveRoot = System.IO.Path.GetFullPath("work/managed/save");
            AssetDb.Init(System.IO.Path.GetFullPath("work/GameData"));
            var t = a.Length > 1 ? Find.TypeByName(a[1]) : typeof(UnityEngine.Object);
            var o = Icalls.UnityEngine_Resources__Load(a[0], t);
            Console.WriteLine($"Resources.Load(\"{a[0]}\", {t}) = {(o == null ? "null" : o.GetType().FullName + " '" + o.name + "'")}");
            foreach (var kv in Icall.Report()) Console.WriteLine("  missing " + kv.Key + " x" + kv.Value);
            foreach (var p in AssetDb.Problems) Console.WriteLine("  data " + p);
            return 0;
        }

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
