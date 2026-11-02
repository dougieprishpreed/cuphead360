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
                case "run":
                    {
                        // Native input threads (Rewired) would keep the process alive.
                        int rc = Run(args.Skip(1).ToArray());
                        Console.Out.Flush();
                        Environment.Exit(rc);
                        return rc;
                    }
                case "play":
                    {
                        // The playable Windows build: Run's boot with a window, XInput and the keyboard.
                        int rc = Run(args.Skip(1).Concat(new[] { "--live", "1" }).ToArray());
                        Console.Out.Flush();
                        Environment.Exit(rc);
                        return rc;
                    }
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
            bool live = false, console = false;
            for (int i = 0; i + 1 < a.Length; i += 2)
                switch (a[i])
                {
                    case "--data": data = a[i + 1]; break;
                    case "--save": save = a[i + 1]; break;
                    case "--frames": frames = int.Parse(a[i + 1]); break;
                    case "--report": report = int.Parse(a[i + 1]); break;
                    case "--seed": seed = int.Parse(a[i + 1]); break;
                    case "--capture":
                        foreach (var f in a[i + 1].Split(',')) Capture.Frames.Add(int.Parse(f));
                        break;
                    case "--capture-every": Capture.Every = int.Parse(a[i + 1]); break;
                    case "--capture-out": Capture.Path = System.IO.Path.GetFullPath(a[i + 1]); break;
                    case "--live": live = true; break;
                    case "--console": console = a[i + 1] != "0"; break;
                    case "--shot": foreach (var f in a[i + 1].Split(',')) LiveHost.Shots.Add(int.Parse(f)); break;
                    case "--max-frames": LiveHost.MaxFrames = int.Parse(a[i + 1]); break;
                    case "--unlocked": LiveHost.Unlocked = a[i + 1] != "0"; break;
                    case "--hidden": LiveHost.Hidden = a[i + 1] != "0"; break;
                    case "--audio-out": audioOut = a[i + 1]; break;
                    case "--mute": LiveHost.Muted = a[i + 1] != "0"; break;
                    case "--inspect": inspect = a[i + 1]; break;
                    case "--dump-at":
                        foreach (var f in a[i + 1].Split(',')) dumpAt.Add(int.Parse(f));
                        break;
                    case "--bot":
                        {
                            var bot = Bot.Load(a[i + 1]);
                            Inputs.Source = bot.Step;
                            break;
                        }
                    case "--input":
                        {
                            var script = InputScript.Load(a[i + 1]);
                            Inputs.Source = script.At;
                            break;
                        }
                }
            if ((Capture.Frames.Count > 0 || Capture.Every > 0) && Capture.Path == null) Capture.Path = System.IO.Path.GetFullPath("work/preview/capture.jsonl");
            if (Capture.Path != null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Capture.Path));
                System.IO.File.WriteAllText(Capture.Path, "");
            }
            // Unity seeds Random from the clock; runs here are reproducible.
            Icalls.SeedRandom(seed);
            Platform.SaveRoot = System.IO.Path.GetFullPath(save);
            Platform.Guard(Platform.SaveRoot);
            Platform.AssemblyDumpDir = System.IO.Path.GetFullPath("work/managed/dumped");
            // Unity's dynamic fonts fall back to an OS font for characters no game font has.
            // (the console has no OS fonts: the staging step copies the PC's Arial into GameData/fonts)
            foreach (var arial in new[] { System.IO.Path.Combine(data, "fonts", "arial.ttf"),
                                          System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") })
                if (System.IO.File.Exists(arial)) { FontState.OsFallback = new TrueType(System.IO.File.ReadAllBytes(arial)); break; }
            AssetDb.Init(System.IO.Path.GetFullPath(data));
            if (audioOut != null) Audio.StartCapture(audioOut);
            Cuphead360.Engine.SceneLoader.Load(0, false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (console)
            {
                int rc = ConsoleHost.Play(f =>
                {
                    PlayerLoop.Frame(ConsoleHost.Delta);
                    return !Icalls.QuitRequested;
                });
                Audio.EndCapture();
                return rc;
            }
            if (live)
            {
                int rc = LiveHost.Play(f =>
                {
                    PlayerLoop.Frame(1f / 60f);
                    return !Icalls.QuitRequested;
                }, System.IO.Path.GetFullPath(data));
                Audio.EndCapture();
                return rc;
            }
            for (int f = 1; f <= frames && !Icalls.QuitRequested; f++)
            {
                PlayerLoop.Frame(1f / 60f);
                if (dumpAt.Contains(f)) DumpState(f);
                if (PlayerLoop.Profile && f % 600 == 0) Console.WriteLine($"frame {f} " + PlayerLoop.ProfileReport(600));
                if (report > 0 && f % report == 0)
                    Console.WriteLine($"FRAME {f} t={UnityEngine.Time.time:0.00} scene={Cuphead360.Engine.Scenes.Active?.Name} objects={Objects.LiveCount} exceptions={Exceptions.Count} missing={Icall.MissingCount}");
            }
            Audio.EndCapture();
            if (ChobObject.ParseStats != null)
                foreach (var kv in ChobObject.ParseStats.OrderByDescending(k => k.Value.bytes).Take(15))
                    Console.WriteLine($"PARSED {kv.Key}: {kv.Value.n} trees, {kv.Value.bytes >> 10} KB raw");
            Console.WriteLine($"RUN done: {frames} frames in {sw.Elapsed.TotalSeconds:0.0} s; scene loads {Cuphead360.Engine.SceneLoader.Loads}; active scene {Cuphead360.Engine.Scenes.Active?.Name}; "
                + $"exceptions {Exceptions.Count}; fields decoded {FieldDecoder.Decoded}, mismatched {FieldDecoder.Mismatched}; data problems {AssetDb.Problems.Count}; missing engine methods {Icall.MissingCount}");
            long peak = 0;
            try { peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20; } catch (Exception) { }
            Console.WriteLine($"MEMORY managed heap {GC.GetTotalMemory(false) >> 20} MB (after GC {GC.GetTotalMemory(true) >> 20} MB), process peak {peak} MB; audio voices {Audio.VoiceCount}; converted files read {ChobFile.LoadedFiles} ({ChobFile.LoadedBytes >> 20} MB)");
            try
            {
                Console.WriteLine($"INPUT Rewired ready={Rewired.ReInput.isReady}" + (Rewired.ReInput.isReady
                    ? $" joysticks={Rewired.ReInput.controllers.joystickCount} keyboard={Rewired.ReInput.controllers.Keyboard != null} players={Rewired.ReInput.players.playerCount}" : ""));
            }
            catch (Exception e) { Console.WriteLine("INPUT Rewired state: " + e.GetType().Name + ": " + e.Message); }
            foreach (var kv in Icall.Report()) Console.WriteLine("  missing " + kv.Key + " x" + kv.Value);
            Console.WriteLine($"PROPS: {Props.Count} property accessors served from the property bag (state only)");
            foreach (var kv in Props.Report()) Console.WriteLine("  props " + kv.Key + " (" + kv.Value + ")");
            foreach (var p in AssetDb.Problems) Console.WriteLine("  data " + p);
            return 0;
        }

        static readonly HashSet<int> dumpAt = new HashSet<int>();
        static string inspect;
        static string audioOut;

        /// <summary>Debug report: time scale and every live Animator's state.</summary>
        static void DumpState(int frame)
        {
            Console.WriteLine($"DUMP frame {frame} scene={Cuphead360.Engine.Scenes.Active?.Name} timeScale={UnityEngine.Time.timeScale} dt={UnityEngine.Time.deltaTime}");
            if (inspect != null)
            {
                foreach (var g in Typed.GameObjects)
                {
                    if (g.Name != inspect) continue;
                    Console.WriteLine($"  active={g.ActiveInHierarchy}");
                    string path = g.Name;
                    for (var t = g.Transform.Parent; t != null; t = t.Parent) path = t.GameObject.Name + "/" + path;
                    var rt = g.Transform as NRectTransform;
                    Console.WriteLine($"  object {path} pos={g.Transform.Position} local={g.Transform.LocalPosition}" + (rt != null ? $" anchorMin={rt.AnchorMin} anchorMax={rt.AnchorMax} pivot={rt.Pivot} sizeDelta={rt.SizeDelta} rect={rt.Rect}" : ""));
                    void Tree(NTransform t, string ind)
                    {
                        foreach (var ch in t.Children)
                        {
                            var cg = ch.GameObject; var crt = ch as NRectTransform;
                            var names = new List<string>();
                            foreach (var c in cg.Components) names.Add(c.Managed?.GetType().Name + (c is NBehaviour nb && !nb.Enabled ? "(off)" : ""));
                            Console.WriteLine($"{ind}{cg.Name}{(cg.ActiveSelf ? "" : " [inactive]")} pos={ch.Position} scale={ch.LocalScale}" + (crt != null ? $" aMin={crt.AnchorMin} aMax={crt.AnchorMax} pivot={crt.Pivot} size={crt.SizeDelta} anch={crt.AnchoredPosition}" : "") + " {" + string.Join(",", names) + "}");
                            foreach (var c in cg.Components)
                                if (c is NBehaviour nb2 && (nb2.Managed is UnityEngine.UI.Text || nb2.Managed is UnityEngine.UI.LayoutElement || nb2.Managed is UnityEngine.UI.Image))
                                    foreach (var fi in Serialization.Fields(nb2.Managed.GetType()))
                                    {
                                        object v = fi.GetValue(nb2.Managed);
                                        if (v is UnityEngine.UI.FontData fd) v = $"font={fd.font} size={fd.fontSize} style={fd.fontStyle} bestFit={fd.bestFit} min={fd.minSize} max={fd.maxSize} align={fd.alignment} alignByGeom={fd.alignByGeometry} rich={fd.richText} hOverflow={fd.horizontalOverflow} vOverflow={fd.verticalOverflow} lineSpacing={fd.lineSpacing}";
                                        string vs = v?.ToString() ?? "null";
                                        if (vs.Length > 200) vs = vs.Substring(0, 200);
                                        Console.WriteLine($"{ind}    .{fi.Name} = {vs}");
                                    }
                            Tree(ch, ind + "  ");
                        }
                    }
                    for (var at = g.Transform.Parent; at != null; at = at.Parent)
                        Console.WriteLine($"    ^ {at.GameObject.Name} local={at.LocalPosition} scale={at.LocalScale}" + (at is NRectTransform art ? $" aMin={art.AnchorMin} aMax={art.AnchorMax} pivot={art.Pivot} size={art.SizeDelta} anch={art.AnchoredPosition} rect={art.Rect}" : " (Transform)"));
                    Tree(g.Transform, "    | ");
                    foreach (var c in g.Components)
                    {
                        if (!(c is NBehaviour b)) { Console.WriteLine("    " + c.Managed.GetType().Name); continue; }
                        Console.WriteLine("    " + b.Managed.GetType().FullName + (b.Enabled ? "" : " (disabled)"));
                        foreach (var fi in Serialization.Fields(b.Managed.GetType()))
                        {
                            object v = fi.GetValue(b.Managed);
                            string vs = v is UnityEngine.RectOffset ro ? $"RectOffset(l={ro.left} r={ro.right} t={ro.top} b={ro.bottom})" : v?.ToString() ?? "null";
                            if (vs.Length > 120) vs = vs.Substring(0, 120);
                            Console.WriteLine($"      {fi.Name} = {vs}");
                        }
                    }
                }
                return;
            }
            foreach (var o in Objects.Live())
            {
                if (!(o is NAnimator an) || an.GameObject == null) continue;
                string path = an.GameObject.Name;
                for (var t = an.GameObject.Transform.Parent; t != null; t = t.Parent) path = t.GameObject.Name + "/" + path;
                string st = "-";
                if (an.Data != null && an.Layers.Length > 0)
                {
                    var l = an.Layers[0];
                    var s0 = an.StateAt(0, l.Current.State);
                    st = (s0 == null ? "?" : an.Data.NameOf(s0.PathId)) + $" t={l.Current.Time:0.00} n={an.Normalized(0, l.Current):0.00}" + (l.Next != null ? " ->" + an.Data.NameOf(an.StateAt(0, l.Next.State)?.PathId ?? 0) : "");
                }
                Console.WriteLine($"  animator {path} active={an.GameObject.ActiveInHierarchy} enabled={an.Enabled} mode={an.UpdateMode} speed={an.Speed} init={an.Initialized} state={st}");
            }
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
