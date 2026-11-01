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
                            if (d.magnitude <= radius) { Next($"reached ({me.Value.x:0.00},{me.Value.y:0.00})"); continue; }
                            if (t > timeout) { Next($"TIMEOUT at ({me.Value.x:0.00},{me.Value.y:0.00}), {d.magnitude:0.00} away"); continue; }
                            var n = d.normalized;
                            p.LX = n.x; p.LY = n.y;
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

        static Vector3? FindObject(string name)
        {
            foreach (var o in Objects.Live())
                if (o is NGameObject g && g.Name == name && g.ActiveInHierarchy) return g.Transform.Position;
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
