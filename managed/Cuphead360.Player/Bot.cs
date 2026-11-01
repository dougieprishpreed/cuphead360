using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    /// <summary>
    /// Synthetic test input that reacts to what is on screen (object
    /// positions, the active scene), for repeatable end-to-end runs. It only
    /// produces pad input; it never touches game state. Plan file, one step
    /// per line:
    ///   wait N                      N frames of no input
    ///   press BUTTON [N]            hold a button N frames (default 6), then release 6
    ///   hold CONTROL VALUE N        hold a stick/trigger/button for N frames
    ///   waitscene NAME [TIMEOUT]    until the active scene is NAME
    ///   goto OBJECT [RADIUS] [TIMEOUT]   steer the left stick toward a GameObject named OBJECT
    ///   gotoxy X Y [RADIUS] [TIMEOUT]    steer toward a world position
    ///   fight [TIMEOUT]             boss fight policy until the scene changes
    ///   repeat N { ... }            not supported; write steps out
    /// Buttons/controls as in InputScript (A B X Y LB RB Back Start LS RS Up Down Left Right, LX LY RX RY LT RT).
    /// </summary>
    public sealed class Bot
    {
        readonly List<string[]> steps = new List<string[]>();
        int index = -1, stepStart, frame;
        readonly InputFrame f = new InputFrame();
        readonly System.Random rng = new System.Random(360);
        public bool Done => index >= steps.Count;

        public static Bot Load(string path)
        {
            var b = new Bot();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length > 0) b.steps.Add(line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return b;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        void Next(string why)
        {
            if (index >= 0 && index < steps.Count) Console.WriteLine($"BOT {string.Join(" ", steps[index])}: {why} at frame {frame}");
            index++;
            stepStart = frame;
        }

        static PadButton Button(string c) => c switch
        {
            "A" => PadButton.A, "B" => PadButton.B, "X" => PadButton.X, "Y" => PadButton.Y, "LB" => PadButton.LeftShoulder,
            "RB" => PadButton.RightShoulder, "Back" => PadButton.Back, "Start" => PadButton.Start, "LS" => PadButton.LeftThumb,
            "RS" => PadButton.RightThumb, "Up" => PadButton.DPadUp, "Down" => PadButton.DPadDown, "Left" => PadButton.DPadLeft,
            "Right" => PadButton.DPadRight, _ => PadButton.None,
        };

        static void Set(InputFrame.Pad p, string c, float v)
        {
            switch (c)
            {
                case "LX": p.LX = v; break; case "LY": p.LY = v; break; case "RX": p.RX = v; break; case "RY": p.RY = v; break;
                case "LT": p.LT = v; break; case "RT": p.RT = v; break;
                default: if (v != 0) p.Buttons |= Button(c); break;
            }
        }

        public InputFrame Step(int fr)
        {
            frame = fr;
            var p = f.Pads[0];
            p.Connected = true; p.Buttons = 0; p.LX = p.LY = p.RX = p.RY = p.LT = p.RT = 0;
            if (index < 0) Next("start");
            for (int guard = 0; guard < 8 && index < steps.Count; guard++)
            {
                var s = steps[index];
                int t = frame - stepStart;
                switch (s[0])
                {
                    case "wait":
                        if (t >= I(s[1])) { Next("done"); continue; }
                        return f;
                    case "press":
                        {
                            int n = s.Length > 2 ? I(s[2]) : 6;
                            if (t < n) { p.Buttons |= Button(s[1]); return f; }
                            if (t < n + 6) return f;
                            Next("done"); continue;
                        }
                    case "hold":
                        if (t < I(s[3])) { Set(p, s[1], F(s[2])); return f; }
                        Next("done"); continue;
                    case "pressuntil":   // pressuntil SCENE BUTTON INTERVAL [TIMEOUT]
                        {
                            if (Cuphead360.Engine.Scenes.Active?.Name == s[1]) { Next("reached"); continue; }
                            int timeout = s.Length > 4 ? I(s[4]) : 6000;
                            if (t > timeout) { Next("TIMEOUT"); continue; }
                            if (t % I(s[3]) < 6) p.Buttons |= Button(s[2]);
                            return f;
                        }
                    case "pressfor":     // pressfor BUTTON INTERVAL FRAMES
                        if (t >= I(s[3])) { Next("done"); continue; }
                        if (t % I(s[2]) < 6) p.Buttons |= Button(s[1]);
                        return f;
                    case "waitscene":
                        if (Cuphead360.Engine.Scenes.Active?.Name == s[1]) { Next("reached"); continue; }
                        if (s.Length > 2 && t > I(s[2])) { Next("TIMEOUT"); continue; }
                        return f;
                    case "goto":
                    case "gotoxy":
                        {
                            Vector3? target;
                            int ai = s[0] == "goto" ? 2 : 3;
                            if (s[0] == "goto") target = FindObject(s[1]);
                            else target = new Vector3(F(s[1]), F(s[2]), 0);
                            float radius = s.Length > ai ? F(s[ai]) : 0.3f;
                            int timeout = s.Length > ai + 1 ? I(s[ai + 1]) : 1800;
                            var me = PlayerPosition();
                            if (target == null || me == null) { if (t > timeout) { Next("TIMEOUT (no " + (target == null ? "target" : "player") + ")"); continue; } return f; }
                            var d = new Vector2(target.Value.x - me.Value.x, target.Value.y - me.Value.y);
                            if (d.magnitude <= radius) { path = null; Next($"reached ({me.Value.x:0.00},{me.Value.y:0.00})"); continue; }
                            if (t > timeout) { path = null; Next($"TIMEOUT at ({me.Value.x:0.00},{me.Value.y:0.00}), {d.magnitude:0.00} away"); continue; }
                            // Walk a path around solid scenery (planned with the game's own colliders), replanned when stuck.
                            var here = new Vector2(me.Value.x, me.Value.y);
                            if (path == null || t - planned > 240 || (t - planned > 30 && (here - lastHere).magnitude < 0.002f && stuckFrames++ > 20))
                            {
                                path = Plan(here, new Vector2(target.Value.x, target.Value.y), radius);
                                planned = t; stuckFrames = 0;
                                if (path == null) { Console.WriteLine($"BOT no path from ({here.x:0.00},{here.y:0.00}) to {s[1]}"); path = new List<Vector2> { new Vector2(target.Value.x, target.Value.y) }; }
                            }
                            lastHere = here;
                            Vector2 aim = new Vector2(target.Value.x, target.Value.y);
                            if (path != null)
                            {
                                while (path.Count > 1 && (path[0] - here).magnitude < 0.12f) path.RemoveAt(0);
                                aim = path[0];
                            }
                            var n = (aim - here).normalized;
                            p.LX = n.x; p.LY = n.y;
                            // Not moving (a dialogue or prompt holds the player): advance it with A.
                            if ((here - lastMoved).magnitude > 0.01f) { lastMoved = here; stillSince = t; }
                            if (t - stillSince > 150 && d.magnitude > radius + 1f && (t - stillSince) % 45 < 6) p.Buttons |= PadButton.A;
                            return f;
                        }
                    case "fight":
                        {
                            int timeout = s.Length > 1 ? I(s[1]) : 36000;
                            if (t == 0) fightScene = Cuphead360.Engine.Scenes.Active?.Name;
                            if (Cuphead360.Engine.Scenes.Active?.Name != fightScene) { Next("scene changed to " + Cuphead360.Engine.Scenes.Active?.Name); continue; }
                            if (t > timeout) { Next("TIMEOUT"); continue; }
                            Fight(p, t);
                            return f;
                        }
                    default:
                        Console.WriteLine("BOT unknown step: " + string.Join(" ", s));
                        Next("skipped");
                        continue;
                }
            }
            return f;
        }

        Vector2 lastMoved; int stillSince;
        List<Vector2> path;
        int planned, stuckFrames;
        Vector2 lastHere;

        /// <summary>A* on a grid around the start and goal, cells free when the player's collider fits there.</summary>
        static List<Vector2> Plan(Vector2 from, Vector2 to, float goalRadius)
        {
            NCollider2D body = null;
            foreach (var o in Objects.Live())
                if (o is NBehaviour b && b.GameObject != null && b.GameObject.ActiveInHierarchy && Array.IndexOf(playerTypes, b.Managed.GetType().Name) >= 0)
                    foreach (var c in b.GameObject.Components) if (c is NCollider2D col && !col.IsTrigger) { body = col; break; }
            float r = 0.15f;
            if (body != null) { body.Shapes(); r = Math.Max(body.AABB.width, body.AABB.height) * 0.5f; }
            float cell = Math.Max(0.08f, r * 0.6f);
            var solids = new List<NCollider2D>();
            foreach (var c in Cuphead360.Engine.Physics.Colliders())
                if (!c.IsTrigger && c != body && (body == null || c.Rigidbody != body.Rigidbody)
                    && !c.GameObject.Components.Exists(k => k is NCamera))   // camera bounds move with the camera
                { c.Shapes(); solids.Add(c); }
            var free = new Dictionary<(int, int), bool>();
            bool Free(int x, int y)
            {
                if (free.TryGetValue((x, y), out bool v)) return v;
                var pt = from + new Vector2(x * cell, y * cell);
                var probe = Shape.Circle(pt, r * 0.95f);
                var pb = probe.Bounds();
                v = true;
                foreach (var c in solids)
                {
                    if (body != null && !Cuphead360.Engine.Physics.LayersCollide(body.GameObject.Layer, c.GameObject.Layer)) continue;
                    if (!c.AABB.Overlaps(pb)) continue;
                    foreach (var sh in c.World) if (Geometry.Overlap(sh, probe)) { v = false; break; }
                    if (!v) break;
                }
                free[(x, y)] = v;
                return v;
            }
            int gx = (int)Math.Round((to.x - from.x) / cell), gy = (int)Math.Round((to.y - from.y) / cell);
            int reach = Math.Max(2, (int)(goalRadius / cell));
            var open = new SortedSet<(float f, int id, int x, int y)>();
            var g = new Dictionary<(int, int), float>();
            var came = new Dictionary<(int, int), (int, int)>();
            int ids = 0;
            g[(0, 0)] = 0;
            open.Add((Heur(0, 0), ids++, 0, 0));
            float Heur(int x, int y) => (float)Math.Sqrt((x - gx) * (x - gx) + (y - gy) * (y - gy));
            int expanded = 0;
            (int, int)? end = null;
            (int, int) closest = (0, 0); float closestH = Heur(0, 0);
            while (open.Count > 0 && expanded++ < 60000)
            {
                var curr = open.Min; open.Remove(curr);
                float hh = Heur(curr.x, curr.y);
                if (hh < closestH) { closestH = hh; closest = (curr.x, curr.y); }
                if (Math.Abs(curr.x - gx) <= reach && Math.Abs(curr.y - gy) <= reach) { end = (curr.x, curr.y); break; }
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = curr.x + dx, ny = curr.y + dy;
                        if (!Free(nx, ny)) continue;
                        if (dx != 0 && dy != 0 && (!Free(curr.x + dx, curr.y) || !Free(curr.x, curr.y + dy))) continue;
                        float ng = g[(curr.x, curr.y)] + (dx != 0 && dy != 0 ? 1.414f : 1f);
                        if (g.TryGetValue((nx, ny), out float old) && old <= ng) continue;
                        g[(nx, ny)] = ng; came[(nx, ny)] = (curr.x, curr.y);
                        open.Add((ng + Heur(nx, ny), ids++, nx, ny));
                    }
            }
            // The goal may sit inside a building's collider (map level entrances): go as close as the scenery allows.
            if (end == null && closest != (0, 0) && closestH * cell < 2.5f) end = closest;
            if (end == null) return null;
            var cells = new List<(int, int)>();
            for (var c = end.Value; ; c = came[c]) { cells.Add(c); if (!came.ContainsKey(c)) break; }
            cells.Reverse();
            var pts = new List<Vector2>();
            for (int i = 1; i < cells.Count; i += 2) pts.Add(from + new Vector2(cells[i].Item1 * cell, cells[i].Item2 * cell));
            if (end.Value == closest && closestH > reach) pts.Add(from + new Vector2(end.Value.Item1 * cell, end.Value.Item2 * cell));
            else pts.Add(to);
            return pts;
        }

        string fightScene;
        int jumpUntil, dashUntil, dashCooldown, jumpCooldown;
        readonly Dictionary<NGameObject, Vector3> lastPos = new Dictionary<NGameObject, Vector3>();

        /// <summary>
        /// A boss policy: shoot constantly (aiming up at targets above), hold
        /// a position away from the boss, jump enemy shots coming along the
        /// ground, step away from shots falling from above, dash out of tight
        /// spots. Level coordinates are pixels (camera orthographic size 360).
        /// </summary>
        void Fight(InputFrame.Pad p, int t)
        {
            p.Buttons |= PadButton.X;   // shoot
            var me = PlayerPosition();
            // Knocked out (ghost rising, death card up): confirm Retry now and then.
            bool alive = me != null && me.Value.y <= 1500 && PlayerAlive();
            if (!alive && wasAlive) Console.WriteLine($"BOT knocked out at frame {frame} fighting {LastBossName ?? "?"}");
            if (alive && !wasAlive) Console.WriteLine($"BOT attempt starts at frame {frame}");
            wasAlive = alive;
            if (!alive) { if (t % 90 < 6) p.Buttons |= PadButton.A; return; }
            var pos = me.Value;
            var body = pos + new Vector3(0, 60, 0);   // the player's hitbox centre, roughly
            var boss = BossPosition(pos);
            float dir = 0;
            bool aimUp = false; float aimDir = 0;
            const float edge = 500f;
            if (boss != null)
            {
                float dx = boss.Value.x - pos.x, dy = boss.Value.y - pos.y;
                float adx = Math.Abs(dx);
                // Keep 300..500 px from the boss, but off the walls.
                dir = adx < 300 ? -Math.Sign(dx) : adx > 500 ? Math.Sign(dx) : 0;
                aimUp = dy > 150;
                aimDir = Math.Sign(dx);
                if (dir == 0 && !aimUp) p.LX = Math.Sign(dx) * 0.35f;   // face it (walks slowly)
            }
            if (pos.x < -edge && dir < 0) dir = 0;
            if (pos.x > edge && dir > 0) dir = 0;
            // Threats: predict each enemy shot's closest approach over the next 40 frames.
            bool jump = false; float dodge = 0; bool threat = false;
            var seen = new Dictionary<NGameObject, Vector3>();
            foreach (var o in Objects.Live())
            {
                if (!(o is NGameObject g) || !g.ActiveInHierarchy || (g.Tag != "EnemyProjectile" && g.Tag != "Enemy")) continue;
                var q = g.Transform.Position;
                seen[g] = q;
                if (g.Tag == "Enemy" && boss.HasValue && (q - boss.Value).sqrMagnitude < 1) continue;
                var v = lastPos.TryGetValue(g, out var lp) ? q - lp : Vector3.zero;
                var rel = q - body;
                float best = rel.magnitude; int when = 0;
                for (int k = 1; k <= 40; k++)
                {
                    float d = (rel + v * k).magnitude;
                    if (d < best) { best = d; when = k; }
                }
                if (best > 110) continue;
                threat = true;
                var at = rel + v * when;   // where it passes, relative to us
                bool lowAndLevel = Math.Abs(v.y) < Math.Abs(v.x) * 0.5f && at.y < 70;
                if (lowAndLevel && when < 22) jump = true;
                else dodge += at.x > 0 ? -1 : 1;   // step away from where it passes
            }
            lastPos.Clear();
            foreach (var kv in seen) lastPos[kv.Key] = kv.Value;
            if (dodge != 0)
            {
                dir = Math.Sign(dodge);
                // Walled in: go the other way under/through it with a dash.
                if ((pos.x < -edge && dir < 0) || (pos.x > edge && dir > 0)) { dir = -dir; if (dashCooldown == 0) { dashUntil = t + 4; dashCooldown = 90; } }
                aimUp = false;
            }
            if (dir != 0) p.LX = dir;
            // Target above: hold Lock (RB) and aim diagonally (or straight up when right below it).
            if (aimUp && !jump && !threat) { p.Buttons |= PadButton.RightShoulder; p.LY = 1; p.LX = Math.Abs(boss.Value.x - pos.x) < 120 ? 0 : aimDir; }
            if (jump && t >= jumpCooldown) { jumpUntil = t + 16; jumpCooldown = t + 34; }
            if (t < jumpUntil) p.Buttons |= PadButton.A;
            if (dashCooldown > 0) dashCooldown--;
            if (t < dashUntil) p.Buttons |= PadButton.Y;   // Dash (Rewired map: gamepad template top row 2)
        }

        /// <summary>A GameObject's position, or for a map entrance the point the game measures interaction from.</summary>
        static Vector3? FindObject(string name)
        {
            foreach (var o in Objects.Live())
                if (o is NGameObject g && g.Name == name && g.ActiveInHierarchy)
                {
                    var pos = g.Transform.Position;
                    foreach (var c in g.Components)
                    {
                        if (!(c is NBehaviour b)) continue;
                        var fi = b.Managed.GetType().GetField("interactionPoint");
                        if (fi != null && fi.FieldType == typeof(Vector2)) { var ip = (Vector2)fi.GetValue(b.Managed); return pos + new Vector3(ip.x, ip.y, 0); }
                    }
                    return pos;
                }
            return null;
        }

        static bool PlayerAlive()
        {
            foreach (var o in Objects.Live())
                if (o is NSpriteRenderer sr && sr.GameObject != null && sr.GameObject.ActiveInHierarchy && sr.Sprite != null)
                {
                    var n = Sprites.Of(sr.Sprite)?.Name ?? "";
                    if (n.StartsWith("cuphead_ghost") || n.StartsWith("mugman_ghost")) return false;
                }
            return true;
        }

        static readonly string[] playerTypes = { "MapPlayerController", "LevelPlayerController", "PlanePlayerController" };
        static Vector3? PlayerPosition()
        {
            foreach (var o in Objects.Live())
                if (o is NBehaviour b && b.GameObject != null && b.GameObject.ActiveInHierarchy && Array.IndexOf(playerTypes, b.Managed.GetType().Name) >= 0)
                    return b.GameObject.Transform.Position;
            return null;
        }

        bool wasAlive = true;
        static string LastBossName;

        /// <summary>The nearest active enemy with a damage receiver (the boss part being fought).</summary>
        static Vector3? BossPosition(Vector3 from)
        {
            Vector3? best = null; float bd = float.MaxValue;
            foreach (var o in Objects.Live())
                if (o is NBehaviour b && b.GameObject != null && b.GameObject.ActiveInHierarchy && b.Managed.GetType().Name == "DamageReceiver")
                {
                    var go = b.GameObject;
                    if (go.Tag == "Player" || go.Name.Contains("Player")) continue;
                    // Aim at the hit area: the collider's centre, not the transform pivot.
                    var q = go.Transform.Position;
                    foreach (var c in go.Components)
                        if (c is NCollider2D col && col.Live) { col.Shapes(); q = new Vector3(col.AABB.center.x, col.AABB.center.y, q.z); break; }
                    float d = (q - from).sqrMagnitude;
                    if (d < bd) { bd = d; best = q; LastBossName = go.Name; }
                }
            return best;
        }
    }
}
