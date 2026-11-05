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
