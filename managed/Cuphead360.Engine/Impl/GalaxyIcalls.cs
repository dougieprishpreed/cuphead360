using System;
using System.IO;
using System.Runtime.InteropServices;
using Galaxy.Api;

namespace Cuphead360.Engine
{
    // GOG Galaxy SDK boundary (GalaxyCSharpGlue P/Invokes).
    //
    // A local Galaxy as the SDK behaves with no Galaxy client running: Init
    // succeeds, sign-in never completes (no auth event fires, SignedIn stays
    // false, so OnlineInterfaceGog's achievement and stat calls take their
    // signed-out path), and IApps.IsDlcInstalled answers from the installed
    // data. The Delicious Last Course is installed when its asset bundles
    // were converted from the player's game (the GOG DLC's files), which is
    // what DLCManager.RefreshDLC asks before it enables the DLC's island,
    // Ms. Chalice, its shop items and title screen. Methods only reachable
    // after a sign-in stay stubs so a stray call shows up in the missing
    // ledger.
    public static partial class Icalls
    {
        static GalaxyInstance.CustomExceptionHelper.CustomExceptionDelegate galaxyRaise;

        public static void Galaxy_Api_GalaxyInstance_CustomExceptionHelper__CustomExceptionRegisterCallback(
            GalaxyInstance.CustomExceptionHelper.CustomExceptionDelegate @customCallback)
        {
            galaxyRaise = @customCallback;
        }

        // InitParams owns a native block; give it a real one so its finalizer
        // has something to free.
        static IntPtr NewGalaxyParams() => Marshal.AllocHGlobal(16);

        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__new_InitParams__SWIG_0(string @jarg1, string @jarg2, string @jarg3, string @jarg4) => NewGalaxyParams();
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__new_InitParams__SWIG_1(string @jarg1, string @jarg2, string @jarg3) => NewGalaxyParams();
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__new_InitParams__SWIG_2(string @jarg1, string @jarg2) => NewGalaxyParams();

        public static void Galaxy_Api_GalaxyInstancePINVOKE__delete_InitParams(HandleRef @jarg1)
        {
            if (@jarg1.Handle != IntPtr.Zero) Marshal.FreeHGlobal(@jarg1.Handle);
        }

        // The glue's standard SWIG exception and string callbacks: this
        // boundary never raises those, so registering is all there is.
        public static void Galaxy_Api_GalaxyInstancePINVOKE_SWIGExceptionHelper__SWIGRegisterExceptionCallbacks_GalaxyInstance(
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate a, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate b,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate c, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate d,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate e, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate f,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate g, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate h,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate i, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate j,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionDelegate k) { }
        public static void Galaxy_Api_GalaxyInstancePINVOKE_SWIGExceptionHelper__SWIGRegisterExceptionCallbacksArgument_GalaxyInstance(
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionArgumentDelegate a, GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionArgumentDelegate b,
            GalaxyInstancePINVOKE.SWIGExceptionHelper.ExceptionArgumentDelegate c) { }
        public static void Galaxy_Api_GalaxyInstancePINVOKE_SWIGStringHelper__SWIGRegisterStringCallback_GalaxyInstance(
            GalaxyInstancePINVOKE.SWIGStringHelper.SWIGStringDelegate s) { }

        // Listener objects (OnlineInterfaceGog is an IAuthListener) get a
        // native block; their director callbacks are accepted, but no Galaxy
        // event ever fires.
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__new_IAuthListener() => NewGalaxyParams();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__delete_IAuthListener(HandleRef @jarg1) => Galaxy_Api_GalaxyInstancePINVOKE__delete_InitParams(@jarg1);
        public static void Galaxy_Api_GalaxyInstancePINVOKE__IAuthListener_director_connect(HandleRef @jarg1,
            IAuthListener.SwigDelegateIAuthListener_0 @delegate0, IAuthListener.SwigDelegateIAuthListener_1 @delegate1, IAuthListener.SwigDelegateIAuthListener_2 @delegate2) { }
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__IAuthListener_SWIGUpcast(IntPtr @jarg1) => @jarg1;
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__GalaxyTypeAwareListenerAuth_SWIGUpcast(IntPtr @jarg1) => @jarg1;
        public static uint Galaxy_Api_GalaxyInstancePINVOKE__GalaxyTypeAwareListenerAuth_GetListenerType() => (uint)ListenerType.AUTH;

        // The interfaces the game asks for: one block each, never freed (the
        // SDK's own interfaces live as long as the process).
        static readonly IntPtr galaxyApps = NewGalaxyParams(), galaxyUser = NewGalaxyParams(), galaxyRegistrar = NewGalaxyParams();
        static bool galaxyInitialized;

        static void GalaxyInit()
        {
            galaxyInitialized = true;
            Log.Line("galaxy: local, signed out; The Delicious Last Course " + (DlcInstalled ? "installed" : "not installed"));
        }

        public static void Galaxy_Api_GalaxyInstancePINVOKE__Init__SWIG_0(string @jarg1, string @jarg2) => GalaxyInit();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__Init__SWIG_1(HandleRef @jarg1) => GalaxyInit();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__InitLocal(string @jarg1, string @jarg2, string @jarg3) => GalaxyInit();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__ProcessData() { }
        public static void Galaxy_Api_GalaxyInstancePINVOKE__Shutdown() => galaxyInitialized = false;

        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__Apps() => galaxyInitialized ? galaxyApps : IntPtr.Zero;
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__User() => galaxyInitialized ? galaxyUser : IntPtr.Zero;
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__ListenerRegistrar() => galaxyInitialized ? galaxyRegistrar : IntPtr.Zero;
        public static void Galaxy_Api_GalaxyInstancePINVOKE__IListenerRegistrar_Register(HandleRef @jarg1, uint @jarg2, HandleRef @jarg3) { }
        public static void Galaxy_Api_GalaxyInstancePINVOKE__IListenerRegistrar_Unregister(HandleRef @jarg1, uint @jarg2, HandleRef @jarg3) { }

        // No Galaxy client to sign in through: the request never completes.
        public static void Galaxy_Api_GalaxyInstancePINVOKE__IUser_SignIn__SWIG_2(HandleRef @jarg1, bool @jarg2) { }
        public static bool Galaxy_Api_GalaxyInstancePINVOKE__IUser_SignedIn(HandleRef @jarg1) => false;

        /// <summary>The GOG product id of Cuphead - The Delicious Last Course.</summary>
        const ulong DlcProductId = 2048763853uL;
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

        public static bool Galaxy_Api_GalaxyInstancePINVOKE__IApps_IsDlcInstalled(HandleRef @jarg1, ulong @jarg2) => @jarg2 == DlcProductId && DlcInstalled;
    }
}
