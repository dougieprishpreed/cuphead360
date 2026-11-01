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
        static int uScreen, uMode, uTint, uTex;
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

        public static int Play(Func<int, bool> frame, string gameData, int width = 1280, int height = 720)
        {
            textureDir = Path.Combine(gameData, "textures");
            if (!Directory.Exists(textureDir)) Console.WriteLine("PLAY no converted textures: run tools/convert_textures.py --bundles (sprites draw untextured)");
            var hwnd = CreateWindow(width, height);
            var dc = GetDC(hwnd);
            InitGl(dc);
            var scripted = Inputs.Source;   // --bot / --input drive a live run when given
            if (scripted == null) Inputs.Source = f => input;
            Console.WriteLine("PLAY window open: XInput pads and the keyboard drive the game; close the window to quit.");
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
                var views = RenderList.Build();
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                Draw(views, rc.right - rc.left, rc.bottom - rc.top);
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Shots.Contains(n)) Shot(n, rc.right - rc.left, rc.bottom - rc.top);
                if (MaxFrames > 0 && n >= MaxFrames) break;
                SwapBuffers(dc);   // vsync paces the loop (60 Hz)
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

        static void Shot(int n, int w, int h)
        {
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
            var hwnd = CreateWindowExW(0, "Cuphead360", "Cuphead360", style | 0x10000000 /* WS_VISIBLE */, 100, 100, r.right - r.left, r.bottom - r.top, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            ShowWindow(hwnd, 5);
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

        // Modes: 0 sprite/UI (tex*col), 1 screen blend, 2 multiply, 3 overlay, 4 alpha-only texture (fonts), 5 SDF text, 6 untextured.
        const string FS = @"#version 120
uniform sampler2D uTex; uniform int uMode; uniform vec4 uTint;
varying vec2 vUv; varying vec4 vCol;
void main() {
  vec4 t = texture2D(uTex, vUv);
  vec4 c = vCol * uTint;
  if (uMode == 1) { gl_FragColor = vec4(1.0 - (1.0 - t.rgb) * (1.0 - uTint.rgb), t.a * vCol.a); }
  else if (uMode == 2) { gl_FragColor = vec4(t.rgb * uTint.rgb, t.a * vCol.a); }
  else if (uMode == 3) { vec3 o = mix(2.0 * t.rgb * uTint.rgb, 1.0 - 2.0 * (1.0 - t.rgb) * (1.0 - uTint.rgb), step(0.5, t.rgb)); gl_FragColor = vec4(o, t.a * vCol.a); }
  else if (uMode == 4) { gl_FragColor = vec4(c.rgb, c.a * t.a); }
  else if (uMode == 5) { float a = smoothstep(0.45, 0.55, t.a); gl_FragColor = vec4(c.rgb, c.a * a); }
  else if (uMode == 6) { gl_FragColor = c; }
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
            try { wglSwapIntervalEXT = Fn<SwapIntervalFn>("wglSwapIntervalEXT"); wglSwapIntervalEXT(Unlocked ? 0 : 1); } catch (InvalidOperationException) { }
            uint vs = Compile(GL_VERTEX_SHADER, VS), fs = Compile(GL_FRAGMENT_SHADER, FS);
            program = glCreateProgram();
            glAttachShader(program, vs); glAttachShader(program, fs);
            glBindAttribLocation(program, 0, "aPos"); glBindAttribLocation(program, 1, "aUv"); glBindAttribLocation(program, 2, "aCol");
            glLinkProgram(program);
            glUseProgram(program);
            uScreen = glGetUniformLocation(program, "uScreen"); uMode = glGetUniformLocation(program, "uMode");
            uTint = glGetUniformLocation(program, "uTint"); uTex = glGetUniformLocation(program, "uTex");
            glUniform1i(uTex, 0);
            glEnableVertexAttribArray(0); glEnableVertexAttribArray(1); glEnableVertexAttribArray(2);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
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
            k += "_" + src.PathId;
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

        static uint LoadTex(string key)
        {
            string fn = Path.Combine(textureDir, key + ".tex");
            if (!File.Exists(fn)) { if (missingTex.Add(key)) Console.WriteLine("PLAY texture not converted: " + key); return 0; }
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

        static void Draw(List<RenderList.View> views, int ww, int wh)
        {
            float sx = ww / (float)Icalls.ScreenWidth, sy = wh / (float)Icalls.ScreenHeight;
            glViewport(0, 0, ww, wh);
            glDisable(GL_SCISSOR_TEST);
            glClearColor(0, 0, 0, 1);
            glClearStencil(0);
            glStencilMask(0xFF);
            glClear(GL_COLOR_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
            glUniform2f(uScreen, ww, wh);
            glEnable(GL_SCISSOR_TEST);
            foreach (var v in views)
            {
                var r = v.PixelRect;
                glScissor((int)(r.x * sx), (int)(r.y * sy), (int)Math.Ceiling(r.width * sx), (int)Math.Ceiling(r.height * sy));
                if (v.Camera != null && (v.Camera.ClearFlags == CameraClearFlags.SolidColor || v.Camera.ClearFlags == CameraClearFlags.Skybox))
                {
                    var bg = v.Camera.Background;
                    glClearColor(bg.r, bg.g, bg.b, 1);
                    glClear(GL_COLOR_BUFFER_BIT);
                }
                glStencilMask(0xFF);
                glClear(GL_STENCIL_BUFFER_BIT);   // each camera starts with a clear stencil (UGUI masks)
                foreach (var it in v.Items) DrawItem(it, sx, sy);
            }
            glDisable(GL_STENCIL_TEST);
            glColorMask(true, true, true, true);
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
            string shader = Objects.Of(MaterialState.Of(it.Material)?.Shader)?.Name ?? "";
            int mode = texMode;
            if (shader.Contains("SpriteBlendDynamic/Screen") || shader.EndsWith("/ScreenBlend")) mode = 1;
            else if (shader.Contains("ScreenIgnoreColor")) mode = 0;
            else if (shader.Contains("SpriteBlendDynamic/Multiply") || shader.EndsWith("/MultiplyBlend")) mode = 2;
            else if (shader.Contains("SpriteBlendDynamic/Overlay") || shader.EndsWith("/OverlayBlend")) mode = 3;
            else if (shader.Contains("Distance Field")) mode = 5;
            if (it.Texture == null) mode = 6;
            var tint = it.Tint;
            // UI graphics: the canvas alpha fades blend-mode materials too (Unity folds it into the vertex alpha).
            if (it.Kind == "ui" && mode >= 1 && mode <= 3)
                for (int i = 0; i < n; i++) col[i * 4 + 3] = (byte)(col[i * 4 + 3] * Mathf.Clamp01(tint.a));
            if (mode == 1 || mode == 2 || mode == 3) glUniform4f(uTint, tint.r, tint.g, tint.b, tint.a);
            else glUniform4f(uTint, tint.r, tint.g, tint.b, tint.a);
            glUniform1i(uMode, mode);
            glBindTexture(GL_TEXTURE_2D, tex);
            var st = it.Stencil;
            if (st != null)
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
            if (st != null) { glDisable(GL_STENCIL_TEST); glColorMask(true, true, true, true); }
        }
    }
}
