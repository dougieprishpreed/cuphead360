using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Application, Debug logging, PlayerPrefs (port-local file, never the PC
    // registry), Screen and SystemInfo. The port presents itself as the GOG
    // Windows player, so the game takes its Windows paths.
    public static partial class Icalls
    {
        public static int TargetFrameRate = -1;
        public static bool QuitRequested;

        public static string UnityEngine_Application__get_persistentDataPath() => Platform.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string UnityEngine_Application__get_temporaryCachePath() => Platform.GetFolderPath(Environment.SpecialFolder.InternetCache);
        public static string UnityEngine_Application__get_dataPath() => AssetDb.Root;
        public static string UnityEngine_Application__get_streamingAssetsPath() => Path.Combine(AssetDb.Root ?? "", "StreamingAssets");
        public static bool UnityEngine_Application__get_isPlaying() => true;
        public static bool UnityEngine_Application__get_isEditor() => false;
        public static bool UnityEngine_Application__get_isFocused() => true;
        public static bool UnityEngine_Application__get_isBatchmode() => false;
        public static bool UnityEngine_Application__get_isLoadingLevel() => false;
        public static bool UnityEngine_Application__get_isWebPlayer() => false;
        public static bool UnityEngine_Application__get_runInBackground() => true;
        public static void UnityEngine_Application__set_runInBackground(bool value) { }
        public static RuntimePlatform UnityEngine_Application__get_platform() => RuntimePlatform.WindowsPlayer;
        public static string UnityEngine_Application__get_unityVersion() => "2017.4.9f1";
        public static string UnityEngine_Application__get_version() => Tree.Str(AssetDb.PlayerSettings, "bundleVersion");
        public static string UnityEngine_Application__get_productName() => Tree.Str(AssetDb.PlayerSettings, "productName");
        public static string UnityEngine_Application__get_companyName() => Tree.Str(AssetDb.PlayerSettings, "companyName");
        public static string UnityEngine_Application__get_identifier() => "";
        public static string UnityEngine_Application__get_cloudProjectId() => "";
        public static string UnityEngine_Application__get_buildGUID() => "";
        public static bool UnityEngine_Application__get_genuine() => true;
        public static bool UnityEngine_Application__get_genuineCheckAvailable() => false;
        public static SystemLanguage UnityEngine_Application__get_systemLanguage() => SystemLanguage.English;
        public static NetworkReachability UnityEngine_Application__get_internetReachability() => NetworkReachability.NotReachable;
        public static int UnityEngine_Application__get_targetFrameRate() => TargetFrameRate;
        public static void UnityEngine_Application__set_targetFrameRate(int value) => TargetFrameRate = value;
        public static void UnityEngine_Application__Quit() { QuitRequested = true; Log.Line("Application.Quit"); }
        public static void UnityEngine_Application__CancelQuit() => QuitRequested = false;
        public static bool UnityEngine_Application__HasProLicense() => true;
        public static void UnityEngine_Application__SetLogCallbackDefined(bool defined) { }
        public static StackTraceLogType UnityEngine_Application__get_stackTraceLogType() => StackTraceLogType.None;
        public static void UnityEngine_Application__set_stackTraceLogType(StackTraceLogType value) { }
        public static StackTraceLogType UnityEngine_Application__GetStackTraceLogType(LogType logType) => StackTraceLogType.None;
        public static void UnityEngine_Application__SetStackTraceLogType(LogType logType, StackTraceLogType stackTraceType) { }
        public static ThreadPriority UnityEngine_Application__get_backgroundLoadingPriority() => ThreadPriority.Normal;
        public static void UnityEngine_Application__set_backgroundLoadingPriority(ThreadPriority value) { }
        public static void UnityEngine_Application__OpenURL(string url) => Log.Line("Application.OpenURL ignored: " + url);
        public static bool UnityEngine_Application__get_submitAnalytics() => false;
        public static bool UnityEngine_Application__CanStreamedLevelBeLoaded(int levelIndex) => levelIndex >= 0 && levelIndex < AssetDb.BuildScenes.Length;
        public static bool UnityEngine_Application__CanStreamedLevelBeLoadedByName(string levelName) => SceneLoader.IndexOf(levelName) >= 0;
        public static float UnityEngine_Application__GetStreamProgressForLevel(int levelIndex) => 1f;
        public static float UnityEngine_Application__GetStreamProgressForLevelByName(string levelName) => 1f;
        public static void UnityEngine_Application__DontDestroyOnLoad(UObject mono) => UnityEngine_Object__DontDestroyOnLoad(mono);

        public static bool UnityEngine_Debug__get_isDebugBuild() => false;
        public static void UnityEngine_Debug__INTERNAL_CALL_DrawLine(ref Vector3 start, ref Vector3 end, ref Color color, float duration, bool depthTest) { }
        public static void UnityEngine_Debug__Break() { }
        public static void UnityEngine_Debug__ClearDeveloperConsole() { }
        public static bool UnityEngine_Debug__get_developerConsoleVisible() => false;
        public static void UnityEngine_Debug__set_developerConsoleVisible(bool value) { }
        public static int GameLogLines;
        public static void UnityEngine_DebugLogHandler__Internal_Log(LogType level, string msg, UObject obj)
        {
            GameLogLines++;
            Log.Line("UNITY " + level + ": " + msg);
        }
        public static void UnityEngine_DebugLogHandler__Internal_LogException(Exception exception, UObject obj)
        {
            Exceptions.Report(exception, Objects.Of(obj), "Debug.LogException");
        }

        // PlayerPrefs: a port-local file under the save root.
        static Dictionary<string, object> prefs;
        static string PrefsPath => Path.Combine(Platform.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "playerprefs.txt");
        static Dictionary<string, object> Prefs()
        {
            if (prefs != null) return prefs;
            prefs = new Dictionary<string, object>(StringComparer.Ordinal);
            if (File.Exists(PrefsPath))
                foreach (var line in File.ReadAllLines(PrefsPath, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 3) continue;
                    string key = Unescape(parts[1]), val = Unescape(parts[2]);
                    prefs[key] = parts[0] == "i" ? (object)int.Parse(val, CultureInfo.InvariantCulture)
                               : parts[0] == "f" ? float.Parse(val, CultureInfo.InvariantCulture) : val;
                }
            return prefs;
        }
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
            }
            return sb.ToString();
        }
        public static bool UnityEngine_PlayerPrefs__TrySetInt(string key, int value) { Prefs()[key] = value; return true; }
        public static bool UnityEngine_PlayerPrefs__TrySetFloat(string key, float value) { Prefs()[key] = value; return true; }
        public static bool UnityEngine_PlayerPrefs__TrySetSetString(string key, string value) { Prefs()[key] = value ?? ""; return true; }
        public static int UnityEngine_PlayerPrefs__GetInt(string key, int defaultValue) => Prefs().TryGetValue(key, out var v) && v is int i ? i : defaultValue;
        public static float UnityEngine_PlayerPrefs__GetFloat(string key, float defaultValue) => Prefs().TryGetValue(key, out var v) && v is float f ? f : defaultValue;
        public static string UnityEngine_PlayerPrefs__GetString(string key, string defaultValue) => Prefs().TryGetValue(key, out var v) && v is string s ? s : defaultValue;
        public static bool UnityEngine_PlayerPrefs__HasKey(string key) => Prefs().ContainsKey(key);
        public static void UnityEngine_PlayerPrefs__DeleteKey(string key) => Prefs().Remove(key);
        public static void UnityEngine_PlayerPrefs__DeleteAll() => Prefs().Clear();
        public static void UnityEngine_PlayerPrefs__Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in Prefs())
            {
                string t = kv.Value is int ? "i" : kv.Value is float ? "f" : "s";
                string v = kv.Value is float f ? f.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                sb.Append(t).Append('\t').Append(Escape(kv.Key)).Append('\t').Append(Escape(v)).Append('\n');
            }
            string tmp = PrefsPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            File.Move(tmp, PrefsPath, true);
        }

        // The console's output: 1280x720.
        public static int ScreenWidth = 1280, ScreenHeight = 720;
        public static int UnityEngine_Screen__get_width() => ScreenWidth;
        public static int UnityEngine_Screen__get_height() => ScreenHeight;
        public static float UnityEngine_Screen__get_dpi() => 96f;
        public static bool UnityEngine_Screen__get_fullScreen() => true;
        public static void UnityEngine_Screen__set_fullScreen(bool value) { }
        public static void UnityEngine_Screen__SetResolution(int width, int height, bool fullscreen, int preferredRefreshRate) { }
        public static Resolution[] UnityEngine_Screen__get_resolutions() => new[] { CurrentResolution() };
        public static void UnityEngine_Screen__INTERNAL_get_currentResolution(out Resolution value) => value = CurrentResolution();
        static Resolution CurrentResolution() { var r = new Resolution(); r.width = ScreenWidth; r.height = ScreenHeight; r.refreshRate = 60; return r; }
        public static void UnityEngine_Screen__get_safeArea_Injected(out Rect ret) => ret = new Rect(0, 0, ScreenWidth, ScreenHeight);
        public static int UnityEngine_Screen__get_sleepTimeout() => -1;
        public static void UnityEngine_Screen__set_sleepTimeout(int value) { }
        public static bool UnityEngine_Cursor__get_visible() => false;
        public static void UnityEngine_Cursor__set_visible(bool value) { }
        public static CursorLockMode UnityEngine_Cursor__get_lockState() => CursorLockMode.None;
        public static void UnityEngine_Cursor__set_lockState(CursorLockMode value) { }

        public static string UnityEngine_SystemInfo__get_operatingSystem() => "Windows 10  (10.0.0) 64bit";
        public static OperatingSystemFamily UnityEngine_SystemInfo__get_operatingSystemFamily() => OperatingSystemFamily.Windows;
        public static int UnityEngine_SystemInfo__get_systemMemorySize() => 512;
        public static int UnityEngine_SystemInfo__get_graphicsMemorySize() => 256;
        public static int UnityEngine_SystemInfo__get_processorCount() => 3;
        public static string UnityEngine_SystemInfo__get_deviceUniqueIdentifier() => "cuphead360";
        public static string UnityEngine_SystemInfo__get_deviceName() => "Xbox 360";
        public static string UnityEngine_SystemInfo__get_deviceModel() => "Xbox 360";
        public static DeviceType UnityEngine_SystemInfo__get_deviceType() => DeviceType.Desktop;
        public static int UnityEngine_SystemInfo__get_maxTextureSize() => 8192;
        public static bool UnityEngine_SystemInfo__get_supportsImageEffects() => true;
        public static bool UnityEngine_SystemInfo__get_supportsRenderTextures() => true;
        public static bool UnityEngine_SystemInfo__SupportsRenderTextureFormat(RenderTextureFormat format) => true;
        public static bool UnityEngine_SystemInfo__SupportsTextureFormat(TextureFormat format) => true;
        public static int UnityEngine_SystemInfo__get_graphicsShaderLevel() => 30;
        public static string UnityEngine_SystemInfo__get_graphicsDeviceName() => "Xenos";
    }
}
