using System;
using System.IO;

namespace Cuphead360.Engine
{
    /// <summary>
    /// The port's own folders. Game code that asks the BCL for the PC's
    /// special folders (the installed game keeps its saves under
    /// %APPDATA%\Cuphead) is patched to ask here instead, so the port never
    /// reads or writes the installed game's saves.
    /// </summary>
    public static class Platform
    {
        /// <summary>Root of the port's saves and settings; set by the host before the game starts.</summary>
        public static string SaveRoot;

        public static string GetFolderPath(Environment.SpecialFolder folder)
        {
            if (string.IsNullOrEmpty(SaveRoot)) throw new InvalidOperationException("Platform.SaveRoot is not set");
            string dir = Path.Combine(SaveRoot, folder.ToString());
            Directory.CreateDirectory(dir);
            Guard(dir);
            return dir;
        }

        /// <summary>Where Assembly.Load(byte[]) payloads are written the first time they are seen (build input).</summary>
        public static string AssemblyDumpDir;

        /// <summary>
        /// Assembly.Load(byte[]) from game code (Rewired's platform library,
        /// decrypted at run time). The patched copy built from an earlier dump
        /// is loaded by name; without one the payload is dumped for the build
        /// and loaded as is (its native calls are then unpatched).
        /// </summary>
        public static System.Reflection.Assembly LoadAssembly(byte[] raw)
        {
            Log.Line("PLATFORM Assembly.Load(byte[" + (raw?.Length ?? -1) + "])");
            string name;
            using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(raw)))
                name = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe).GetString(
                    System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe).GetAssemblyDefinition().Name);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name && !a.IsDynamic && a.Location.Length > 0) return a;
            // The patched copy ships next to the player (JIT) or is compiled in (AOT).
            string local = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(local)) return System.Reflection.Assembly.LoadFrom(local);
            try { return System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(name)); }
            catch (FileNotFoundException) { }
            if (AssemblyDumpDir != null)
            {
                Directory.CreateDirectory(AssemblyDumpDir);
                File.WriteAllBytes(Path.Combine(AssemblyDumpDir, name + ".dll"), raw);
                Log.Line("PLATFORM dumped " + name + " (" + raw.Length + " bytes) for the build; loaded unpatched this run");
            }
            return System.Reflection.Assembly.Load(raw);
        }

        public static string GetFolderPathOption(Environment.SpecialFolder folder, Environment.SpecialFolderOption option) => GetFolderPath(folder);

        /// <summary>
        /// The game's GC.Collect(): nothing. Unity's collector made it cheap; here a full collection
        /// of a ~100 MB heap pauses the console for about half a second, and the game asks for one each
        /// time the pause or equip menu opens and at every load's start (where AssetUnload's pass, a
        /// few frames later with the old scene gone, collects anyway).
        /// </summary>
        public static void GameCollect() { }

        // ---- the game's save (PlayerData.Save: at every death, win and map event) ----
        // On the console a death's frame took 150-270 ms (the forty-first run: update 80-140 of it): the save builds
        // an XmlSerializer for Dialoguer's variables each time (the type's mapping worked out by reflection, 43
        // floats written), then opens and writes its file on the drive, all in the frame the player dies in.

        // CH360_SAVE_PLAIN=1: the save as the game wrote it (a serializer each time, the file written in the frame): a PC
        // measure of what the rest saves.
        static readonly bool savePlain = Environment.GetEnvironmentVariable("CH360_SAVE_PLAIN") == "1";

        static readonly System.Collections.Generic.Dictionary<Type, System.Xml.Serialization.XmlSerializer> xmlSerializers =
            new System.Collections.Generic.Dictionary<Type, System.Xml.Serialization.XmlSerializer>();

        /// <summary>The game's new XmlSerializer(type): one per type, kept (they hold no state between calls).</summary>
        public static System.Xml.Serialization.XmlSerializer XmlSerializerFor(Type type)
        {
            if (savePlain) return new System.Xml.Serialization.XmlSerializer(type);
            if (!xmlSerializers.TryGetValue(type, out var s)) xmlSerializers[type] = s = new System.Xml.Serialization.XmlSerializer(type);
            return s;
        }

        static string dialoguerKey, dialoguerXml;
        static System.Reflection.FieldInfo dialoguerBools, dialoguerFloats, dialoguerStrings;
        static Type dialoguerType;
        static bool dialoguerKnown;

        /// <summary>
        /// The game's XmlSerializer.Serialize(TextWriter, object). Dialoguer's variables (what every save writes)
        /// come out as the text the serializer gave for the same values last time; anything else, and any change,
        /// goes through the serializer as before, so the text is always its own.
        /// </summary>
        public static void XmlSerialize(System.Xml.Serialization.XmlSerializer serializer, TextWriter writer, object o)
        {
            if (!savePlain && o != null && writer is StringWriter && o.GetType().FullName == "DialoguerCore.DialoguerGlobalVariables")
            {
                string key = DialoguerKey(o);
                if (key != null)
                {
                    if (key != dialoguerKey)
                    {
                        var sw = new StringWriter();
                        serializer.Serialize(sw, o);
                        dialoguerXml = sw.ToString();
                        dialoguerKey = key;
                    }
                    writer.Write(dialoguerXml);
                    return;
                }
            }
            serializer.Serialize(writer, o);
        }

        /// <summary>Every value of Dialoguer's variables in one string (null if the class is not the one known).</summary>
        static string DialoguerKey(object o)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var t = o.GetType();
            if (dialoguerType != t)
            {
                dialoguerType = t;
                dialoguerBools = t.GetField("booleans", all); dialoguerFloats = t.GetField("floats", all); dialoguerStrings = t.GetField("strings", all);
                dialoguerKnown = dialoguerBools != null && dialoguerFloats != null && dialoguerStrings != null && t.GetFields(all).Length == 3;
                dialoguerKey = null;
            }
            if (!dialoguerKnown) return null;
            var sb = new System.Text.StringBuilder(512);
            if (dialoguerBools.GetValue(o) is System.Collections.Generic.List<bool> bools) { foreach (bool b in bools) sb.Append(b ? '1' : '0'); sb.Append('|'); }
            else sb.Append("-|");
            if (dialoguerFloats.GetValue(o) is System.Collections.Generic.List<float> floats) { foreach (float v in floats) sb.Append(BitConverter.SingleToInt32Bits(v).ToString("x")).Append(','); sb.Append('|'); }
            else sb.Append("-|");
            if (dialoguerStrings.GetValue(o) is System.Collections.Generic.List<string> strings)
                foreach (string v in strings) { if (v == null) sb.Append("n,"); else sb.Append(v.Length).Append(':').Append(v).Append(','); }
            else sb.Append('-');
            return sb.ToString();
        }

        /// <summary>
        /// The host's way of writing a whole file without holding the game up (the console: its bytes copied and
        /// written by another thread; a read of the file waits for it). Null, or false from it (the host could not
        /// write the last one): written here, through the runtime's files as the game always did.
        /// </summary>
        public static Func<string, byte[], int, bool> WriteFileLater;

        /// <summary>
        /// The game's new StreamWriter(path) (its save files): the text gathers in memory and the whole file is
        /// written when the writer closes, by the host where it can (WriteFileLater).
        /// </summary>
        public static StreamWriter FileWriter(string path) => new StreamWriter(new WholeFileStream(path));

        sealed class WholeFileStream : MemoryStream
        {
            readonly string path;
            bool done;
            public WholeFileStream(string path) { this.path = path; }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !done)
                {
                    done = true;
                    if (WriteFileLater == null || savePlain || !WriteFileLater(path, GetBuffer(), (int)Length))
                        using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) f.Write(GetBuffer(), 0, (int)Length);
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>Refuses any port folder that is (or is inside) a real special folder of this PC.</summary>
        public static void Guard(string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                                                            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonApplicationData })
            {
                string real = Environment.GetFolderPath(f);
                if (string.IsNullOrEmpty(real)) continue;
                string r = Path.GetFullPath(real).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("port save folder " + dir + " is inside the PC's " + f + " folder");
            }
        }
    }
}
