using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    /// <summary>
    /// The playable Windows host: a Win32 window with an OpenGL context that
    /// draws the engine's render list every frame, XInput pads and the
    /// keyboard as input, and frames locked to the display's vsync at 60 Hz.
    /// Textures come from tools/convert_textures.py (GameData/textures),
    /// uploaded as stored (BC7/DXT stay compressed).
    /// </summary>
    public static unsafe class LiveHost
    {
        // ------------------------------------------------------------ Win32

        delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        [DllImport("user32", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32")] static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32")] static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);
        [DllImport("user32")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("user32")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("kernel32")] static extern IntPtr GetModuleHandleW(IntPtr name);
        [DllImport("gdi32")] static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32")] static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32")] static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32")] static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
        [DllImport("opengl32", CharSet = CharSet.Ansi)] static extern IntPtr wglGetProcAddress(string name);

        // ------------------------------------------------------------ GL 1.1

        [DllImport("opengl32")] static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32")] static extern void glScissor(int x, int y, int w, int h);
        [DllImport("opengl32")] static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32")] static extern void glClear(uint mask);
        [DllImport("opengl32")] static extern void glEnable(uint cap);
        [DllImport("opengl32")] static extern void glDisable(uint cap);
        [DllImport("opengl32")] static extern void glStencilFunc(uint func, int reference, uint mask);
        [DllImport("opengl32")] static extern void glStencilOp(uint sfail, uint dpfail, uint dppass);
        [DllImport("opengl32")] static extern void glStencilMask(uint mask);
        [DllImport("opengl32")] static extern void glColorMask(bool r, bool g, bool b, bool a);
        [DllImport("opengl32")] static extern void glClearStencil(int s);
        [DllImport("opengl32")] static extern void glBlendFunc(uint s, uint d);
        [DllImport("opengl32")] static extern void glGenTextures(int n, uint* t);
        [DllImport("opengl32")] static extern void glBindTexture(uint target, uint t);
        [DllImport("opengl32")] static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, void* data);
        [DllImport("opengl32")] static extern void glTexParameteri(uint target, uint name, int v);
        [DllImport("opengl32")] static extern void glPixelStorei(uint name, int v);
        [DllImport("opengl32")] static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport("opengl32")] static extern uint glGetError();
        [DllImport("opengl32")] static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, void* data);

        const uint GL_TEXTURE_2D = 0x0DE1, GL_BLEND = 0x0BE2, GL_SCISSOR_TEST = 0x0C11, GL_COLOR_BUFFER_BIT = 0x4000;
        const uint GL_STENCIL_TEST = 0x0B90, GL_STENCIL_BUFFER_BIT = 0x0400;
        // Unity StencilOp codes (Keep, Zero, Replace, IncrSat, DecrSat, Invert, IncrWrap, DecrWrap) as GL ops.
        static readonly uint[] glStencilOps = { 0x1E00, 0, 0x1E01, 0x1E02, 0x1E03, 0x150A, 0x8507, 0x8508 };
        const uint GL_SRC_ALPHA = 0x0302, GL_ONE_MINUS_SRC_ALPHA = 0x0303, GL_TRIANGLES = 4, GL_FLOAT = 0x1406, GL_UNSIGNED_BYTE = 0x1401;
        const uint GL_RGBA = 0x1908, GL_RGB = 0x1907, GL_ALPHA = 0x1906, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_LINEAR = 0x2601;
        const uint GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F, GL_UNPACK_ALIGNMENT = 0x0CF5;
        const uint GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30;

        // GL 2.0 / extensions through wglGetProcAddress.
        delegate uint CreateShaderFn(uint type);
        delegate void ShaderSourceFn(uint s, int n, string[] src, int[] len);
        delegate void CompileShaderFn(uint s);
        delegate uint CreateProgramFn();
        delegate void AttachShaderFn(uint p, uint s);
        delegate void LinkProgramFn(uint p);
        delegate void UseProgramFn(uint p);
        delegate int GetUniformLocationFn(uint p, string name);
        delegate void Uniform1iFn(int loc, int v);
        delegate void Uniform2fFn(int loc, float a, float b);
        delegate void Uniform4fFn(int loc, float a, float b, float c, float d);
        delegate void BindAttribLocationFn(uint p, uint index, string name);
        delegate void VertexAttribPointerFn(uint index, int size, uint type, bool norm, int stride, void* ptr);
        delegate void EnableVertexAttribArrayFn(uint index);
        delegate void CompressedTexImage2DFn(uint target, int level, uint fmt, int w, int h, int border, int size, void* data);
        delegate void GetShaderInfoLogFn(uint s, int max, out int len, byte* log);
        delegate bool SwapIntervalFn(int interval);
        delegate void GenFn(int n, out uint id);
        delegate void BindFn(uint target, uint id);
        delegate void RenderbufferStorageFn(uint target, uint fmt, int w, int h);
        delegate void FramebufferRenderbufferFn(uint target, uint attachment, uint rbTarget, uint rb);
        delegate uint CheckFramebufferStatusFn(uint target);
        delegate void FramebufferTexture2DFn(uint target, uint attachment, uint texTarget, uint tex, int level);
        delegate void BlendFuncSeparateFn(uint srcRgb, uint dstRgb, uint srcAlpha, uint dstAlpha);
        delegate void ActiveTextureFn(uint unit);
        delegate void BlendEquationSeparateFn(uint rgb, uint alpha);

        static CreateShaderFn glCreateShader; static ShaderSourceFn glShaderSource; static CompileShaderFn glCompileShader;
        static CreateProgramFn glCreateProgram; static AttachShaderFn glAttachShader; static LinkProgramFn glLinkProgram; static UseProgramFn glUseProgram;
        static GetUniformLocationFn glGetUniformLocation; static Uniform1iFn glUniform1i; static Uniform2fFn glUniform2f; static Uniform4fFn glUniform4f;
        static BindAttribLocationFn glBindAttribLocation; static VertexAttribPointerFn glVertexAttribPointer; static EnableVertexAttribArrayFn glEnableVertexAttribArray;
        static CompressedTexImage2DFn glCompressedTexImage2D; static GetShaderInfoLogFn glGetShaderInfoLog; static SwapIntervalFn wglSwapIntervalEXT;

        static T Fn<T>(string name) where T : Delegate
        {
            var p = wglGetProcAddress(name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("OpenGL function missing: " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        // ------------------------------------------------------------ XInput

        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }
        [DllImport("xinput1_4")] static extern uint XInputGetState(uint index, out XINPUT_STATE s);

        // ------------------------------------------------------------ state

        static double tLogic, tBuild, tDraw, tSwap, tWorst, lastReport;
        static int slow;
        static WndProcDelegate wndProc;
        static bool quit;
        static readonly HashSet<KeyCode> keys = new HashSet<KeyCode>();
        static readonly InputFrame input = new InputFrame();
        static uint program;
        static int uScreen, uMode, uTint, uTex, uTex2, uP0, uP1, uP2;
        static BindFn glBindFramebuffer; static GenFn glGenFramebuffers, glGenRenderbuffers; static BindFn glBindRenderbuffer;
        static RenderbufferStorageFn glRenderbufferStorage; static FramebufferRenderbufferFn glFramebufferRenderbuffer;
        static FramebufferTexture2DFn glFramebufferTexture2D; static BlendFuncSeparateFn glBlendFuncSeparate; static ActiveTextureFn glActiveTexture;
        static BlendEquationSeparateFn glBlendEquationSeparate;
        const uint GL_FRAMEBUFFER = 0x8D40, GL_RENDERBUFFER = 0x8D41, GL_ONE = 1;
        /// <summary>The framebuffer the screen's views draw into: the window's (0), or the offscreen one of a hidden run.</summary>
        static uint screenFb;
        /// <summary>Render textures by the engine's number: a framebuffer with a colour texture and depth/stencil.</summary>
        static readonly Dictionary<int, (uint fb, uint tex, int w, int h)> targets = new Dictionary<int, (uint, uint, int, int)>();
        static readonly float[] effectParams = new float[Effects.ParamCount];
        static readonly Dictionary<string, (uint id, int version)> textures = new Dictionary<string, (uint, int)>();
        static string textureDir;
        static readonly HashSet<string> missingTex = new HashSet<string>();

        /// <summary>Frames to save as PNG (framebuffer read back), for checking the live renderer.</summary>
        public static readonly HashSet<int> Shots = new HashSet<int>();
        public static string ShotDir = "work/live";
        /// <summary>Stop after this many frames (0 = until the window closes).</summary>
        public static int MaxFrames;
        /// <summary>No vsync: frames as fast as possible (test runs; game time still advances 1/60 s per frame).</summary>
        public static bool Unlocked;
        /// <summary>No window on screen: the same renderer draws into an offscreen framebuffer (scripted checks on a desktop someone is using).</summary>
        public static bool Hidden;
        /// <summary>No sound device (scripted checks); hidden runs are always silent.</summary>
        public static bool Muted;

        public static int Play(Func<int, bool> frame, string gameData, int width = 1280, int height = 720)
        {
            textureDir = Path.Combine(gameData, "textures");
            if (!Directory.Exists(textureDir) && !Directory.Exists(Path.Combine(gameData, "ctex")))
                Console.WriteLine("PLAY no converted textures: run tools/convert_textures.py --bundles (sprites draw untextured)");
            var hwnd = CreateWindow(width, height);
            var dc = GetDC(hwnd);
            InitGl(dc);
            if (Hidden) CreateOffscreen(width, height);
            if (!Hidden && !Muted) Sound.Start();
            var scripted = Inputs.Source;   // --bot / --input drive a live run when given
            if (scripted == null) Inputs.Source = f => input;
            if (!Hidden) Console.WriteLine("PLAY window open: XInput pads and the keyboard drive the game; close the window to quit.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            while (!quit)
            {
                while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref m);
                    DispatchMessageW(ref m);
                }
                if (quit) break;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                PollInput();
                if (!frame(++n)) break;
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                GetClientRect(hwnd, out var rc);
                if (Hidden) rc = new RECT { right = width, bottom = height };
                var views = RenderList.Build();
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                Draw(views, rc.right - rc.left, rc.bottom - rc.top);
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Shots.Contains(n)) Shot(n, rc.right - rc.left, rc.bottom - rc.top);
                if (MaxFrames > 0 && n >= MaxFrames) break;
                if (!Hidden) SwapBuffers(dc);   // vsync paces the loop (60 Hz)
                long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                tLogic += (t1 - t0) * f; tBuild += (t2 - t1) * f; tDraw += (t3 - t2) * f; tSwap += (t4 - t3) * f;
                double whole = (t4 - t0) * f;
                if (whole > tWorst) tWorst = whole;
                if (whole > 17.5) slow++;
                if (n % 600 == 0)
                {
                    double secs = clock.Elapsed.TotalSeconds - lastReport;
                    lastReport = clock.Elapsed.TotalSeconds;
                    Console.WriteLine($"PLAY frame {n} scene={Cuphead360.Engine.Scenes.Active?.Name} {600 / secs:0.0} fps; ms/frame logic={tLogic / 600:0.00} build={tBuild / 600:0.00} draw={tDraw / 600:0.00} swap={tSwap / 600:0.00}; worst {tWorst:0.0} ms, {slow} frames over 17.5 ms");
                    tLogic = tBuild = tDraw = tSwap = tWorst = 0; slow = 0;
                }
            }
            return 0;
        }

        /// <summary>
        /// The sound device: winmm waveOut, 48 kHz 16-bit stereo, fed from the
        /// engine's mixer. The game thread queues each mixed frame; a thread
        /// keeps four 20 ms device buffers full (silence on underrun; the queue
        /// is capped so a stall can't build up latency).
        /// </summary>
        static class Sound
        {
            [StructLayout(LayoutKind.Sequential)]
            struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
            [StructLayout(LayoutKind.Sequential)]
            struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
            [DllImport("winmm")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
            [DllImport("winmm")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
            [DllImport("winmm")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);

            const int Frames = 960, Buffers = 4;   // 20 ms each
            static readonly Queue<short> queue = new Queue<short>();
            static IntPtr dev;

            public static void Start()
            {
                var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Audio.Rate, nAvgBytesPerSec = Audio.Rate * 4, nBlockAlign = 4, wBitsPerSample = 16 };
                if (waveOutOpen(out dev, 0xFFFFFFFF /* WAVE_MAPPER */, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { Console.WriteLine("PLAY no sound device"); return; }
                Audio.Output = (mix, frames) =>
                {
                    lock (queue)
                    {
                        for (int i = 0; i < frames * 2; i++) queue.Enqueue((short)Math.Round(Math.Clamp(mix[i], -1f, 1f) * 32767f));
                        while (queue.Count > Audio.Rate / 4 * 2) queue.Dequeue();   // keep at most 250 ms queued
                    }
                };
                var t = new System.Threading.Thread(Pump) { IsBackground = true, Name = "Cuphead360 sound", Priority = System.Threading.ThreadPriority.AboveNormal };
                t.Start();
                Console.WriteLine("PLAY sound: waveOut 48 kHz stereo");
            }

            static void Pump()
            {
                int hsize = Marshal.SizeOf<WAVEHDR>();
                var hdrs = new IntPtr[Buffers];
                for (int i = 0; i < Buffers; i++)
                {
                    hdrs[i] = Marshal.AllocHGlobal(hsize);
                    var h = new WAVEHDR { lpData = Marshal.AllocHGlobal(Frames * 4), dwBufferLength = Frames * 4, dwFlags = 1 /* WHDR_DONE: free to fill */ };
                    Marshal.StructureToPtr(h, hdrs[i], false);
                }
                var block = new short[Frames * 2];
                bool[] prepared = new bool[Buffers];
                while (true)
                {
                    bool wrote = false;
                    for (int i = 0; i < Buffers; i++)
                    {
                        var h = Marshal.PtrToStructure<WAVEHDR>(hdrs[i]);
                        if ((h.dwFlags & 1) == 0) continue;   // still playing
                        lock (queue)
                            for (int k = 0; k < block.Length; k++) block[k] = queue.Count > 0 ? queue.Dequeue() : (short)0;
                        Marshal.Copy(block, 0, h.lpData, block.Length);
                        h.dwFlags &= ~1u;
                        Marshal.StructureToPtr(h, hdrs[i], false);
                        if (!prepared[i]) { waveOutPrepareHeader(dev, hdrs[i], hsize); prepared[i] = true; }
                        waveOutWrite(dev, hdrs[i], hsize);
                        wrote = true;
                    }
                    if (!wrote) System.Threading.Thread.Sleep(2);
                }
            }
        }

        /// <summary>An offscreen colour + depth/stencil framebuffer the size of the game screen (hidden runs).</summary>
        static void CreateOffscreen(int w, int h)
        {
            const uint GL_FRAMEBUFFER = 0x8D40, GL_RENDERBUFFER = 0x8D41;
            var genFb = Fn<GenFn>("glGenFramebuffers"); var bindFb = Fn<BindFn>("glBindFramebuffer");
            var genRb = Fn<GenFn>("glGenRenderbuffers"); var bindRb = Fn<BindFn>("glBindRenderbuffer");
            var storage = Fn<RenderbufferStorageFn>("glRenderbufferStorage"); var attach = Fn<FramebufferRenderbufferFn>("glFramebufferRenderbuffer");
            var status = Fn<CheckFramebufferStatusFn>("glCheckFramebufferStatus");
            genFb(1, out uint fb); bindFb(GL_FRAMEBUFFER, fb);
            screenFb = fb;
            genRb(1, out uint color); bindRb(GL_RENDERBUFFER, color); storage(GL_RENDERBUFFER, 0x8058 /* RGBA8 */, w, h);
            attach(GL_FRAMEBUFFER, 0x8CE0 /* COLOR_ATTACHMENT0 */, GL_RENDERBUFFER, color);
            genRb(1, out uint ds); bindRb(GL_RENDERBUFFER, ds); storage(GL_RENDERBUFFER, 0x88F0 /* DEPTH24_STENCIL8 */, w, h);
            attach(GL_FRAMEBUFFER, 0x821A /* DEPTH_STENCIL_ATTACHMENT */, GL_RENDERBUFFER, ds);
            uint st = status(GL_FRAMEBUFFER);
            Console.WriteLine(st == 0x8CD5 ? $"PLAY hidden: drawing offscreen at {w}x{h}" : $"PLAY hidden: framebuffer incomplete (0x{st:X})");
        }

        static void Shot(int n, int w, int h)
        {
            if (w <= 0 || h <= 0) { Console.WriteLine($"PLAY shot {n} skipped: the window has no drawable area (minimized)"); return; }
            var px = new byte[w * h * 4];
            fixed (byte* p = px) glReadPixels(0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, p);
            var flipped = new byte[px.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
            for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
            Directory.CreateDirectory(ShotDir);
            string fn = Path.Combine(ShotDir, $"shot_{n:00000}.png");
            Png.Write(fn, flipped, w, h);
            Console.WriteLine("PLAY shot " + fn);
        }

        static IntPtr CreateWindow(int w, int h)
        {
            wndProc = WndProc;
            var inst = GetModuleHandleW(IntPtr.Zero);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), style = 0x20 /* CS_OWNDC */, lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = inst, hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512), lpszClassName = "Cuphead360",
            };
            RegisterClassExW(ref wc);
            const uint style = 0x00CF0000;   // WS_OVERLAPPEDWINDOW
            var r = new RECT { right = w, bottom = h };
            AdjustWindowRect(ref r, style, false);
            var hwnd = CreateWindowExW(0, "Cuphead360", "Cuphead360", style | (Hidden ? 0u : 0x10000000u) /* WS_VISIBLE */, 100, 100, r.right - r.left, r.bottom - r.top, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (Hidden) ShowWindow(hwnd, 0);   // SW_HIDE
            else ShowWindow(hwnd, 5);
            return hwnd;
        }

        static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case 0x0010: case 0x0002: quit = true; return IntPtr.Zero;   // WM_CLOSE, WM_DESTROY
                case 0x0100: case 0x0104: { var k = KeyFromVk((int)wParam); if (k != KeyCode.None) keys.Add(k); return IntPtr.Zero; }
                case 0x0101: case 0x0105: { var k = KeyFromVk((int)wParam); if (k != KeyCode.None) keys.Remove(k); return IntPtr.Zero; }
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        static KeyCode KeyFromVk(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return KeyCode.A + (vk - 0x41);
            if (vk >= 0x30 && vk <= 0x39) return KeyCode.Alpha0 + (vk - 0x30);
            if (vk >= 0x70 && vk <= 0x7B) return KeyCode.F1 + (vk - 0x70);
            switch (vk)
            {
                case 0x25: return KeyCode.LeftArrow; case 0x26: return KeyCode.UpArrow; case 0x27: return KeyCode.RightArrow; case 0x28: return KeyCode.DownArrow;
                case 0x20: return KeyCode.Space; case 0x0D: return KeyCode.Return; case 0x1B: return KeyCode.Escape; case 0x09: return KeyCode.Tab; case 0x08: return KeyCode.Backspace;
                case 0x10: case 0xA0: return KeyCode.LeftShift; case 0xA1: return KeyCode.RightShift;
                case 0x11: case 0xA2: return KeyCode.LeftControl; case 0xA3: return KeyCode.RightControl;
                case 0x12: case 0xA4: return KeyCode.LeftAlt; case 0xA5: return KeyCode.RightAlt;
                case 0xBC: return KeyCode.Comma; case 0xBE: return KeyCode.Period; case 0xBF: return KeyCode.Slash; case 0xBA: return KeyCode.Semicolon;
                case 0xDE: return KeyCode.Quote; case 0xDB: return KeyCode.LeftBracket; case 0xDD: return KeyCode.RightBracket; case 0xBD: return KeyCode.Minus; case 0xBB: return KeyCode.Equals;
            }
            return KeyCode.None;
        }

        static void PollInput()
        {
            input.Keys.Clear();
            input.Keys.UnionWith(keys);
            for (uint i = 0; i < 4; i++)
            {
                var p = input.Pads[i];
                if (XInputGetState(i, out var s) != 0) { p.Connected = false; p.Buttons = 0; continue; }
                p.Connected = true;
                p.Buttons = (PadButton)s.buttons;
                p.LX = Stick(s.lx, 7849); p.LY = Stick(s.ly, 7849); p.RX = Stick(s.rx, 8689); p.RY = Stick(s.ry, 8689);
                p.LT = s.lt > 30 ? s.lt / 255f : 0; p.RT = s.rt > 30 ? s.rt / 255f : 0;
            }
        }

        static float Stick(short v, int dead)
        {
            if (Math.Abs((int)v) < dead) return 0;
            return Math.Max(-1f, v / 32767f);
        }

        // ------------------------------------------------------------ GL setup

        const string VS = @"#version 120
attribute vec2 aPos; attribute vec2 aUv; attribute vec4 aCol;
uniform vec2 uScreen;
varying vec2 vUv; varying vec4 vCol;
void main() { vUv = aUv; vCol = aCol; gl_Position = vec4(aPos / uScreen * 2.0 - 1.0, 0.0, 1.0); }";

        // Modes: 0 sprite/UI (tex*col), 1 screen blend, 2 multiply, 3 overlay, 4 alpha-only texture (fonts), 5 SDF text, 6 untextured,
        // 7 sprite mask; the effects as Effects.cs numbers them (8-10 premultiplied, 11-18 blits), as the console's shaders.
        const string FS = @"#version 120
uniform sampler2D uTex; uniform sampler2D uTex2; uniform int uMode; uniform vec4 uTint; uniform vec4 uP0; uniform vec4 uP1; uniform vec4 uP2;
varying vec2 vUv; varying vec4 vCol;
float outside(vec2 uv) { return 1.0 - step(0.1, texture2D(uTex, uv).a); }
vec3 blur7(vec2 d) {
  return texture2D(uTex, vUv).rgb * 0.324 + (texture2D(uTex, vUv - d).rgb + texture2D(uTex, vUv + d).rgb) * 0.232
       + (texture2D(uTex, vUv - 2.0 * d).rgb + texture2D(uTex, vUv + 2.0 * d).rgb) * 0.0855
       + (texture2D(uTex, vUv - 3.0 * d).rgb + texture2D(uTex, vUv + 3.0 * d).rgb) * 0.0205;
}
vec4 blur7s(vec2 d, vec4 c, float t) {
  vec4 b = (texture2D(uTex, vUv) * 0.324 + (texture2D(uTex, vUv - d) + texture2D(uTex, vUv + d)) * 0.232
       + (texture2D(uTex, vUv - 2.0 * d) + texture2D(uTex, vUv + 2.0 * d)) * 0.0855
       + (texture2D(uTex, vUv - 3.0 * d) + texture2D(uTex, vUv + 3.0 * d)) * 0.0205) * c;
  float low = step(b.r, uP0.z) * step(b.g, uP0.z) * step(b.b, uP0.z);
  float a = b.a * (1.0 - uP0.w * low) * t;
  return vec4(b.rgb * a, a);
}
vec3 powc(vec3 x, float g) { return exp2(log2(max(x, vec3(1e-6))) * g); }
void main() {
  vec4 t = texture2D(uTex, vUv);
  vec4 c = vCol * uTint;
  if (uMode == 8) {
    vec2 d = vec2(clamp(vUv.x * 4.0 - 2.0, -0.5, 0.5) * uP2.x * uP1.z, uP2.y * uP1.w);
    float n = outside(vUv + d) + outside(vUv + 2.0 * d) + outside(vUv + 3.0 * d) + outside(vUv + 4.0 * d) + outside(vUv + 5.0 * d);
    float k = 1.0 - clamp(n * uP1.y, 0.0, uP0.a);
    gl_FragColor = mix(t, vec4(uP0.rgb * (1.0 - k), 1.0 + k * (uP1.x - 1.0)), step(0.1, t.a));
  }
  else if (uMode == 9) { gl_FragColor = vec4(0.0, 0.0, 0.0, t.r * uP0.x); }
  else if (uMode == 10) {
    vec2 tx = uP2.xy;
    float hl = step(1e-20, uP0.y);
    float k = hl * clamp(uP0.x / max(uP0.y, 1e-20), 0.0, 1.0) + (1.0 - hl) * step(1e-20, uP0.x);
    vec4 avg = (texture2D(uTex, vUv + tx) + texture2D(uTex, vUv - 0.5 * tx) + texture2D(uTex, vUv + vec2(0.5, -0.5) * tx) + texture2D(uTex, vUv + vec2(-0.5, 0.5) * tx)) * 0.25 * c;
    vec4 r = mix(t, avg, k);
    vec4 acc = vec4(r.rgb * r.a, r.a);
    vec4 v = blur7s(vec2(0.0, tx.y * uP0.x), c, k), h = blur7s(vec2(tx.x * uP0.x, 0.0), c, k);
    acc = v + (1.0 - v.a) * acc; acc = h + (1.0 - h.a) * acc; acc = h + (1.0 - h.a) * acc; acc = v + (1.0 - v.a) * acc;
    gl_FragColor = acc;
  }
  else if (uMode == 11) { gl_FragColor = t; }
  else if (uMode == 12) {
    vec2 tx = uP2.xy;
    gl_FragColor = (texture2D(uTex, vUv + tx) + texture2D(uTex, vUv - 0.5 * tx) + texture2D(uTex, vUv + vec2(0.5, -0.5) * tx) + texture2D(uTex, vUv + vec2(-0.5, 0.5) * tx)) * 0.25;
  }
  else if (uMode == 13) { gl_FragColor = vec4(blur7(uP0.xy), t.a); }
  else if (uMode == 14) { gl_FragColor = vec4(powc(blur7(uP0.xy), uP0.z), t.a); }
  else if (uMode == 15) {
    vec3 b = blur7(uP0.xy); float gb = (b.g + b.b) * 0.5;
    float g = (1.0 - 0.67 * (1.0 - gb)) * (1.0 - 0.34 * (1.0 - b.r));
    gl_FragColor = vec4(powc(vec3(b.r, g, gb) * 1.25 - 0.25, uP0.z), t.a);
  }
  else if (uMode == 16) {
    vec3 cc = powc(blur7(uP0.xy), uP0.z);
    float x = max(dot(cc, vec3(0.2125, 0.7154, 0.0721)), dot(cc, vec3(0.333))) * 1.15 - 0.1;
    float y = exp2(log2(max(x, 1e-6)) * uP0.z * 0.9);
    gl_FragColor = vec4(y, y, y, t.a);
  }
  else if (uMode == 17) {
    float r = texture2D(uTex, vUv - uP0.xy).r, g = texture2D(uTex, vUv - uP0.zw).g, b = texture2D(uTex, vUv - uP1.xy).b;
    vec4 o = texture2D(uTex2, fract(vec2(dot(vUv, uP1.zw), dot(vUv, uP2.xy))));
    gl_FragColor = vec4(o.rgb * (vec3(r, g, b) + 1.0 - o.a), 1.0);
  }
  else if (uMode >= 40 && uMode <= 50) {
    // The passes of the game's blend and mask shaders (console/xsdk/shaders/am_*, stencilmask, blend*).
    vec4 tc = t * c; float a = t.a * c.a; float hi = step(0.5, t.r);
    vec3 hard = a * mix(t.rgb, 2.0 * t.rgb - 1.0, hi);
    if (uMode == 40) gl_FragColor = vec4(tc.rgb, 1.0 - tc.a);
    else if (uMode == 41) gl_FragColor = tc;
    else if (uMode == 42) gl_FragColor = vec4(a * a);
    else if (uMode == 43) gl_FragColor = vec4(a);
    else if (uMode == 44) gl_FragColor = vec4(0.0, 0.0, 0.0, c.a);
    else if (uMode == 45) gl_FragColor = vec4((t.rgb + c.rgb * c.a * uP0.x * (1.0 - t.rgb)) * t.a, t.a);
    else if (uMode == 46) { if (tc.a < 0.5) discard; gl_FragColor = tc; }
    else if (uMode == 47) gl_FragColor = vec4(mix(1.0 - a + a * c.rgb * (1.0 - t.rgb), 1.0 - a + hi * 2.0 * a * (1.0 - t.rgb), uP0.x), 1.0);
    else if (uMode == 48) gl_FragColor = vec4(max(mix(a * c.rgb * t.rgb, hard, uP0.x), vec3(0.0)), a);
    else if (uMode == 49) gl_FragColor = vec4(max(-hard, vec3(0.0)), 0.0);
    else gl_FragColor = vec4(tc.rgb * tc.a, tc.a);
  }
  else if (uMode == 18) {
    vec2 d = uP0.xy;
    gl_FragColor = t * 0.324 + (texture2D(uTex, vUv - d) + texture2D(uTex, vUv + d)) * 0.232
                 + (texture2D(uTex, vUv - 2.0 * d) + texture2D(uTex, vUv + 2.0 * d)) * 0.0855
                 + (texture2D(uTex, vUv - 3.0 * d) + texture2D(uTex, vUv + 3.0 * d)) * 0.0205;
  }
  else if (uMode == 1) { gl_FragColor = vec4(1.0 - (1.0 - t.rgb) * (1.0 - uTint.rgb), t.a * vCol.a); }
  else if (uMode == 2) { gl_FragColor = vec4(t.rgb * uTint.rgb, t.a * vCol.a); }
  else if (uMode == 3) { vec3 o = mix(2.0 * t.rgb * uTint.rgb, 1.0 - 2.0 * (1.0 - t.rgb) * (1.0 - uTint.rgb), step(0.5, t.rgb)); gl_FragColor = vec4(o, t.a * vCol.a); }
  else if (uMode == 4) { gl_FragColor = vec4(c.rgb, c.a * t.a); }
  else if (uMode == 5) { float a = smoothstep(0.45, 0.55, t.a); gl_FragColor = vec4(c.rgb, c.a * a); }
  else if (uMode == 6) { gl_FragColor = c; }
  else if (uMode == 7) { if (t.a < uTint.a) discard; gl_FragColor = vec4(0.0); }
  else { gl_FragColor = t * c; }
}";

        static void InitGl(IntPtr dc)
        {
            var pfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1, dwFlags = 0x25 /* draw to window | OpenGL | double buffer */, cColorBits = 32, cAlphaBits = 8, cStencilBits = 8 };
            SetPixelFormat(dc, ChoosePixelFormat(dc, ref pfd), ref pfd);
            wglMakeCurrent(dc, wglCreateContext(dc));
            glCreateShader = Fn<CreateShaderFn>("glCreateShader"); glShaderSource = Fn<ShaderSourceFn>("glShaderSource"); glCompileShader = Fn<CompileShaderFn>("glCompileShader");
            glCreateProgram = Fn<CreateProgramFn>("glCreateProgram"); glAttachShader = Fn<AttachShaderFn>("glAttachShader"); glLinkProgram = Fn<LinkProgramFn>("glLinkProgram");
            glUseProgram = Fn<UseProgramFn>("glUseProgram"); glGetUniformLocation = Fn<GetUniformLocationFn>("glGetUniformLocation"); glUniform1i = Fn<Uniform1iFn>("glUniform1i");
            glUniform2f = Fn<Uniform2fFn>("glUniform2f"); glUniform4f = Fn<Uniform4fFn>("glUniform4f"); glBindAttribLocation = Fn<BindAttribLocationFn>("glBindAttribLocation");
            glVertexAttribPointer = Fn<VertexAttribPointerFn>("glVertexAttribPointer"); glEnableVertexAttribArray = Fn<EnableVertexAttribArrayFn>("glEnableVertexAttribArray");
            glCompressedTexImage2D = Fn<CompressedTexImage2DFn>("glCompressedTexImage2D"); glGetShaderInfoLog = Fn<GetShaderInfoLogFn>("glGetShaderInfoLog");
            glBindFramebuffer = Fn<BindFn>("glBindFramebuffer"); glGenFramebuffers = Fn<GenFn>("glGenFramebuffers");
            glGenRenderbuffers = Fn<GenFn>("glGenRenderbuffers"); glBindRenderbuffer = Fn<BindFn>("glBindRenderbuffer");
            glRenderbufferStorage = Fn<RenderbufferStorageFn>("glRenderbufferStorage"); glFramebufferRenderbuffer = Fn<FramebufferRenderbufferFn>("glFramebufferRenderbuffer");
            glFramebufferTexture2D = Fn<FramebufferTexture2DFn>("glFramebufferTexture2D"); glBlendFuncSeparate = Fn<BlendFuncSeparateFn>("glBlendFuncSeparate");
            glActiveTexture = Fn<ActiveTextureFn>("glActiveTexture");
            glBlendEquationSeparate = Fn<BlendEquationSeparateFn>("glBlendEquationSeparate");
            try { wglSwapIntervalEXT = Fn<SwapIntervalFn>("wglSwapIntervalEXT"); wglSwapIntervalEXT(Unlocked ? 0 : 1); } catch (InvalidOperationException) { }
            uint vs = Compile(GL_VERTEX_SHADER, VS), fs = Compile(GL_FRAGMENT_SHADER, FS);
            program = glCreateProgram();
            glAttachShader(program, vs); glAttachShader(program, fs);
            glBindAttribLocation(program, 0, "aPos"); glBindAttribLocation(program, 1, "aUv"); glBindAttribLocation(program, 2, "aCol");
            glLinkProgram(program);
            glUseProgram(program);
            uScreen = glGetUniformLocation(program, "uScreen"); uMode = glGetUniformLocation(program, "uMode");
            uTint = glGetUniformLocation(program, "uTint"); uTex = glGetUniformLocation(program, "uTex");
            uTex2 = glGetUniformLocation(program, "uTex2"); uP0 = glGetUniformLocation(program, "uP0");
            uP1 = glGetUniformLocation(program, "uP1"); uP2 = glGetUniformLocation(program, "uP2");
            glUniform1i(uTex, 0);
            glUniform1i(uTex2, 1);
            glEnableVertexAttribArray(0); glEnableVertexAttribArray(1); glEnableVertexAttribArray(2);
            glEnable(GL_BLEND);
            // Straight colour over, alpha accumulated as the console's premultiplied blend does (render textures keep it).
            glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        }

        static uint Compile(uint type, string src)
        {
            uint s = glCreateShader(type);
            glShaderSource(s, 1, new[] { src }, null);
            glCompileShader(s);
            var log = new byte[2048];
            fixed (byte* l = log) { glGetShaderInfoLog(s, log.Length, out int len, l); if (len > 0) Console.WriteLine("PLAY shader log: " + System.Text.Encoding.ASCII.GetString(log, 0, len)); }
            return s;
        }

        // ------------------------------------------------------------ textures

        static uint white;

        static uint Texture(Texture t, out int mode)
        {
            mode = 0;
            if (t == null) return 0;
            if (t is RenderTexture rtex) return targets.TryGetValue(Effects.TargetId(rtex), out var rt) ? rt.tex : 0;
            var rp = RuntimePixels.Get(t);
            if (rp != null)
            {
                string key = "rt:" + (Objects.Of(t)?.InstanceId ?? 0);
                if (textures.TryGetValue(key, out var have) && have.version == rp.Version) { mode = rp.Channels == 1 ? 4 : 0; return have.id; }
                uint id = have.id;
                if (id == 0) { uint nid; glGenTextures(1, &nid); id = nid; }
                glBindTexture(GL_TEXTURE_2D, id);
                Params();
                // Run-time pixels are stored top row first; GL's first row is the bottom.
                int stride = rp.W * rp.Channels;
                var flipped = new byte[rp.Data.Length];
                for (int y = 0; y < rp.H; y++) Buffer.BlockCopy(rp.Data, y * stride, flipped, (rp.H - 1 - y) * stride, stride);
                fixed (byte* p = flipped)
                    glTexImage2D(GL_TEXTURE_2D, 0, rp.Channels == 1 ? (int)GL_ALPHA : (int)GL_RGBA, rp.W, rp.H, 0, rp.Channels == 1 ? GL_ALPHA : GL_RGBA, GL_UNSIGNED_BYTE, p);
                textures[key] = (id, rp.Version);
                mode = rp.Channels == 1 ? 4 : 0;
                return id;
            }
            var src = (Objects.Of(t) as NAsset)?.Source;
            if (src == null) return 0;
            string k = src.File.Path.Replace('/', '_');
            if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
            k += "_" + IdText.Of(src.PathId);
            if (textures.TryGetValue(k, out var e)) return e.id;
            uint tex = LoadTex(k);
            textures[k] = (tex, 0);
            return tex;
        }

        static void Params()
        {
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
        }

        static uint LoadCtx(string key)
        {
            string fn = Path.Combine(Path.GetDirectoryName(textureDir), "ctex", key + ".ctx");
            if (!File.Exists(fn)) return 0;
            var b = File.ReadAllBytes(fn);
            if (b.Length < 28 || BitConverter.ToUInt32(b, 0) != 0x31585443) return 0;
            int fmt = BitConverter.ToInt32(b, 4), w = BitConverter.ToInt32(b, 8), h = BitConverter.ToInt32(b, 12), size = BitConverter.ToInt32(b, 24);
            uint id; glGenTextures(1, &id);
            glBindTexture(GL_TEXTURE_2D, id);
            Params();
            fixed (byte* p = &b[28])
            {
                if (fmt == 1) glCompressedTexImage2D(GL_TEXTURE_2D, 0, 0x83F1, w, h, 0, size, p);        // DXT1
                else if (fmt == 2) glCompressedTexImage2D(GL_TEXTURE_2D, 0, 0x83F3, w, h, 0, size, p);   // DXT5
                else glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, 0x80E1 /* GL_BGRA */, GL_UNSIGNED_BYTE, p);
            }
            return id;
        }

        static uint LoadTex(string key)
        {
            string fn = Path.Combine(textureDir, key + ".tex");
            if (!File.Exists(fn))
            {
                // Without the full-size exports, the console's textures (tools/convert_console.py: pages halved).
                uint c = LoadCtx(key);
                if (c == 0 && missingTex.Add(key)) Console.WriteLine("PLAY texture not converted: " + key);
                return c;
            }
            var b = File.ReadAllBytes(fn);
            int fmt = BitConverter.ToInt32(b, 8), w = BitConverter.ToInt32(b, 12), h = BitConverter.ToInt32(b, 16), size = BitConverter.ToInt32(b, 24);
            uint id; glGenTextures(1, &id);
            glBindTexture(GL_TEXTURE_2D, id);
            Params();
            fixed (byte* p = &b[28])
            {
                switch (fmt)
                {
                    case 10: glCompressedTexImage2D(GL_TEXTURE_2D, 0, 0x83F1, w, h, 0, Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8, p); break;   // DXT1
                    case 12: glCompressedTexImage2D(GL_TEXTURE_2D, 0, 0x83F3, w, h, 0, Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16, p); break;  // DXT5
                    case 25: glCompressedTexImage2D(GL_TEXTURE_2D, 0, 0x8E8C, w, h, 0, Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16, p); break;  // BC7
                    case 4: glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, p); break;                                       // RGBA32
                    case 3: glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGB, w, h, 0, GL_RGB, GL_UNSIGNED_BYTE, p); break;                                          // RGB24
                    case 1: glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_ALPHA, w, h, 0, GL_ALPHA, GL_UNSIGNED_BYTE, p); break;                                      // Alpha8
                    case 5:                                                                                                                                    // ARGB32
                        {
                            var rgba = new byte[w * h * 4];
                            for (int i = 0; i < w * h; i++) { rgba[i * 4] = b[28 + i * 4 + 1]; rgba[i * 4 + 1] = b[28 + i * 4 + 2]; rgba[i * 4 + 2] = b[28 + i * 4 + 3]; rgba[i * 4 + 3] = b[28 + i * 4]; }
                            fixed (byte* q = rgba) glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, q);
                            break;
                        }
                    case 2:                                                                                                                                    // ARGB4444
                        {
                            var rgba = new byte[w * h * 4];
                            for (int i = 0; i < w * h; i++)
                            {
                                int v = b[28 + i * 2] | (b[28 + i * 2 + 1] << 8);
                                rgba[i * 4] = (byte)(((v >> 8) & 15) * 17); rgba[i * 4 + 1] = (byte)(((v >> 4) & 15) * 17);
                                rgba[i * 4 + 2] = (byte)((v & 15) * 17); rgba[i * 4 + 3] = (byte)(((v >> 12) & 15) * 17);
                            }
                            fixed (byte* q = rgba) glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, q);
                            break;
                        }
                    default:
                        Console.WriteLine($"PLAY texture format {fmt} not supported yet: {key}");
                        break;
                }
            }
            return id;
        }

        // ------------------------------------------------------------ drawing

        static float[] pos = new float[4096], uv = new float[4096];
        static byte[] col = new byte[8192];

        /// <summary>A render texture's framebuffer, made (or remade at a new size) on first use.</summary>
        static (uint fb, uint tex, int w, int h) Target(RenderTexture rt)
        {
            int id = Effects.TargetId(rt), w = Effects.Width(rt), h = Effects.Height(rt);
            if (targets.TryGetValue(id, out var have) && have.w == w && have.h == h) return have;
            glGenFramebuffers(1, out uint fb);
            glBindFramebuffer(GL_FRAMEBUFFER, fb);
            uint tex; glGenTextures(1, &tex);
            glBindTexture(GL_TEXTURE_2D, tex);
            Params();
            glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
            glFramebufferTexture2D(GL_FRAMEBUFFER, 0x8CE0 /* COLOR_ATTACHMENT0 */, GL_TEXTURE_2D, tex, 0);
            glGenRenderbuffers(1, out uint ds); glBindRenderbuffer(GL_RENDERBUFFER, ds); glRenderbufferStorage(GL_RENDERBUFFER, 0x88F0 /* DEPTH24_STENCIL8 */, w, h);
            glFramebufferRenderbuffer(GL_FRAMEBUFFER, 0x821A /* DEPTH_STENCIL_ATTACHMENT */, GL_RENDERBUFFER, ds);
            return targets[id] = (fb, tex, w, h);
        }

        static void Draw(List<RenderList.View> views, int ww, int wh)
        {
            glBindFramebuffer(GL_FRAMEBUFFER, screenFb);
            glViewport(0, 0, ww, wh);
            glDisable(GL_SCISSOR_TEST);
            glClearColor(0, 0, 0, 1);
            glClearStencil(0);
            glStencilMask(0xFF);
            glClear(GL_COLOR_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
            RenderTexture current = null;
            bool first = true;
            float sx = 1, sy = 1;
            foreach (var v in views)
            {
                // A render texture's run of views starts cleared (transparent); the screen keeps what it has.
                if (first || !ReferenceEquals(v.Target, current))
                {
                    current = v.Target;
                    glDisable(GL_SCISSOR_TEST);
                    if (current == null)
                    {
                        glBindFramebuffer(GL_FRAMEBUFFER, screenFb);
                        glViewport(0, 0, ww, wh);
                        sx = ww / (float)Icalls.ScreenWidth; sy = wh / (float)Icalls.ScreenHeight;
                        glUniform2f(uScreen, ww, wh);
                    }
                    else
                    {
                        var t = Target(current);
                        glBindFramebuffer(GL_FRAMEBUFFER, t.fb);
                        glViewport(0, 0, t.w, t.h);
                        sx = sy = 1;
                        glUniform2f(uScreen, t.w, t.h);
                        glClearColor(0, 0, 0, 0);
                        glStencilMask(0xFF);
                        glClear(GL_COLOR_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
                    }
                    glEnable(GL_SCISSOR_TEST);
                    first = false;
                }
                var r = v.PixelRect;
                glScissor((int)(r.x * sx), (int)(r.y * sy), (int)Math.Ceiling(r.width * sx), (int)Math.Ceiling(r.height * sy));
                if (v.Camera != null && (v.Camera.ClearFlags == CameraClearFlags.SolidColor || v.Camera.ClearFlags == CameraClearFlags.Skybox))
                {
                    var bg = v.Camera.Background;
                    glClearColor(bg.r, bg.g, bg.b, v.Target != null ? bg.a : 1);
                    glClear(GL_COLOR_BUFFER_BIT);
                }
                glStencilMask(0xFF);
                glClear(GL_STENCIL_BUFFER_BIT);   // each camera starts with a clear stencil (UGUI masks)
                foreach (var it in v.Items) DrawItem(it, sx, sy);
            }
            glDisable(GL_STENCIL_TEST);
            glColorMask(true, true, true, true);
        }

        // The game's blend and mask shaders' passes (console/xsdk/main.cpp, PASSES): program (uMode), blend
        // (GL factors, colour then alpha, and ops), stencil (0 none, 1 write the value, 2 draw where it is).
        static readonly (int prog, uint sf, uint df, uint sfa, uint dfa, bool revRgb, bool noColor, int stencil, int sref)[][] passes =
        {
            /* 20 */ new[] { (40, 0u, 1u, 1u, 0u, false, false, 0, 0) },
            /* 21 */ new[] { (40, 0u, 1u, 0x0305u, 1u, false, false, 0, 0), (41, 0x0305u, 0x0304u, 0u, 1u, false, false, 0, 0) },
            /* 22 */ new[] { (42, 0x0305u, 1u, 0u, 1u, true, false, 0, 0), (50, 0x0305u, 1u, 0u, 1u, false, false, 0, 0) },
            /* 23 */ new[] { (42, 0x0304u, 1u, 0u, 1u, true, false, 0, 0), (50, 0x0304u, 1u, 0u, 1u, false, false, 0, 0) },
            /* 24 */ new[] { (42, 0x0305u, 1u, 0u, 1u, true, false, 0, 0), (45, 0x0305u, 1u, 0u, 1u, false, false, 0, 0) },
            /* 25 */ new[] { (42, 0x0304u, 1u, 0u, 1u, true, false, 0, 0), (45, 0x0304u, 1u, 0u, 1u, false, false, 0, 0) },
            /* 26 */ new[] { (43, 0x0304u, 1u, 0u, 1u, true, false, 0, 0), (50, 0x0304u, 1u, 0u, 1u, false, false, 0, 0) },
            /* 27 */ new[] { (44, 0u, 1u, 1u, 0u, false, false, 0, 0) },
            /* 28 */ new[] { (46, 1u, 0x0303u, 1u, 0x0303u, false, true, 1, 5) },
            /* 29 */ new[] { (50, 1u, 0x0303u, 1u, 0x0303u, false, false, 2, 5) },
            /* 30 */ new (int, uint, uint, uint, uint, bool, bool, int, int)[0],
            /* 31 */ new (int, uint, uint, uint, uint, bool, bool, int, int)[0],
            /* 32 */ new (int, uint, uint, uint, uint, bool, bool, int, int)[0],
            /* 33 */ new[] { (47, 0u, 0x0300u, 0u, 1u, false, false, 0, 0), (48, 1u, 1u, 0x0302u, 0x0303u, false, false, 0, 0) },
            /* 34 */ new[] { (47, 0u, 0x0300u, 0u, 1u, false, false, 0, 0), (48, 1u, 1u, 0x0302u, 0x0303u, false, false, 0, 0),
                             (49, 1u, 1u, 0u, 1u, true, false, 0, 0) },
        };

        static void DrawPasses(RenderList.Item it, int mode, int n, uint tex)
        {
            var p = effectParams;
            Effects.Params(it, mode, p);
            glUniform4f(uP0, p[0], p[1], p[2], p[3]);
            glUniform4f(uTint, it.Tint.r, it.Tint.g, it.Tint.b, it.Tint.a);
            glBindTexture(GL_TEXTURE_2D, tex);
            fixed (float* pp = pos) fixed (float* up = uv) fixed (byte* cp = col)
            {
                glVertexAttribPointer(0, 2, GL_FLOAT, false, 0, pp);
                glVertexAttribPointer(1, 2, GL_FLOAT, false, 0, up);
                glVertexAttribPointer(2, 4, GL_UNSIGNED_BYTE, true, 0, cp);
                foreach (var ps in passes[mode - 20])
                {
                    glUniform1i(uMode, ps.prog);
                    glBlendFuncSeparate(ps.sf, ps.df, ps.sfa, ps.dfa);
                    glBlendEquationSeparate(ps.revRgb ? 0x800Bu /* FUNC_REVERSE_SUBTRACT */ : 0x8006u /* FUNC_ADD */, 0x8006u);
                    if (ps.noColor) glColorMask(false, false, false, false);
                    if (ps.stencil != 0)
                    {
                        glEnable(GL_STENCIL_TEST);
                        glStencilFunc(ps.stencil == 1 ? 0x0207u /* ALWAYS */ : 0x0202u /* EQUAL */, ps.sref, 0xFF);
                        glStencilOp(0x1E00, 0x1E00, ps.stencil == 1 ? 0x1E01u /* REPLACE */ : 0x1E00u);
                        glStencilMask(ps.stencil == 1 ? 0xFFu : 0u);
                    }
                    glDrawArrays(GL_TRIANGLES, 0, n);
                    if (ps.noColor) glColorMask(true, true, true, true);
                    if (ps.stencil != 0) { glDisable(GL_STENCIL_TEST); glStencilMask(0xFF); }
                }
            }
            glBlendEquationSeparate(0x8006u, 0x8006u);
            glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        }

        static void DrawItem(RenderList.Item it, float sx, float sy)
        {
            int n = it.Triangles.Length;
            if (n == 0) return;
            if (pos.Length < n * 2) { pos = new float[n * 4]; uv = new float[n * 4]; col = new byte[n * 8]; }
            uint tex = Texture(it.Texture, out int texMode);
            if (it.Texture != null && tex == 0) return;   // not loadable: skip rather than draw a white shape
            for (int i = 0; i < n; i++)
            {
                int k = it.Triangles[i];
                if (k < 0 || k >= it.Screen.Length) return;
                pos[i * 2] = it.Screen[k].x * sx; pos[i * 2 + 1] = it.Screen[k].y * sy;
                uv[i * 2] = it.Uv[k].x; uv[i * 2 + 1] = it.Uv[k].y;
                var c = it.Colors != null && k < it.Colors.Length ? it.Colors[k] : new Color32(255, 255, 255, 255);
                col[i * 4] = c.r; col[i * 4 + 1] = c.g; col[i * 4 + 2] = c.b; col[i * 4 + 3] = c.a;
            }
            int mode = Effects.ModeOf(it);
            if (mode >= 20 && mode <= 34) { DrawPasses(it, mode, n, tex); return; }
            if (mode == Effects.Sprite || mode == Effects.LightRay) mode = texMode;   // (the light ray drawn as a sprite here)
            if (it.Texture == null && mode < Effects.KnightGlow) mode = 6;
            if (mode >= Effects.KnightGlow)
            {
                var p = it.Effect >= 0 ? it.P : effectParams;
                if (it.Effect < 0) Effects.Params(it, mode, p);
                glUniform4f(uP0, p[0], p[1], p[2], p[3]); glUniform4f(uP1, p[4], p[5], p[6], p[7]); glUniform4f(uP2, p[8], p[9], p[10], p[11]);
                glActiveTexture(0x84C1);   // TEXTURE1: the film grain's overlay
                glBindTexture(GL_TEXTURE_2D, it.Tex2 != null ? Texture(it.Tex2, out _) : 0);
                glActiveTexture(0x84C0);
                if (Effects.IsBlit(mode)) glDisable(GL_BLEND);
                else glBlendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            }
            var tint = it.Tint;
            // UI graphics: the canvas alpha fades blend-mode materials too (Unity folds it into the vertex alpha).
            if (it.Kind == "ui" && mode >= 1 && mode <= 3)
                for (int i = 0; i < n; i++) col[i * 4 + 3] = (byte)(col[i * 4 + 3] * Mathf.Clamp01(tint.a));
            if (mode == 1 || mode == 2 || mode == 3) glUniform4f(uTint, tint.r, tint.g, tint.b, tint.a);
            else glUniform4f(uTint, tint.r, tint.g, tint.b, tint.a);
            glUniform1i(uMode, mode);
            glBindTexture(GL_TEXTURE_2D, tex);
            var st = it.Stencil;
            int ss = it.SpriteStencil;
            if (ss != 0)
            {
                // Sprite masks: add or take away one (no colour), or draw where the stencil is 1 or more / 0.
                glEnable(GL_STENCIL_TEST);
                if (ss == RenderList.Item.MaskAdd || ss == RenderList.Item.MaskRemove)
                {
                    glStencilFunc(0x0207u, 0, 0xFF);
                    glStencilOp(0x1E00, 0x1E00, ss == RenderList.Item.MaskAdd ? 0x1E02u : 0x1E03u);
                    glStencilMask(0xFF);
                    glColorMask(false, false, false, false);
                }
                else
                {
                    glStencilFunc(ss == RenderList.Item.MaskInside ? 0x0203u : 0x0204u, 1, 0xFF);   // LEQUAL / GREATER: 1 <= s, 1 > s
                    glStencilOp(0x1E00, 0x1E00, 0x1E00);
                    glStencilMask(0);
                }
            }
            else if (st != null)
            {
                // ref, comp (Unity CompareFunction: Never 1 .. Always 8), op, read, write, colorMask (RGBA bits 8,4,2,1).
                glEnable(GL_STENCIL_TEST);
                glStencilFunc(0x0200u + (uint)Math.Clamp(st[1] - 1, 0, 7), st[0], (uint)st[3]);
                uint op = glStencilOps[Math.Clamp(st[2], 0, 7)];
                glStencilOp(0x1E00, 0x1E00, op);
                glStencilMask((uint)st[4]);
                glColorMask((st[5] & 8) != 0, (st[5] & 4) != 0, (st[5] & 2) != 0, (st[5] & 1) != 0);
            }
            fixed (float* pp = pos) fixed (float* up = uv) fixed (byte* cp = col)
            {
                glVertexAttribPointer(0, 2, GL_FLOAT, false, 0, pp);
                glVertexAttribPointer(1, 2, GL_FLOAT, false, 0, up);
                glVertexAttribPointer(2, 4, GL_UNSIGNED_BYTE, true, 0, cp);
                glDrawArrays(GL_TRIANGLES, 0, n);
            }
            if (st != null || ss != 0) { glDisable(GL_STENCIL_TEST); glColorMask(true, true, true, true); glStencilMask(0xFF); }
            if (mode >= Effects.KnightGlow)
            {
                glEnable(GL_BLEND);
                glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            }
        }
    }
}
