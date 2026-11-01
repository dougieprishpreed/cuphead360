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

        public static string GetFolderPathOption(Environment.SpecialFolder folder, Environment.SpecialFolderOption option) => GetFolderPath(folder);

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
