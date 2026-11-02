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

        static readonly InputFrame input = new InputFrame();
        static byte[] buf = new byte[1 << 20];
        static int len;
        static readonly Dictionary<Texture, int> ids = new Dictionary<Texture, int>();
        static readonly Dictionary<int, int> versions = new Dictionary<int, int>();
        static short[] pcm = new short[0];
        /// <summary>The last frame's real duration in seconds (Unity's deltaTime), 1/60 to 1/15; scripted runs keep 1/60.</summary>
        public static float Delta = 1f / 60f;

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
            long last = System.Diagnostics.Stopwatch.GetTimestamp();
            while (FrameBegin() != 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float real = (float)((now - last) / (double)System.Diagnostics.Stopwatch.Frequency);
                last = now;
                // A console frame slower than the vblank still moves the game at its real speed.
                Delta = scripted != null ? 1f / 60f : Math.Clamp(real, 1f / 60f, 1f / 15f);
                PollPads();
                if (!frame(++n)) break;
                Draw(RenderList.Build());
                if (n % 600 == 0)
                    Console.WriteLine($"CONSOLE frame {n} scene={Cuphead360.Engine.Scenes.Active?.Name} heap {GC.GetTotalMemory(false) >> 20} MB exceptions {Exceptions.Count}");
            }
            return 0;
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
            string shader = Objects.Of(MaterialState.Of(it.Material)?.Shader)?.Name ?? "";
            uint mode = 0;
            if (shader.Contains("SpriteBlendDynamic/Screen") || shader.EndsWith("/ScreenBlend")) mode = 1;
            else if (shader.Contains("ScreenIgnoreColor")) mode = 0;
            else if (shader.Contains("SpriteBlendDynamic/Multiply") || shader.EndsWith("/MultiplyBlend")) mode = 2;
            else if (shader.Contains("SpriteBlendDynamic/Overlay") || shader.EndsWith("/OverlayBlend")) mode = 3;
            else if (shader.Contains("Distance Field")) mode = 5;
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

        static int TextureId(Texture t)
        {
            if (!ids.TryGetValue(t, out int id))
            {
                id = ids.Count;
                ids[t] = id;
                var src = (Objects.Of(t) as NAsset)?.Source;
                if (src != null && RuntimePixels.Get(t) == null)
                {
                    string k = src.File.Path.Replace('/', '_');
                    if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
                    var key = Encoding.UTF8.GetBytes(k + "_" + src.PathId);
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
