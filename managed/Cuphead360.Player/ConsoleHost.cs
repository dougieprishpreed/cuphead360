using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    /// <summary>
    /// The console build's side of the host boundary (the XEX's console/main.cpp
    /// implements the other). The player runs as a WASI module translated to
    /// C; each frame it asks the host for the pads, runs the player loop, and
    /// hands over the frame's draw list, new textures and the mixed audio
    /// through the "ch360" imports. Wasm memory is little-endian; the host
    /// reads it with w2c2's load helpers.
    ///
    /// Draw list (little-endian):
    ///   u32 'CHF1', u32 views
    ///   view: f32 x y w h, u32 clear, f32 r g b a, u32 items
    ///   item: i32 texture (-1 none), u32 mode, f32 blend r g b a, u32 vertices
    ///         vertex: f32 x y u v, u8 r g b a      (a triangle list)
    /// Modes: 0 texel * colour, 1 screen, 2 multiply, 3 overlay (the blend
    /// colour is the renderer colour), 5 SDF text (texel alpha thresholded).
    /// </summary>
    internal static unsafe class ConsoleHost
    {
        [DllImport("ch360", EntryPoint = "frame_begin"), WasmImportLinkage]
        static extern int FrameBegin();
        [DllImport("ch360", EntryPoint = "pad"), WasmImportLinkage]
        static extern int Pad(int index, byte* state);
        [DllImport("ch360", EntryPoint = "submit"), WasmImportLinkage]
        static extern void Submit(byte* data, int length);
        [DllImport("ch360", EntryPoint = "texture_file"), WasmImportLinkage]
        static extern void TextureFile(int id, byte* key, int length);
        [DllImport("ch360", EntryPoint = "texture_pixels"), WasmImportLinkage]
        static extern void TexturePixels(int id, int width, int height, byte* bgra);
        [DllImport("ch360", EntryPoint = "audio"), WasmImportLinkage]
        static extern void AudioOut(short* samples, int frames);
        [DllImport("ch360", EntryPoint = "ogg_open"), WasmImportLinkage]
        static extern int OggOpen(byte* path, int length, int* info);
        [DllImport("ch360", EntryPoint = "ogg_read"), WasmImportLinkage]
        static extern int OggRead(int handle, float* samples, int frames);
        [DllImport("ch360", EntryPoint = "ogg_seek"), WasmImportLinkage]
        static extern int OggSeek(int handle, int frame);
        [DllImport("ch360", EntryPoint = "ogg_close"), WasmImportLinkage]
        static extern void OggClose(int handle);

        /// <summary>
        /// A clip decoded by the host (stb_vorbis in native code, console/ch360_ogg.c):
        /// NVorbis as translated wasm took most of a world map frame on the console.
        /// </summary>
        sealed class HostClip : Audio.IClipReader
        {
            int h;
            public int Channels { get; }
            public int SampleRate { get; }
            public long TotalSamples { get; }
            HostClip(int handle, int channels, int rate, long frames) { h = handle; Channels = channels; SampleRate = rate; TotalSamples = frames; }

            public static HostClip Open(string path)
            {
                var b = Encoding.UTF8.GetBytes(path);
                int* info = stackalloc int[3];
                int h;
                fixed (byte* p = b) h = OggOpen(p, b.Length, info);
                return h < 0 || info[0] <= 0 ? null : new HostClip(h, info[0], info[1], (uint)info[2]);
            }

            public long SamplePosition { set { if (h >= 0) OggSeek(h, (int)Math.Clamp(value, 0, int.MaxValue)); } }

            public int ReadSamples(float[] buffer, int offset, int count)
            {
                int frames = count / Channels;
                if (h < 0 || frames <= 0) return 0;
                fixed (float* p = &buffer[offset]) return OggRead(h, p, frames) * Channels;
            }

            public void Dispose() { if (h >= 0) OggClose(h); h = -1; }
        }

        static readonly InputFrame input = new InputFrame();
        static byte[] buf = new byte[1 << 20];
        static int len;
        static readonly Dictionary<Texture, int> ids = new Dictionary<Texture, int>();
        static readonly Dictionary<int, int> versions = new Dictionary<int, int>();
        static short[] pcm = new short[0];
        /// <summary>
        /// The last frame's real duration in seconds (Unity's deltaTime), 1/60 to
        /// 1/30: a slower frame moves the game less than real time rather than run
        /// more fixed steps and fall further behind; the mixer still gets the real
        /// time (PlayerLoop.AudioDelta). Scripted runs keep 1/60.
        /// </summary>
        public static float Delta = 1f / 60f;
        static readonly double[] phaseSum = new double[PlayerLoop.PhaseMs.Length + 2];
        static double frameSum;

        public static int Play(Func<int, bool> frame)
        {
            var scripted = Inputs.Source;   // --bot / --input still drive a console run when given
            if (scripted == null) Inputs.Source = f => input;
            Audio.Output = (mix, frames) =>
            {
                if (pcm.Length < frames * 2) pcm = new short[frames * 2];
                for (int i = 0; i < frames * 2; i++) pcm[i] = (short)Math.Round(Math.Clamp(mix[i], -1f, 1f) * 32767f);
                fixed (short* p = pcm) AudioOut(p, frames);
            };
            Console.WriteLine("CONSOLE host loop");
            int n = 0;
            long last = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
            PlayerLoop.Profile = true;
            while (FrameBegin() != 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float real = (float)((now - last) / (double)hz);
                last = now;
                // A console frame slower than the vblank still moves the game at its real speed.
                Delta = scripted != null ? 1f / 60f : Math.Clamp(real, 1f / 60f, 1f / 30f);
                PlayerLoop.AudioDelta = scripted != null ? -1 : Math.Clamp(real, 0f, 0.25f);
                PollPads();
                if (!frame(++n)) break;
                long built = System.Diagnostics.Stopwatch.GetTimestamp();
                var views = RenderList.Build();
                long listed = System.Diagnostics.Stopwatch.GetTimestamp();
                Draw(views);
                long drawn = System.Diagnostics.Stopwatch.GetTimestamp();
                var ph = PlayerLoop.PhaseMs;
                for (int i = 0; i < ph.Length; i++) phaseSum[i] += ph[i];
                phaseSum[ph.Length] += (listed - built) * 1000.0 / hz;
                phaseSum[ph.Length + 1] += (drawn - listed) * 1000.0 / hz;
                frameSum += (drawn - now) * 1000.0 / hz;
                // A frame over half a second says where its time went (the player loop's phases, the draw list).
                if ((drawn - now) * 2 > hz)
                    Console.WriteLine($"SLOW frame {n}: {(drawn - now) * 1000 / hz} ms; {PlayerLoop.ProfileReport(1)} list={(listed - built) * 1000 / hz} submit={(drawn - listed) * 1000 / hz}");
                else Array.Clear(PlayerLoop.PhaseMs);
                // Where an ordinary frame's time goes, averaged over 300 frames.
                if (n % 300 == 0)
                {
                    var sb = new StringBuilder($"FRAMES {n - 299}-{n}: {frameSum / 300:0.0} ms/frame;");
                    for (int i = 0; i < ph.Length; i++) sb.Append(' ').Append(PlayerLoop.PhaseNames[i]).Append('=').Append((phaseSum[i] / 300).ToString("0.0"));
                    sb.Append(" list=").Append((phaseSum[ph.Length] / 300).ToString("0.0")).Append(" submit=").Append((phaseSum[ph.Length + 1] / 300).ToString("0.0"));
                    sb.Append("; audio decode ").Append((Audio.DecodeTicks * 1000.0 / hz / 300).ToString("0.0")).Append(" ms/frame, ").Append(Audio.VoiceCount).Append(" voices");
                    Audio.DecodeTicks = 0;
                    Console.WriteLine(sb.ToString());
                    Array.Clear(phaseSum);
                    frameSum = 0;
                }
                if (n % 600 == 0)
                    Console.WriteLine($"CONSOLE frame {n} scene={Cuphead360.Engine.Scenes.Active?.Name} heap {GC.GetTotalMemory(false) >> 20} MB exceptions {Exceptions.Count}; "
                        + $"GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (gen0/1/2), paused {(long)GC.GetTotalPauseDuration().TotalMilliseconds} ms in all; "
                        + $"converted files {ChobFile.LoadedFiles} ({ChobFile.LoadedBytes >> 20} MB, objects read {ChobFile.ObjectReads} ({ChobFile.ObjectBytes >> 20} MB) in {ChobFile.BlockReads} block reads)"
                        + $", trees dropped {ChobObject.TreesDropped}, parsed again {ChobObject.TreesReparsed}");
            }
            return 0;
        }

        /// <summary>
        /// Console set-up and a 64-bit integer check at start-up: the first two
        /// hardware runs formatted path ids with their low word's sign, which
        /// "I64CHECK" catches (values come from strings so nothing folds at
        /// compile time).
        /// </summary>
        public static void SelfCheck()
        {
            // The console reports slow loads and keeps a smaller decoded-audio cache (32 MB).
            Cuphead360.Engine.SceneLoader.Timing = true;
            Audio.OpenReader = HostClip.Open;
            ChobFile.Trace = Console.WriteLine;
            Audio.CacheFloats = 4L * 1024 * 1024;   // 16 MB
            if (ChobObject.TreeBudget == long.MaxValue) ChobObject.TreeBudget = 24L << 20;   // parsed trees kept (estimated bytes)
            ChobFile.BlockBudget = 8L << 20;   // large files' blocks kept
            var bad = new List<string>();
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "4294967296", "-4294967296", "2147483648", "-1" })
                if (long.Parse(s).ToString() != s) bad.Add("format " + s + " -> " + long.Parse(s));
            long two32 = long.Parse("4294967296"), one = long.Parse("1"), two31 = long.Parse("2147483648"), zero = long.Parse("0");
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "-9223372036854775808", "0" })
                if (IdText.Of(long.Parse(s)) != s) bad.Add("IdText " + s + " -> " + IdText.Of(long.Parse(s)));
            if (!(two32 > one)) bad.Add("2^32 > 1");
            if (!(two31 > zero)) bad.Add("2^31 > 0");
            if (two32 + 5 == long.Parse("5")) bad.Add("2^32+5 == 5");
            if (!((ulong)two32 > uint.MaxValue)) bad.Add("(ulong)2^32 > uint.MaxValue");
            if (!(-two32 < zero)) bad.Add("-2^32 < 0");
            // What 32-bit mode could still get wrong after record_forms.py: signs
            // and zero tests of 64-bit results, carries, shifts of negatives.
            long neg2_32 = -two32, mixed = long.Parse("-4294967295");   // 0xFFFFFFFF_00000000, 0xFFFFFFFF_00000001
            if (Math.Sign(two31) != 1 || Math.Sign(neg2_32) != -1 || Math.Sign(mixed) != -1) bad.Add("Math.Sign");
            if (two31.CompareTo(zero) != 1 || neg2_32.CompareTo(zero) != -1) bad.Add("CompareTo 0");
            if (Math.Abs(neg2_32) != two32 || Math.Abs(two31) != two31) bad.Add("Math.Abs");
            if (neg2_32 / 16 != long.Parse("-268435456") || mixed / 2 != long.Parse("-2147483647")) bad.Add("signed / 2^n");
            if (neg2_32 >> 4 != long.Parse("-268435456")) bad.Add("signed >> 4");
            if ((two32 & two32) == 0 || (neg2_32 & long.Parse("-4294967296")) == 0) bad.Add("high-word & != 0");
            ulong ua = ulong.Parse("4294967296"), ub = ulong.Parse("4294967295"), uc = ulong.Parse("18446744069414584320");
            if (!(ua > ub) || !(uc > ua) || Math.Max(ua, ub) != ua || Math.Min(uc, ua) != ua) bad.Add("unsigned compare");
            if (ub + 1 != ua || ua - 1 != ub || unchecked(uc + ua) != 0) bad.Add("carry");
            Console.WriteLine(bad.Count == 0 ? "I64CHECK ok" : "I64CHECK FAIL: " + string.Join("; ", bad));
        }

        static void PollPads()
        {
            byte* s = stackalloc byte[16];
            for (int i = 0; i < 4; i++)
            {
                var p = input.Pads[i];
                if (Pad(i, s) == 0) { p.Connected = false; p.Buttons = 0; p.LX = p.LY = p.RX = p.RY = p.LT = p.RT = 0; continue; }
                var span = new ReadOnlySpan<byte>(s, 16);
                p.Connected = true;
                p.Buttons = (PadButton)BinaryPrimitives.ReadUInt16LittleEndian(span);
                p.LX = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(2)), 7849);
                p.LY = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(4)), 7849);
                p.RX = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(6)), 8689);
                p.RY = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(8)), 8689);
                p.LT = span[10] > 30 ? span[10] / 255f : 0;
                p.RT = span[11] > 30 ? span[11] / 255f : 0;
            }
        }

        static float Stick(short v, int dead) => Math.Abs((int)v) < dead ? 0 : Math.Max(-1f, v / 32767f);

        // ------------------------------------------------------------ draw list

        static void Need(int bytes)
        {
            if (len + bytes <= buf.Length) return;
            Array.Resize(ref buf, Math.Max(buf.Length * 2, len + bytes));
        }
        static void U32(uint v) { Need(4); BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(len), v); len += 4; }
        static void I32(int v) { Need(4); BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(len), v); len += 4; }
        static void F32(float v) { Need(4); BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(len), v); len += 4; }

        static void Draw(List<RenderList.View> views)
        {
            len = 0;
            U32(0x31464843);   // 'CHF1'
            U32((uint)views.Count);
            foreach (var v in views)
            {
                F32(v.PixelRect.x); F32(v.PixelRect.y); F32(v.PixelRect.width); F32(v.PixelRect.height);
                var cam = v.Camera;
                bool clear = cam != null && (cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.Skybox);
                U32(clear ? 1u : 0u);
                var bg = cam != null ? cam.Background : default;
                F32(bg.r); F32(bg.g); F32(bg.b); F32(1f);
                int countAt = len; U32(0);
                uint items = 0;
                foreach (var it in v.Items) if (Item(it)) items++;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(countAt), items);
            }
            fixed (byte* p = buf) Submit(p, len);
        }

        static bool Item(RenderList.Item it)
        {
            int n = it.Triangles.Length;
            if (n == 0) return false;
            // A UGUI mask's own graphic only writes the stencil (no stencil here yet: its children draw unclipped).
            if (it.Stencil != null && it.Stencil[5] == 0) return false;
            int tex = -1;
            if (it.Texture != null && (tex = TextureId(it.Texture)) < 0) return false;   // not drawable (render textures)
            uint mode = ModeOf(MaterialState.Of(it.Material)?.Shader);
            var tint = it.Tint;
            I32(tex); U32(mode);
            F32(tint.r); F32(tint.g); F32(tint.b); F32(tint.a);
            U32((uint)n);
            Need(n * 20);
            bool blend = mode >= 1 && mode <= 3;
            float alphaScale = blend && it.Kind == "ui" ? Mathf.Clamp01(tint.a) : 1f;
            var span = buf.AsSpan();
            for (int i = 0; i < n; i++)
            {
                int k = it.Triangles[i];
                if (k < 0 || k >= it.Screen.Length) k = 0;
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len), it.Screen[k].x);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 4), it.Screen[k].y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 8), it.Uv[k].x);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 12), it.Uv[k].y);
                var c = it.Colors != null && k < it.Colors.Length ? it.Colors[k] : new Color32(255, 255, 255, 255);
                if (blend) { buf[len + 16] = c.r; buf[len + 17] = c.g; buf[len + 18] = c.b; buf[len + 19] = (byte)(c.a * alphaScale); }
                else
                {
                    // the renderer colour folds into the vertex colour
                    buf[len + 16] = (byte)Mathf.Clamp(c.r * tint.r, 0, 255); buf[len + 17] = (byte)Mathf.Clamp(c.g * tint.g, 0, 255);
                    buf[len + 18] = (byte)Mathf.Clamp(c.b * tint.b, 0, 255); buf[len + 19] = (byte)Mathf.Clamp(c.a * tint.a, 0, 255);
                }
                len += 20;
            }
            return true;
        }

        // A shader's blend mode, worked out from its name once.
        static readonly Dictionary<Shader, uint> modes = new Dictionary<Shader, uint>();
        static uint ModeOf(Shader sh)
        {
            if (sh == null) return 0;
            if (modes.TryGetValue(sh, out uint mode)) return mode;
            string shader = Objects.Of(sh)?.Name ?? "";
            if (shader.Contains("SpriteBlendDynamic/Screen") || shader.EndsWith("/ScreenBlend")) mode = 1;
            else if (shader.Contains("ScreenIgnoreColor")) mode = 0;
            else if (shader.Contains("SpriteBlendDynamic/Multiply") || shader.EndsWith("/MultiplyBlend")) mode = 2;
            else if (shader.Contains("SpriteBlendDynamic/Overlay") || shader.EndsWith("/OverlayBlend")) mode = 3;
            else if (shader.Contains("Distance Field")) mode = 5;
            modes[sh] = mode;
            return mode;
        }

        static int TextureId(Texture t)
        {
            if (!ids.TryGetValue(t, out int id))
            {
                id = ids.Count;
                ids[t] = id;
                var src = (Objects.Of(t) as NAsset)?.Source;
                if (t is RenderTexture) { ids[t] = -1; return -1; }   // not drawn yet (no converted file)
                if (src != null && RuntimePixels.Get(t) == null)
                {
                    string k = src.File.Path.Replace('/', '_');
                    if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
                    var key = Encoding.UTF8.GetBytes(k + "_" + IdText.Of(src.PathId));
                    fixed (byte* p = key) TextureFile(id, p, key.Length);
                }
                else if (RuntimePixels.Get(t) == null) { ids[t] = -1; return -1; }
            }
            if (id < 0) return -1;
            var rp = RuntimePixels.Get(t);
            if (rp != null && (!versions.TryGetValue(id, out int ver) || ver != rp.Version))
            {
                versions[id] = rp.Version;
                // Runtime pixels are stored top row first; the console's first row is Unity's v = 0 (the bottom).
                var bgra = new byte[rp.W * rp.H * 4];
                for (int y = 0; y < rp.H; y++)
                {
                    int src = y * rp.W * rp.Channels, dst = (rp.H - 1 - y) * rp.W * 4;
                    for (int x = 0; x < rp.W; x++, src += rp.Channels, dst += 4)
                    {
                        if (rp.Channels == 1) { bgra[dst] = bgra[dst + 1] = bgra[dst + 2] = 255; bgra[dst + 3] = rp.Data[src]; }
                        else { bgra[dst] = rp.Data[src + 2]; bgra[dst + 1] = rp.Data[src + 1]; bgra[dst + 2] = rp.Data[src]; bgra[dst + 3] = rp.Data[src + 3]; }
                    }
                }
                fixed (byte* p = bgra) TexturePixels(id, rp.W, rp.H, p);
            }
            return id;
        }
    }
}
