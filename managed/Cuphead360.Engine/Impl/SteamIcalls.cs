using System;
using System.IO;
using Steamworks;

namespace Cuphead360.Engine
{
    // Steamworks.NET boundary (Assembly-CSharp-firstpass's Steamworks.NativeMethods, P/Invokes into CSteamworks /
    // steam_api64): a Steam that starts and has no user to sync with. SteamManager.Awake's SteamAPI.Init succeeds and
    // SteamClient() is non-null, so the game's Steam calls (InteropHelp.TestIfAvailableClient) don't throw; the DLC's
    // ISteamApps queries answer from the converted data (DLCManager.RefreshDLC enables The Delicious Last Course's
    // island, Ms. Chalice, its shop items and title screen when they say it is installed and downloaded); achievements
    // and stats are taken and kept nowhere (OnlineInterfaceSteam's unlocks and StoreStats). The saves go through
    // OnlineInterfaceSteam's own file code (the save folder), not through Steam. Every other Steamworks call stays a
    // generated stub, so a stray one shows in the missing ledger.
    public static partial class Icalls
    {
        /// <summary>Steam's app id of Cuphead - The Delicious Last Course (DLCManager.DLCAppID).</summary>
        const uint DlcAppId = 1117850u;
        static readonly IntPtr steamClient = new IntPtr(0x5157);   // any non-null handle: nothing dereferences it here
        static bool steamStarted;
        static int dlcInstalled = -1;

        /// <summary>The DLC's asset bundles (its map, world and shop atlases) are among the converted data.</summary>
        public static bool DlcInstalled
        {
            get
            {
                if (dlcInstalled < 0)
                {
                    string bundles = Path.Combine(AssetDb.Root ?? "", "objects", "bundles");
                    dlcInstalled = Directory.Exists(Path.Combine(bundles, "atlas_world_dlc")) && Directory.Exists(Path.Combine(bundles, "atlas_map_player_dlc")) ? 1 : 0;
                }
                return dlcInstalled == 1;
            }
        }

        public static bool Steamworks_NativeMethods__SteamAPI_RestartAppIfNecessary(AppId_t @unOwnAppID) => false;

        public static bool Steamworks_NativeMethods__SteamAPI_InitSafe()
        {
            if (!steamStarted)
                Log.Line("steam: local, no user; The Delicious Last Course " + (DlcInstalled ? "installed" : "not installed"));
            steamStarted = true;
            return true;
        }

        public static bool Steamworks_NativeMethods__SteamAPI_IsSteamRunning() => steamStarted;
        public static void Steamworks_NativeMethods__SteamAPI_Shutdown() => steamStarted = false;
        public static void Steamworks_NativeMethods__SteamAPI_RunCallbacks() { }
        public static IntPtr Steamworks_NativeMethods__SteamClient() => steamStarted ? steamClient : IntPtr.Zero;
        public static void Steamworks_NativeMethods__ISteamClient_SetWarningMessageHook(SteamAPIWarningMessageHook_t @pFunction) { }

        public static bool Steamworks_NativeMethods__ISteamApps_BIsDlcInstalled(AppId_t @appID) => @appID.m_AppId == DlcAppId && DlcInstalled;

        /// <summary>Not downloading: the converted DLC is all there.</summary>
        public static bool Steamworks_NativeMethods__ISteamApps_GetDlcDownloadProgress(AppId_t @nAppID, out ulong @punBytesDownloaded, out ulong @punBytesTotal)
        {
            @punBytesDownloaded = @punBytesTotal = 0;
            return false;
        }

        // The DLC store page (DLCManager.launchStore: only offered without the DLC): no overlay to open.
        public static void Steamworks_NativeMethods__ISteamFriends_ActivateGameOverlayToStore(AppId_t @nAppID, EOverlayToStoreFlag @eFlag) { }

        // Stats and achievements: accepted, kept nowhere (nothing is ever reported unlocked, so each unlock asks again).
        public static bool Steamworks_NativeMethods__ISteamUserStats_RequestCurrentStats() => true;
        public static bool Steamworks_NativeMethods__ISteamUserStats_StoreStats() => true;
        public static bool Steamworks_NativeMethods__ISteamUserStats_GetAchievement(InteropHelp.UTF8StringHandle @pchName, out bool @pbAchieved)
        {
            @pbAchieved = false;
            return true;
        }
        public static bool Steamworks_NativeMethods__ISteamUserStats_SetAchievement(InteropHelp.UTF8StringHandle @pchName) => true;
        public static bool Steamworks_NativeMethods__ISteamUserStats_GetStat(InteropHelp.UTF8StringHandle @pchName, out int @pData)
        {
            @pData = 0;
            return true;
        }
        public static bool Steamworks_NativeMethods__ISteamUserStats_SetStat(InteropHelp.UTF8StringHandle @pchName, int @nData) => true;
        public static bool Steamworks_NativeMethods__ISteamUserStats_SetStat_(InteropHelp.UTF8StringHandle @pchName, float @fData) => true;
        public static bool Steamworks_NativeMethods__ISteamUserStats_GetStat_(InteropHelp.UTF8StringHandle @pchName, out float @pData)
        {
            @pData = 0f;
            return true;
        }
    }
}
