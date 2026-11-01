// Cuphead360.Patcher - static edits to the player's own Cuphead assemblies,
// made once on their PC before the managed code is compiled ahead of time.
//
// Unity's managed assemblies are kept exactly as shipped except at the engine
// boundary: every method whose body lives in the native Unity player
// ([MethodImpl(InternalCall)]) or in a native library (P/Invoke) gets a body
// that calls Cuphead360.Engine.Icalls.<mangled name>. Everything above that
// boundary - Unity's own C# (Vector3, Mathf, MonoBehaviour, UI) and all game
// code - runs as the original IL.
//
//   publicize --in <dir> --out <dir>
//       copy every .dll; in the engine-side assemblies (UnityEngine*, Rewired*,
//       GalaxyCSharp) make types, fields and non-virtual methods public so the
//       engine can be written against them in C#.
//   gen --in <public dir> --impl <dir> --out <Icalls.g.cs> [--list <json>]
//       one default stub per engine method that <impl> does not implement yet.
//   redirect --in <public dir> --engine <Cuphead360.Engine.dll> --out <dir> [--report <json>]
//       write the patched set: engine method bodies call the engine.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cuphead360.Patcher;

internal static class Program
{
    const string EngineNamespace = "Cuphead360.Engine";
    const string IcallsType = "Icalls";

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: publicize|gen|redirect ..."); return 2; }
        var opt = new Dictionary<string, string>();
        for (int i = 1; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
        string Need(string k) => opt.TryGetValue(k, out var v) ? v : throw new ArgumentException("--" + k + " is required");
        try
        {
            switch (args[0])
            {
                case "publicize": return Publicize(Need("in"), Need("out"));
                case "gen": return Gen(Need("in"), Need("impl"), Need("out"), opt.GetValueOrDefault("list"));
                case "dump": return Dump(Need("in"), Need("type"), Need("method"));
                case "redirect": return Redirect(Need("in"), Need("engine"), Need("out"), opt.GetValueOrDefault("report"));
            }
        }
        catch (Exception e) { Console.Error.WriteLine("PATCHER FAILED: " + e); return 1; }
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
    }

    static bool EngineSide(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        return n.StartsWith("UnityEngine", StringComparison.Ordinal) || n.StartsWith("Rewired", StringComparison.Ordinal) || n == "GalaxyCSharp";
    }

    // Unity's own Mono class libraries are replaced by the .NET runtime's: the
    // game's references to mscorlib 2.0 / System 2.0 / System.Core 3.5 bind to
    // its facades.
    static readonly HashSet<string> MonoFramework = new(StringComparer.OrdinalIgnoreCase)
        { "mscorlib", "System", "System.Core", "System.Xml", "Mono.Security" };

    static IEnumerable<string> Dlls(string dir) =>
        Directory.GetFiles(dir, "*.dll")
            .Where(f => !MonoFramework.Contains(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);

    static ReaderParameters ReadParams(string dir)
    {
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(dir);
        return new ReaderParameters { AssemblyResolver = res, ReadingMode = ReadingMode.Immediate, InMemory = true };
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types)
            foreach (var x in WithNested(t)) yield return x;
    }

    static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in WithNested(n)) yield return x;
    }

    // ---------------------------------------------------------------- publicize

    static int Publicize(string inDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int types = 0, fields = 0, methods = 0;
        // Fields that were not public: Unity serializes those only with
        // [SerializeField], so the engine needs their original visibility.
        var widened = new List<string>();
        foreach (var f in Dlls(inDir))
        {
            string dst = Path.Combine(outDir, Path.GetFileName(f));
            if (!EngineSide(f)) { File.Copy(f, dst, true); continue; }
            using var asm = AssemblyDefinition.ReadAssembly(f, ReadParams(inDir));
            foreach (var t in AllTypes(asm.MainModule))
            {
                if (t.Name == "<Module>") continue;
                if (t.IsNested) { if (!t.IsNestedPublic) { t.IsNestedPublic = true; types++; } }
                else if (!t.IsPublic) { t.IsPublic = true; types++; }
                foreach (var fd in t.Fields)
                    if (!fd.IsPublic) { widened.Add(t.FullName.Replace('/', '+') + "::" + fd.Name); fd.IsPublic = true; fields++; }
                // Virtual methods keep their access: widening one could change
                // which overrides in other assemblies the runtime accepts.
                foreach (var md in t.Methods)
                    if (!md.IsPublic && !md.IsVirtual) { md.IsPublic = true; methods++; }
            }
            asm.Write(dst);
        }
        File.WriteAllLines(Path.Combine(outDir, "publicized_fields.txt"), widened);
        Console.WriteLine($"publicize: {types} types, {fields} fields, {methods} methods made public -> {outDir}");
        return 0;
    }

    // ---------------------------------------------------------------- engine methods

    sealed class EngineMethod
    {
        public MethodDefinition Method = null!;
        public string Assembly = "";
        public string Id = "";      // Type::Method, for logs
        public string Name = "";    // mangled C# name in Icalls
        public string Kind = "";    // icall | pinvoke
    }

    static bool IsEngineMethod(MethodDefinition m, out string kind)
    {
        kind = m.IsPInvokeImpl ? "pinvoke" : m.IsInternalCall ? "icall" : "";
        if (kind == "" && m.IsRuntime && !m.DeclaringType.IsDelegate()) kind = "icall";
        return kind != "" && !m.HasBody;
    }

    static string Mangle(string s) => Regex.Replace(s, @"[^A-Za-z0-9_]", "_");

    static List<EngineMethod> FindEngineMethods(string dir, List<AssemblyDefinition> keep)
    {
        var list = new List<EngineMethod>();
        foreach (var f in Dlls(dir))
        {
            var asm = AssemblyDefinition.ReadAssembly(f, ReadParams(dir));
            keep.Add(asm);
            var found = new List<EngineMethod>();
            foreach (var t in AllTypes(asm.MainModule))
                foreach (var m in t.Methods)
                    if (IsEngineMethod(m, out var kind))
                        found.Add(new EngineMethod
                        {
                            Method = m, Assembly = asm.Name.Name, Kind = kind,
                            Id = t.FullName + "::" + m.Name,
                            Name = Mangle(t.FullName) + "__" + Mangle(m.Name),
                        });
            list.AddRange(found);
        }
        // Overloads: number them in metadata order, so names stay stable for a
        // given set of assemblies.
        foreach (var g in list.GroupBy(e => e.Name).Where(g => g.Count() > 1))
        {
            int i = 0;
            foreach (var e in g) e.Name += "_" + (i++);
        }
        return list;
    }

    static bool IsDelegate(this TypeDefinition t) =>
        t.BaseType != null && (t.BaseType.FullName == "System.MulticastDelegate" || t.BaseType.FullName == "System.Delegate");

    // ---------------------------------------------------------------- gen

    static string CsType(TypeReference t)
    {
        switch (t)
        {
            case ArrayType a: return CsType(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]";
            case PointerType p: return CsType(p.ElementType) + "*";
            case ByReferenceType b: return CsType(b.ElementType);
            case RequiredModifierType r: return CsType(r.ElementType);
            case OptionalModifierType o: return CsType(o.ElementType);
            case GenericParameter g: return g.Name;
            case GenericInstanceType gi:
                {
                    string baseName = CsType(gi.ElementType);
                    int tick = baseName.LastIndexOf('`');
                    if (tick >= 0) baseName = baseName.Substring(0, tick);
                    return baseName + "<" + string.Join(", ", gi.GenericArguments.Select(CsType)) + ">";
                }
        }
        if (t.FullName == "System.Void") return "void";
        if (t.IsNested) return CsType(t.DeclaringType) + "." + StripTick(t.Name);
        return "global::" + (t.Namespace.Length > 0 ? t.Namespace + "." : "") + StripTick(t.Name);
    }

    static string StripTick(string n) { int i = n.IndexOf('`'); return i < 0 ? n : n.Substring(0, i); }

    static bool Unsupported(EngineMethod e, out string why)
    {
        why = "";
        if (e.Method.DeclaringType.HasGenericParameters) { why = "generic declaring type"; return true; }
        return false;
    }

    static string Ident(string n) => "@" + Regex.Replace(n.Length == 0 ? "arg" : n, @"[^A-Za-z0-9_]", "_");

    static (string sig, List<string> outs) Signature(EngineMethod e)
    {
        var m = e.Method;
        var ps = new List<string>();
        var outs = new List<string>();
        if (m.HasThis)
            ps.Add((m.DeclaringType.IsValueType ? "ref " : "") + CsType(m.DeclaringType) + " self");
        int i = 0;
        foreach (var p in m.Parameters)
        {
            string name = Ident(p.Name.Length == 0 ? "a" + i : p.Name);
            string mod = "";
            if (p.ParameterType is ByReferenceType)
            {
                mod = p.IsOut && !p.IsIn ? "out " : "ref ";
                if (mod == "out ") outs.Add(name);
            }
            ps.Add(mod + CsType(p.ParameterType) + " " + name);
            i++;
        }
        string gen = m.HasGenericParameters ? "<" + string.Join(", ", m.GenericParameters.Select(g => g.Name)) + ">" : "";
        return (CsType(m.ReturnType) + " " + e.Name + gen + "(" + string.Join(", ", ps) + ")", outs);
    }

    static int Gen(string inDir, string implDir, string outFile, string? listFile)
    {
        var keep = new List<AssemblyDefinition>();
        var all = FindEngineMethods(inDir, keep);
        // Names the hand-written engine already defines.
        var implemented = new HashSet<string>();
        if (Directory.Exists(implDir))
            foreach (var f in Directory.GetFiles(implDir, "*.cs", SearchOption.AllDirectories))
                foreach (Match mt in Regex.Matches(File.ReadAllText(f), @"\bstatic\s+[^;{=()]*?\b([A-Za-z0-9_]+__[A-Za-z0-9_]+)\s*(<[^>]*>)?\s*\("))
                    implemented.Add(mt.Groups[1].Value);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by Cuphead360.Patcher gen from the player's own assemblies.");
        sb.AppendLine("// One default stub per engine method not yet implemented under Impl/.");
        sb.AppendLine("// Do not edit: implement a method in Impl/ (same name) and rerun gen.");
        sb.AppendLine("#pragma warning disable CS0108, CS0162, CS0618, CS8981, CS0169, CS1591");
        sb.AppendLine("namespace " + EngineNamespace + "\n{");
        sb.AppendLine("    public static unsafe partial class " + IcallsType + "\n    {");
        int stubs = 0, skipped = 0;
        var listing = new List<object>();
        foreach (var e in all)
        {
            bool unsup = Unsupported(e, out var why);
            listing.Add(new { e.Assembly, e.Kind, e.Id, e.Name, implemented = implemented.Contains(e.Name), unsupported = unsup ? why : null });
            if (unsup) { skipped++; continue; }
            if (implemented.Contains(e.Name)) continue;
            var (sig, outs) = Signature(e);
            sb.Append("        public static ").Append(sig).Append(" { ");
            foreach (var o in outs) sb.Append(o).Append(" = default; ");
            sb.Append("Icall.Missing(\"").Append(e.Kind).Append(' ').Append(e.Id.Replace("\"", "\\\"")).Append("\"); ");
            if (e.Method.ReturnType.FullName != "System.Void") sb.Append("return default; ");
            sb.AppendLine("}");
            stubs++;
        }
        sb.AppendLine("    }\n}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, sb.ToString());
        if (listFile != null)
            File.WriteAllText(listFile, JsonSerializer.Serialize(listing, new JsonSerializerOptions { WriteIndented = true }));
        // The fields publicize widened, for the engine's serialization rules.
        var widenedFile = Path.Combine(inDir, "publicized_fields.txt");
        var pf = new StringBuilder();
        pf.AppendLine("// <auto-generated> by Cuphead360.Patcher gen: fields that were not public before publicize.");
        pf.AppendLine("namespace " + EngineNamespace + "\n{\n    public static partial class Publicized\n    {");
        pf.AppendLine("        public static readonly System.Collections.Generic.HashSet<string> Fields = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)\n        {");
        if (File.Exists(widenedFile))
            foreach (var line in File.ReadAllLines(widenedFile))
                pf.AppendLine("            \"" + line.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",");
        pf.AppendLine("        };\n    }\n}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outFile))!, "Publicized.g.cs"), pf.ToString());
        int impl = all.Count(e => implemented.Contains(e.Name));
        Console.WriteLine($"gen: {all.Count} engine methods ({all.Count(e => e.Kind == "icall")} icall, {all.Count(e => e.Kind == "pinvoke")} pinvoke): {impl} implemented, {stubs} stubs, {skipped} unsupported -> {outFile}");
        foreach (var a in keep) a.Dispose();
        return 0;
    }

    static int Dump(string file, string type, string method)
    {
        using var asm = AssemblyDefinition.ReadAssembly(file, ReadParams(Path.GetDirectoryName(Path.GetFullPath(file))!));
        foreach (var t in AllTypes(asm.MainModule).Where(t => t.FullName == type))
            foreach (var m in t.Methods.Where(m => m.Name == method))
            {
                Console.WriteLine($"{m.FullName} attrs={m.Attributes} impl={m.ImplAttributes} rva={m.RVA} hasBody={m.HasBody} sec={m.HasSecurityDeclarations} ca={string.Join(",", m.CustomAttributes.Select(c => c.AttributeType.Name))}");
                Console.WriteLine($"  return marshal={m.MethodReturnType.MarshalInfo?.NativeType}");
                foreach (var p in m.Parameters) Console.WriteLine($"  param {p.Name} {p.ParameterType} attrs={p.Attributes} marshal={p.MarshalInfo?.NativeType}");
                if (m.HasBody) foreach (var i in m.Body.Instructions) Console.WriteLine("  " + i);
            }
        return 0;
    }

    // ---------------------------------------------------------------- redirect

    static int Redirect(string inDir, string enginePath, string outDir, string? reportFile)
    {
        Directory.CreateDirectory(outDir);
        var keep = new List<AssemblyDefinition>();
        var all = FindEngineMethods(inDir, keep);
        using var engine = AssemblyDefinition.ReadAssembly(enginePath, ReadParams(Path.GetDirectoryName(Path.GetFullPath(enginePath))!));
        var icalls = engine.MainModule.GetType(EngineNamespace + "." + IcallsType)
            ?? throw new InvalidOperationException("no " + IcallsType + " in " + enginePath);
        var byName = icalls.Methods.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());

        int patched = 0;
        var missing = new List<string>();
        foreach (var e in all)
        {
            if (Unsupported(e, out var why)) { missing.Add(e.Id + " (" + why + ")"); continue; }
            if (!byName.TryGetValue(e.Name, out var target)) { missing.Add(e.Id + " (no " + e.Name + ")"); continue; }
            var m = e.Method;
            var mod = m.Module;
            MethodReference callee = mod.ImportReference(target);
            if (m.HasGenericParameters)
            {
                var gi = new GenericInstanceMethod(callee);
                foreach (var gp in m.GenericParameters) gi.GenericArguments.Add(gp);
                callee = gi;
            }
            // Cecil's PInvokeInfo setter sets the flag again: clear the info first.
            if (m.IsPInvokeImpl) { m.PInvokeInfo = null; m.IsPInvokeImpl = false; }
            m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            m.Body = new MethodBody(m);
            var il = m.Body.GetILProcessor();
            int argc = m.Parameters.Count + (m.HasThis ? 1 : 0);
            for (int i = 0; i < argc; i++) il.Append(il.Create(OpCodes.Ldarg, i < 0 ? null! : ArgAt(m, i)));
            il.Append(il.Create(OpCodes.Call, callee));
            il.Append(il.Create(OpCodes.Ret));
            patched++;
        }
        int replaced = ReplaceCalls(keep, engine);
        foreach (var asm in keep)
            asm.Write(Path.Combine(outDir, asm.MainModule.Name));
        foreach (var a in keep) a.Dispose();
        if (reportFile != null)
            File.WriteAllText(reportFile, JsonSerializer.Serialize(new { patched, missing }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"redirect: {patched} engine methods now call the engine, {missing.Count} left native -> {outDir}");
        foreach (var x in missing.Take(20)) Console.WriteLine("  left native: " + x);
        return missing.Count == 0 ? 0 : 3;
    }

    // Calls from game code into the BCL that would reach the PC's own folders
    // (the installed game's saves): they go to Cuphead360.Engine.Platform,
    // which answers with port-local folders.
    static readonly Dictionary<string, string> CallReplacements = new()
    {
        ["System.String System.Environment::GetFolderPath(System.Environment/SpecialFolder)"] = "GetFolderPath",
        ["System.String System.Environment::GetFolderPath(System.Environment/SpecialFolder,System.Environment/SpecialFolderOption)"] = "GetFolderPathOption",
    };

    static int ReplaceCalls(List<AssemblyDefinition> assemblies, AssemblyDefinition engine)
    {
        var platform = engine.MainModule.GetType(EngineNamespace + ".Platform")
            ?? throw new InvalidOperationException("no Platform type in the engine");
        int n = 0;
        foreach (var asm in assemblies)
            foreach (var t in AllTypes(asm.MainModule))
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if ((ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) || !(ins.Operand is MethodReference mr)) continue;
                        if (!CallReplacements.TryGetValue(mr.FullName, out var name)) continue;
                        var target = platform.Methods.First(x => x.Name == name);
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = m.Module.ImportReference(target);
                        n++;
                    }
                }
        Console.WriteLine($"redirect: {n} BCL calls to PC folders now call Cuphead360.Engine.Platform");
        return n;
    }

    // Ldarg with an index the ILProcessor understands: 'this' is the method
    // body's ThisParameter, the rest are ParameterDefinitions.
    static ParameterDefinition ArgAt(MethodDefinition m, int i)
    {
        if (m.HasThis) return i == 0 ? m.Body.ThisParameter : m.Parameters[i - 1];
        return m.Parameters[i];
    }
}
