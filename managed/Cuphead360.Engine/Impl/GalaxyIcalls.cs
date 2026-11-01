using System;
using System.Runtime.InteropServices;
using Galaxy.Api;

namespace Cuphead360.Engine
{
    // GOG Galaxy SDK boundary (GalaxyCSharpGlue P/Invokes).
    //
    // There is no Galaxy peer on this platform, so GalaxyInstance.Init reports a
    // RUNTIME_ERROR through the glue's own exception callback, exactly as the
    // native glue does when the peer library cannot be loaded. The original
    // GogGalaxyManager.Awake catches GalaxyInstance.Error, logs "Failed to
    // initialize GOG Galaxy" and leaves isInitialized false; every call in
    // OnlineInterfaceGog is guarded by IsInitialized, so achievements/stats
    // take the game's own "Galaxy unavailable" path. Nothing here reports
    // success. Methods only reachable after a successful Init stay stubs so a
    // stray call shows up in the missing ledger.
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

        static void GalaxyUnavailable()
        {
            const string why = "Galaxy peer is not available on this platform";
            if (galaxyRaise != null) galaxyRaise(IError.Type.RUNTIME_ERROR, why);
            else Icall.Missing("galaxy: init without a registered exception callback");
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

        // Listener objects (OnlineInterfaceGog is an IAuthListener) are
        // constructed even when Init failed; they get a native block; their
        // director callbacks are accepted, but no Galaxy event ever fires.
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__new_IAuthListener() => NewGalaxyParams();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__delete_IAuthListener(HandleRef @jarg1) => Galaxy_Api_GalaxyInstancePINVOKE__delete_InitParams(@jarg1);
        public static void Galaxy_Api_GalaxyInstancePINVOKE__IAuthListener_director_connect(HandleRef @jarg1,
            IAuthListener.SwigDelegateIAuthListener_0 @delegate0, IAuthListener.SwigDelegateIAuthListener_1 @delegate1, IAuthListener.SwigDelegateIAuthListener_2 @delegate2) { }
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__IAuthListener_SWIGUpcast(IntPtr @jarg1) => @jarg1;
        public static IntPtr Galaxy_Api_GalaxyInstancePINVOKE__GalaxyTypeAwareListenerAuth_SWIGUpcast(IntPtr @jarg1) => @jarg1;

        public static void Galaxy_Api_GalaxyInstancePINVOKE__Init__SWIG_0(string @jarg1, string @jarg2) => GalaxyUnavailable();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__Init__SWIG_1(HandleRef @jarg1) => GalaxyUnavailable();
        public static void Galaxy_Api_GalaxyInstancePINVOKE__InitLocal(string @jarg1, string @jarg2, string @jarg3) => GalaxyUnavailable();
    }
}
