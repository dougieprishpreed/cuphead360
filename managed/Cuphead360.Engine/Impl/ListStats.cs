using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// CH360_LIST_STATS=N (tools/level_costs.py): every Nth frame the render list is built as the console's host builds
    /// it and counted, and every 600 frames a LIST line gives what a frame drew (items, vertices, textures, render
    /// targets: average and most), its densest item and the build's time on this machine, and the texels of the distinct
    /// sprite boxes drawn over the window (the art a level shows in ten seconds, against the console's texture room:
    /// what doesn't fit is read from the drive again in play). The PC's own run path builds no list, so a level's
    /// drawing cost was seen only on the console.
    /// </summary>
    public static class ListStats
    {
        public static readonly int Every = int.TryParse(Environment.GetEnvironmentVariable("CH360_LIST_STATS"), out var e) ? e : 0;
        static int frames, from = -1;
        static long items, verts, textures, targets, ticks, maxItems, maxVerts, bigVerts;
        static string bigName;
        static readonly HashSet<Texture> seen = new HashSet<Texture>();
        static readonly HashSet<RenderTexture> seenTargets = new HashSet<RenderTexture>();
        static readonly HashSet<(Texture, int, int, int, int)> boxes = new HashSet<(Texture, int, int, int, int)>();
        static double texels;

        public static void Count(List<RenderList.View> views, long buildTicks)
        {
            if (from < 0) from = Clock.FrameCount;
            int it = 0, vx = 0;
            seen.Clear(); seenTargets.Clear();
            foreach (var v in views)
            {
                if (v.Target != null) seenTargets.Add(v.Target);
                foreach (var item in v.Items)
                {
                    it++;
                    int n = item.Local?.Length ?? item.Screen?.Length ?? 0;
                    vx += n;
                    if (item.Texture != null)
                    {
                        seen.Add(item.Texture);
                        if (item.Uv != null && item.Uv.Length > 0 && !(item.Texture is RenderTexture))
                        {
                            float u0 = 1, v0 = 1, u1 = 0, v1 = 0;
                            foreach (var uv in item.Uv) { u0 = Math.Min(u0, uv.x); v0 = Math.Min(v0, uv.y); u1 = Math.Max(u1, uv.x); v1 = Math.Max(v1, uv.y); }
                            int w = item.Texture.width, h = item.Texture.height;
                            var key = (item.Texture, (int)(u0 * w) >> 4, (int)(v0 * h) >> 4, (int)(u1 * w) >> 4, (int)(v1 * h) >> 4);
                            if (boxes.Add(key)) texels += Math.Max(0, u1 - u0) * w * Math.Max(0, v1 - v0) * h;
                        }
                    }
                    if (n > bigVerts) { bigVerts = n; bigName = item.Name; }
                }
            }
            frames++; items += it; verts += vx; textures += seen.Count; targets += seenTargets.Count; ticks += buildTicks;
            maxItems = Math.Max(maxItems, it); maxVerts = Math.Max(maxVerts, vx);
            if (Clock.FrameCount - from < 600) return;
            double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
            Log.Line($"LIST frames {from}-{Clock.FrameCount}: {items / frames} items (most {maxItems}), {verts / frames} vertices (most {maxVerts}), {textures / frames} textures, {targets / frames} render targets a frame; densest item {bigName} {bigVerts} vertices; built in {ms:0.00} ms; distinct sprite art {texels / 1048576:0.0} Mtexels in {boxes.Count} boxes");
            frames = 0; from = Clock.FrameCount; items = verts = textures = targets = ticks = maxItems = maxVerts = bigVerts = 0; bigName = null;
            boxes.Clear(); texels = 0;
        }
    }
}
