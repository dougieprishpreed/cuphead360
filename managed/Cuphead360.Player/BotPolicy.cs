using System;
using System.Collections.Generic;
using System.Reflection;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    /// <summary>
    /// The sweep's fight policy ("fight N all") and the fight's read-only
    /// tracking: the boss's phases from the level's own LevelProperties (its
    /// states and health triggers), its health, a win, long stretches without
    /// damage; the player's weapon, super meter, supers, EX shots, parries and
    /// dashes. Everything here reads game state; the only output is pad input.
    /// The policy plays every part of the player's kit: EX shots and supers
    /// (alternating windows of spending cards and saving five), parries (pink
    /// shots, switches and entities: the only damage in King's Leap and the
    /// Mausoleum), weapon switches, dashes and 8-way aiming; plane levels fly
    /// in two dimensions, run-and-gun levels run right.
    /// </summary>
    public sealed partial class Bot
    {
        bool fightAll;
        static readonly bool traceBot = Environment.GetEnvironmentVariable("CH360_TRACE_BOT") == "1";

        static readonly Dictionary<string, Type> gameTypes = new Dictionary<string, Type>();
        static Type GameType(string name)
        {
            if (gameTypes.TryGetValue(name, out var t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) if ((t = asm.GetType(name)) != null) break;
            return gameTypes[name] = t;
        }

        const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static readonly Dictionary<(Type, string), MemberInfo> members = new Dictionary<(Type, string), MemberInfo>();

        /// <summary>A field or property of o (private ones of base classes too), or null.</summary>
        static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            if (!members.TryGetValue((t, name), out var m))
            {
                for (var k = t; k != null && m == null; k = k.BaseType)
                    m = (MemberInfo)k.GetField(name, AnyMember) ?? k.GetProperty(name, AnyMember);
                members[(t, name)] = m;
            }
            return m is FieldInfo fi ? fi.GetValue(o) : m is PropertyInfo pi ? pi.GetValue(o) : null;
        }

        static float GetFloat(object o, string name) => Get(o, name) switch { float v => v, int i => i, _ => float.NaN };

        /// <summary>Synthetic, as the equip menu's result: player one's weapons, super and charm.</summary>
        static string SetLoadout(string[] s)
        {
            var data = GameType("PlayerData")?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var loadouts = Get(data, "Loadouts");
            var one = Get(loadouts, "playerOne");
            if (one == null || s.Length < 5) return "PlayerData.Data.Loadouts.playerOne not found";
            try
            {
                void SetEnum(string field, string type, string value) => one.GetType().GetField(field).SetValue(one, Enum.Parse(GameType(type), value));
                SetEnum("primaryWeapon", "Weapon", s[1]);
                SetEnum("secondaryWeapon", "Weapon", s[2]);
                SetEnum("super", "Super", s[3]);
                SetEnum("charm", "Charm", s[4]);
            }
            catch (ArgumentException e) { return "loadout: " + e.Message; }
            return $"SYNTHETIC: player one's loadout {s[1]} {s[2]} {s[3]} {s[4]}";
        }

        // ---- tracking ----
        object trackedLevel, props, timeline;
        string phase;
        float lastHealth = float.NaN, totalHealth, lastDamage = float.NaN, timelineHealth;
        readonly List<(string name, float at)> events = new List<(string, float)>();
        int eventsReached;
        int lastDamageFrame, stalls, exPresses, superPresses, superStarts, parryTries, dashes, switches;
        bool wonLogged;
        readonly List<string> phaseList = new List<string>(), phasesReached = new List<string>();
        readonly List<string> weaponsSeen = new List<string>(), supersSeen = new List<string>(), targetsSeen = new List<string>();
        string lastWeapon;
        object lastSuper;

        void ResetTracking()
        {
            trackedLevel = props = timeline = null; phase = null; lastHealth = lastDamage = float.NaN; totalHealth = timelineHealth = 0;
            events.Clear(); eventsReached = 0;
            lastDamageFrame = frame; stalls = exPresses = superPresses = superStarts = parryTries = dashes = switches = 0; wonLogged = false;
            phaseList.Clear(); phasesReached.Clear(); weaponsSeen.Clear(); targetsSeen.Clear(); supersSeen.Clear(); lastWeapon = null; lastSuper = null;
            runBest = float.MinValue; runLastProgress = 0; parryArm = false;
            wantSuper = true; exLeft = 0; lastMeter = float.NaN; supersUsed = exUsed = 0;
            ResetForce();
        }

        /// <summary>The level's LevelProperties (a field whose type derives from AbstractLevelProperties), or null.</summary>
        static object LevelProperties(object level)
        {
            for (var k = level?.GetType(); k != null; k = k.BaseType)
                foreach (var fi in k.GetFields(AnyMember))
                    for (var b = fi.FieldType.BaseType; b != null; b = b.BaseType)
                        if (b.IsGenericType && b.GetGenericTypeDefinition().Name == "AbstractLevelProperties`3") return fi.GetValue(level);
            return null;
        }

        void TrackPhases()
        {
            var levelType = GameType("Level");
            var level = levelType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            // A retry makes a new level, whose timeline its Start makes a frame or more later.
            if (level != trackedLevel || (level != null && !ReferenceEquals(Get(level, "timeline"), timeline)))
            {
                bool sameLevel = level == trackedLevel;
                trackedLevel = level; props = LevelProperties(level); if (!sameLevel) phase = null;
                lastDamageFrame = frame; lastDamage = float.NaN;
                phaseList.Clear();
                if (props != null)
                {
                    totalHealth = GetFloat(props, "TotalHealth");
                    var parts = new List<string>();
                    if (Get(props, "states") is Array states)
                        foreach (var st in states)
                        {
                            string n = Get(st, "stateName")?.ToString();
                            if (!phaseList.Contains(n)) phaseList.Add(n);
                            parts.Add($"{n}@{GetFloat(st, "healthTrigger"):0.##}");
                        }
                    if (!sameLevel) Console.WriteLine($"BOT level {level.GetType().Name} {levelType.GetProperty("CurrentMode", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)}: health {totalHealth:0}, phases {string.Join(", ", parts)}");
                }
                else if (level != null && !sameLevel) Console.WriteLine($"BOT level {level.GetType().Name}: no boss properties");
                // The death card's timeline: the boss's whole health, its named milestones (an event at
                // percentage p is reached at damage health * (1 - p)).
                timeline = Get(level, "timeline");
                events.Clear(); eventsReached = 0;
                timelineHealth = GetFloat(timeline, "health");
                if (timeline != null && timelineHealth > 0)
                {
                    if (Get(timeline, "events") is System.Collections.IList evs)
                        foreach (var e in evs) events.Add((Get(e, "name")?.ToString(), timelineHealth * (1f - GetFloat(e, "percentage"))));
                    events.Sort((a, b) => a.at.CompareTo(b.at));
                    var evText = events.ConvertAll(e => $"{e.name}@{e.at:0}");
                    Console.WriteLine($"BOT timeline: health {timelineHealth:0}, events [{string.Join(", ", evText)}]");
                }
            }
            if (timeline != null && timelineHealth > 0)
            {
                float dmg = GetFloat(timeline, "damage");
                if (dmg != lastDamage) { lastDamage = dmg; lastDamageFrame = frame; }
                while (eventsReached < events.Count && dmg >= events[eventsReached].at)
                {
                    Console.WriteLine($"BOT event {events[eventsReached].name} at frame {frame} (damage {dmg:0.#}/{timelineHealth:0})");
                    eventsReached++;
                }
            }
            if (!wonLogged && levelType?.GetProperty("Won", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true)
            {
                wonLogged = true;
                Console.WriteLine($"BOT won at frame {frame}");
            }
            if (props == null) { StallCheck(); return; }
            float hp = GetFloat(props, "CurrentHealth");
            string name = Get(Get(props, "CurrentState"), "stateName")?.ToString();
            if (name != phase)
            {
                phase = name;
                if (!phasesReached.Contains(name)) phasesReached.Add(name);
                Console.WriteLine($"BOT phase {name} at frame {frame} (health {hp:0.#}/{totalHealth:0})");
            }
            if (hp != lastHealth) { lastHealth = hp; lastDamageFrame = frame; }
            StallCheck();
        }

        void StallCheck()
        {
            if (frame - lastDamageFrame != 2400 || wonLogged || (props == null && (timeline == null || timelineHealth <= 0 || !(lastDamage > 0)))) return;
            stalls++;
            Console.WriteLine($"BOT no damage for 40 s in phase {phase} at frame {frame} (health {lastHealth:0.#}/{totalHealth:0}, timeline {lastDamage:0.#}/{timelineHealth:0}, target {LastBossName})");
            // What could take damage now: each listened-to receiver, enabled, with a live collider, on screen.
            var cam = Camera.main;
            foreach (var o in Objects.Live())
                if (o is NBehaviour b && b.Managed != null && b.GameObject != null && TypeName(b.Managed) == "DamageReceiver" && Get(b.Managed, "OnDamageTaken") != null
                    && b.GameObject.Tag != "Player" && !b.GameObject.Name.Contains("Player"))
                {
                    var box = LiveColliderBox(b.GameObject.Transform, 3);
                    var q = box != null ? (Vector3)box.Value.center : b.GameObject.Transform.Position;
                    var vp = cam != null ? cam.WorldToViewportPoint(q) : Vector3.zero;
                    Console.WriteLine($"BOT   receiver {b.GameObject.Name}: active {b.GameObject.ActiveInHierarchy}, enabled {b.Enabled}, live collider {(box != null ? $"{box.Value.width:0}x{box.Value.height:0}" : "none")}, at ({q.x:0},{q.y:0}) viewport ({vp.x:0.00},{vp.y:0.00})");
                }
        }

        void FightSummary()
        {
            var missing = phaseList.FindAll(n => !phasesReached.Contains(n));
            Console.WriteLine($"BOT summary: phases {phasesReached.Count}/{phaseList.Count} [{string.Join(", ", phasesReached)}]"
                + (missing.Count > 0 ? $" missing [{string.Join(", ", missing)}]" : "")
                + (props != null ? $", health {lastHealth:0.#}/{totalHealth:0}" : "")
                + (timelineHealth > 0 ? $", damage {lastDamage:0.#}/{timelineHealth:0} ({100 * lastDamage / timelineHealth:0}%), events {eventsReached}/{events.Count}" : "")
                + $", targets [{string.Join(", ", targetsSeen)}]"
                + $", won {(wonLogged ? "yes" : "no")}, stalls {stalls}"
                + (fightAll ? $", weapons [{string.Join(", ", weaponsSeen)}], supers used {supersUsed} [{string.Join(", ", supersSeen)}], EX used {exUsed}, super presses {superPresses}, EX presses {exPresses}, parry tries {parryTries}, dashes {dashes}, switches {switches}" : ""));
        }

        /// <summary>The player's weapon and super, logged when they change.</summary>
        void TrackPlayer(NBehaviour pc)
        {
            foreach (var c in pc.GameObject.Components)
            {
                if (!(c is NBehaviour b) || b.Managed == null) continue;
                string tn = TypeName(b.Managed);
                if (tn != "LevelPlayerWeaponManager" && tn != "PlanePlayerWeaponManager") continue;
                string w = Get(b.Managed, "currentWeapon")?.ToString();
                if (w != lastWeapon)
                {
                    lastWeapon = w;
                    if (!weaponsSeen.Contains(w)) weaponsSeen.Add(w);
                    Console.WriteLine($"BOT weapon {w} at frame {frame}");
                }
                var sup = Get(b.Managed, "activeSuper");
                if (sup is UnityEngine.Object uo && uo == null) sup = null;
                if (sup != null && sup != lastSuper)
                {
                    superStarts++; wantSuper = false; exLeft = 3;
                    string sn = TypeName(sup);
                    if (!supersSeen.Contains(sn)) supersSeen.Add(sn);
                    Console.WriteLine($"BOT super {sn} at frame {frame}");
                }
                lastSuper = sup;
            }
        }

        // ---- forced progress ("fight N all force") ----
        bool fightForce;
        int forcePhaseFrames = 1500, forceStateIndex = -1, forceStateStart, forceKilledAt, forceBestFrame, forceLastPush, forceDoorAt, forceEndingSince, forceRetries;
        int forceEvents = -1, forceLastHit, forceNoTarget, forceIdlePushes;
        float forceBestX = float.MinValue, forcePathT;
        bool forceKillFailed, forceEndLogged;
        object forceLevel;

        void ResetForce()
        {
            forceStateIndex = -1; forceStateStart = frame; forceKilledAt = 0; forceBestFrame = frame; forceLastPush = 0; forceDoorAt = 0;
            forceBestX = float.MinValue; forcePathT = 0; forceKillFailed = forceEndLogged = false; forceEndingSince = 0; forceRetries = 0; forceLevel = null;
            forceEvents = -1; forceLastHit = 0; forceNoTarget = 0; forceIdlePushes = 0;
        }

        /// <summary>A render capture (with --capture-out) and a live-window shot (with --live) delay frames on.</summary>
        static void ScheduleCapture(int frameNow, int delay)
        {
            if (Capture.Path != null) Capture.Frames.Add(frameNow + delay);
            LiveHost.Shots.Add(frameNow + delay);
        }

        bool LevelEnding() => Get(trackedLevel, "Ending") is true;

        /// <summary>
        /// The forced sweep's progress: temporary pushes to reach everything a level holds, not a way to
        /// play it. Each of the boss's states gets forcePhaseFrames of the normal policy, then the boss is
        /// dealt the damage that crosses into its next state through the level's own
        /// LevelProperties.DealDamage (so its own phase handlers run), and after the last state the rest
        /// (its own win). A run-and-gun level that makes no progress for five seconds moves the player along
        /// its camera's path to the exit; a level that doesn't end that way (the tutorials, rooms) puts the
        /// player at its exit door and presses A. Each state is captured (with --capture-out) early and late.
        /// </summary>
        void ForceStep(InputFrame.Pad p, int t)
        {
            // A lost level the game restarts (the Mausoleum's urn): the next tries get shorter turns.
            if (trackedLevel != forceLevel)
            {
                if (forceLevel != null) { forceRetries++; Console.WriteLine($"BOT force: the level restarted (try {forceRetries + 1}) at frame {frame}"); }
                forceLevel = trackedLevel; forceStateIndex = -1; forceKilledAt = 0; forceKillFailed = forceEndLogged = false; forceEndingSince = 0;
            }
            int turn = forcePhaseFrames >> Math.Min(forceRetries, 3);
            if (forceKilledAt > 0 && !forceKillFailed && !LevelEnding() && frame - forceKilledAt > 900)
            {
                forceKillFailed = true;
                Console.WriteLine($"BOT force: the kill didn't end {trackedLevel?.GetType().Name} in 15 s; trying its exit");
            }
            if (LevelEnding() && !forceKillFailed)
            {
                if (!forceEndLogged) { forceEndLogged = true; forceEndingSince = frame; Console.WriteLine($"BOT force: level ending at frame {frame}"); ScheduleCapture(frame, 120); }
                // Ending without leaving (the tutorials end at their door): the exit.
                if (frame - forceEndingSince > 900) { forceKillFailed = true; Console.WriteLine($"BOT force: {trackedLevel?.GetType().Name} still here 15 s after ending; trying its exit"); }
                return;
            }
            if (props != null && !forceKillFailed)
            {
                int idx = Get(props, "stateIndex") is int i ? i : -1;
                // A new turn with each state, and with each of the death card's milestones (the sub-bosses of a
                // level whose health is one state: the Phantom Express, the Root Pack, the Baroness's candies).
                if (idx != forceStateIndex || eventsReached != forceEvents)
                {
                    forceStateIndex = idx; forceEvents = eventsReached; forceStateStart = frame; forceNoTarget = 0; forceIdlePushes = 0;
                    ScheduleCapture(frame, 150); ScheduleCapture(frame, Math.Max(300, turn - 120));
                }
                if (forceKilledAt > 0 || frame - forceStateStart < turn) return;
                // First through the part being fought (its own damage handler: sub-bosses die by their own code),
                // 3% of the boss's health every 20 frames; the level's properties only when nothing takes it.
                // A part that ignores the pushes (the Rum Runners' worm between its attacks) counts as nothing to hit
                // once 15 of them in a row moved neither the level's health nor the death card's damage (the
                // Phantom Express's parts have health of their own: its level health stays put).
                var rcv = LastBossReceiver;
                if (forceIdlePushes < 15 && rcv != null && !rcv.Destroyed && rcv.Enabled && rcv.GameObject != null && rcv.GameObject.ActiveInHierarchy && Get(rcv.Managed, "OnDamageTaken") != null)
                {
                    forceNoTarget = 0;
                    if (frame - forceLastHit < 20) return;
                    forceIdlePushes = lastDamageFrame <= forceLastHit && frame - forceStateStart - turn >= 20 ? forceIdlePushes + 1 : 0;
                    forceLastHit = frame;
                    if (forceIdlePushes == 15) Console.WriteLine($"BOT force: {rcv.GameObject.Name} took 15 pushes without any damage counting; the level's own damage from here");
                    float amount = Math.Max(1f, GetFloat(props, "TotalHealth") * 0.03f);
                    if ((frame - forceStateStart - turn) < 20)
                        Console.WriteLine($"BOT force: state {idx} ({phase}), milestone {eventsReached}, played {turn} frames; pushing {rcv.GameObject.Name} {amount:0.#} every 20 frames from frame {frame}");
                    var infoType = GameType("DamageDealer+DamageInfo");
                    var ps = infoType?.GetConstructors()[0].GetParameters();
                    if (ps != null && ps.Length == 4)
                    {
                        var info = Activator.CreateInstance(infoType, amount, Enum.ToObject(ps[1].ParameterType, 0), (Vector2)rcv.GameObject.Transform.Position, Enum.ToObject(ps[3].ParameterType, 0));
                        rcv.Managed.GetType().GetMethod("TakeDamage")?.Invoke(rcv.Managed, new[] { info });
                    }
                    return;
                }
                if (forceNoTarget == 0) forceNoTarget = frame;
                if (frame - forceNoTarget < 600) return;
                float total = GetFloat(props, "TotalHealth"), hp = GetFloat(props, "CurrentHealth");
                float trig = props.GetType().GetMethod("GetNextStateHealthTrigger")?.Invoke(props, null) is float f ? f : 0f;
                float dmg = trig > 0 ? hp - trig * total + Math.Max(1f, total * 0.002f) : hp + 1f;
                if (dmg <= 0) dmg = 1f;
                Console.WriteLine($"BOT force: state {idx} ({phase}) played {frame - forceStateStart} frames; dealing {dmg:0.#} at frame {frame} (health {hp:0.#}/{total:0})" + (trig > 0 ? "" : ": the last state, the kill"));
                props.GetType().GetMethod("DealDamage")?.Invoke(props, new object[] { dmg });
                if (trig <= 0) { forceKilledAt = frame; ScheduleCapture(frame, 60); ScheduleCapture(frame, 400); }
                forceStateStart = frame;   // a state that doesn't change gets another full turn before the next push
                return;
            }
            var pc = PlayerController(LiveNow());
            if (pc == null) return;
            var tr = ((Component)pc.Managed).transform;
            if (trackedLevel != null && trackedLevel.GetType().Name.StartsWith("PlatformingLevel")) { ForceRun(tr); return; }
            ForceDoor(p, t, tr);
        }

        /// <summary>A run-and-gun level: after five seconds without progress, the player a step further along its camera's path; at its end, past the exit.</summary>
        void ForceRun(Transform tr)
        {
            var me = tr.position;
            if (me.x > forceBestX + 40f) { forceBestX = me.x; forceBestFrame = frame; }
            if (frame - forceBestFrame < 300 || frame - forceLastPush < 300) return;
            forceLastPush = frame;
            var cam = GameType("CupheadLevelCamera")?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var path = Get(cam, "path");
            var lerp = path?.GetType().GetMethod("Lerp", new[] { typeof(float) });
            Vector3 target;
            if (lerp != null && forcePathT < 1f)
            {
                // Where along the path the player is (the nearest of 64 samples), then a step on.
                float bestT = 0, bd = float.MaxValue;
                for (int k = 0; k <= 64; k++)
                {
                    var q = (Vector3)lerp.Invoke(path, new object[] { k / 64f });
                    float d = (q.x - me.x) * (q.x - me.x) + (q.y - me.y) * (q.y - me.y);
                    if (d < bd) { bd = d; bestT = k / 64f; }
                }
                forcePathT = Math.Min(1f, Math.Max(forcePathT, bestT) + 0.06f);
                target = (Vector3)lerp.Invoke(path, new object[] { forcePathT });
            }
            else
            {
                Vector3? exit = null; float dist = 500f;
                foreach (var o in LiveNow())
                    if (o is NBehaviour b && b.Managed != null && TypeName(b.Managed) == "PlatformingLevelExit")
                    { exit = b.GameObject.Transform.Position; dist = GetFloat(b.Managed, "_exitDistance"); }
                if (exit == null) { Console.WriteLine($"BOT force: no camera path or exit at frame {frame}"); return; }
                target = exit.Value + new Vector3(dist + 40f, 60f, 0);
            }
            Console.WriteLine($"BOT force: no progress for 5 s at ({me.x:0},{me.y:0}); moved to ({target.x:0},{target.y:0}) (path {forcePathT:0.00}) at frame {frame}");
            tr.position = new Vector3(target.x, target.y, me.z);
            forceBestX = target.x; forceBestFrame = frame;
            ScheduleCapture(frame, 90);
        }

        static readonly string[] exitTypes = { "TutorialLevelDoor", "HouseLevelExit", "KitchenLevelExit", "ChessCastleLevelExit", "PlatformingLevelExit" };

        /// <summary>A level without a boss to finish (the tutorials, rooms): after its turn, the player at its exit and A pressed.</summary>
        void ForceDoor(InputFrame.Pad p, int t, Transform tr)
        {
            if (t < forcePhaseFrames && !forceKillFailed) return;
            Vector3? door = null; string which = null;
            foreach (var o in LiveNow())
                if (o is NBehaviour b && b.Managed != null && b.GameObject.ActiveInHierarchy && Array.IndexOf(exitTypes, TypeName(b.Managed)) >= 0)
                { door = b.GameObject.Transform.Position; which = TypeName(b.Managed); break; }
            if (door == null) return;
            if (forceDoorAt == 0 || frame - forceDoorAt > 600)
            {
                forceDoorAt = frame;
                Console.WriteLine($"BOT force: at the exit ({which}, {door.Value.x:0},{door.Value.y:0}) at frame {frame}");
                tr.position = new Vector3(door.Value.x, door.Value.y + 10f, tr.position.z);
                ScheduleCapture(frame, 60);
            }
            if ((frame - forceDoorAt) % 40 < 6) p.Buttons |= PadButton.A;
            else if ((frame - forceDoorAt) % 40 < 12) p.Buttons |= PadButton.DPadUp;
        }

        static object Stats(NBehaviour pc) => Get(pc.Managed, "stats");

        static NBehaviour PlayerController(List<NObject> live)
        {
            foreach (var o in live)
                if (o is NBehaviour b && b.GameObject != null && b.GameObject.ActiveInHierarchy && Array.IndexOf(playerTypes, TypeName(b.Managed)) >= 0)
                    return b;
            return null;
        }

        static readonly Dictionary<Type, int> parryKinds = new Dictionary<Type, int>();

        /// <summary>What the parry effect can parry (AbstractParryEffect.OnCollision): pink projectiles, parry switches, parryable entities.</summary>
        static bool Parryable(NBehaviour b)
        {
            var t = b.Managed.GetType();
            if (!parryKinds.TryGetValue(t, out int kind))
            {
                kind = GameType("AbstractProjectile")?.IsAssignableFrom(t) == true ? 1 : GameType("ParrySwitch")?.IsAssignableFrom(t) == true ? 2
                    : GameType("AbstractLevelEntity")?.IsAssignableFrom(t) == true ? 3 : 0;
                parryKinds[t] = kind;
            }
            switch (kind)
            {
                case 1: return Get(b.Managed, "CanParry") is true;
                case 2: return b.Enabled && Get(b.Managed, "IsParryable") is true;
                case 3: return b.Enabled && Get(b.Managed, "canParry") is true;
                default: return false;
            }
        }

        static Vector3 HitCentre(NGameObject go)
        {
            foreach (var c in go.Components)
                if (c is NCollider2D col && col.Live) { col.Shapes(); return new Vector3(col.AABB.center.x, col.AABB.center.y, 0); }
            var p = go.Transform.Position;
            return new Vector3(p.x, p.y, 0);
        }

        /// <summary>The nearest parryable thing to a point within a radius, or null.</summary>
        static Vector3? NearestParryable(List<NObject> live, Vector3 from, float radius)
        {
            Vector3? best = null; float bd = radius * radius;
            foreach (var o in live)
                if (o is NBehaviour b && b.Managed != null && b.GameObject != null && b.GameObject.ActiveInHierarchy && Parryable(b))
                {
                    // Something the parry can touch: a live collider (a parried ghost keeps its flag, not its collider).
                    var box = LiveColliderBox(b.GameObject.Transform, 2);
                    if (box == null) continue;
                    var q = (Vector3)box.Value.center;
                    float d = (q - from).sqrMagnitude;
                    if (d < bd) { bd = d; best = q; }
                }
            return best;
        }

        /// <summary>
        /// The nearest boss part shots can damage: an enabled DamageReceiver (not the player's) on an active
        /// GameObject with a live collider on it or a child. A phase's parts that are shown but not hittable
        /// (the Devil's first body during his giant head) only when nothing hittable is there.
        /// </summary>
        static NBehaviour LastBossReceiver;

        static Vector3? HittableBoss(Vector3 from, List<NObject> live)
        {
            Vector3? best = null; float bd = float.MaxValue;
            string bestName = null;
            NBehaviour bestReceiver = null;
            // Parts off the screen (a later phase's, placed and waiting) count as far away.
            var cam = Camera.main;
            float OffScreen(Vector3 q)
            {
                if (cam == null) return 0f;
                var vp = cam.WorldToViewportPoint(q);
                return vp.x < -0.02f || vp.x > 1.02f || vp.y < -0.02f || vp.y > 1.02f ? 1e8f : 0f;
            }
            foreach (var o in live)
            {
                if (!(o is NBehaviour b) || !b.Enabled || b.GameObject == null || !b.GameObject.ActiveInHierarchy || TypeName(b.Managed) != "DamageReceiver") continue;
                var go = b.GameObject;
                if (go.Tag == "Player" || go.Name.Contains("Player")) continue;
                // DamageReceiver.TakeDamage does nothing without a listener (Beppi's balloon body: his head takes it).
                if (Get(b.Managed, "OnDamageTaken") == null) continue;
                var hit = LiveColliderBox(go.Transform, 3);
                var q = hit != null ? (Vector3)hit.Value.center : go.Transform.Position;
                // On the screen first, then hittable, then the level's own parts before spawned minions (clones:
                // the Robot's bomb bots, the Bee's invincible triangles), then the biggest, then nearest.
                float area = hit != null ? Math.Min(hit.Value.width * hit.Value.height, 4e6f) : 0f;
                float d = OffScreen(q) + (hit != null ? 0f : 1e7f) + (go.Name.EndsWith("(Clone)") ? 5e6f : 0f) - area + 0.01f * (q - from).magnitude;
                if (d < bd) { bd = d; best = q; bestName = go.Name; bestReceiver = b; }
            }
            if (best != null) LastBossName = bestName;
            LastBossReceiver = bestReceiver;
            return best;
        }

        /// <summary>The box of the first live collider on a transform or below it (a few levels down).</summary>
        static Rect? LiveColliderBox(NTransform t, int depth)
        {
            foreach (var c in t.GameObject.Components)
                if (c is NCollider2D col && col.Live) { col.Shapes(); return col.AABB; }
            if (depth > 0)
                foreach (var child in t.Children)
                    if (child.GameObject.ActiveInHierarchy && LiveColliderBox(child, depth - 1) is Rect r) return r;
            return null;
        }

        // ---- the policy ----
        bool parryArm;
        int parryDeadline, pressAUntil, holdAUntil;
        float runBest; int runLastProgress, runBackUntil;

        /// <summary>Enemy shots that will pass close in the next 40 frames: where the closest one passes, whether it comes along the ground.</summary>
        void Threats(List<NObject> live, Vector3 body, Vector3? boss, float near, out bool threat, out bool jump, out Vector2 away)
        {
            threat = jump = false; away = Vector2.zero;
            var seen = new Dictionary<NGameObject, Vector3>();
            foreach (var o in live)
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
                if (best > near) continue;
                threat = true;
                var at = rel + v * when;
                if (Math.Abs(v.y) < Math.Abs(v.x) * 0.5f && at.y < 70 && when < 22) jump = true;
                var a = new Vector2(-at.x, -at.y);
                away += a.sqrMagnitude > 1e-3f ? a.normalized : new Vector2(1, 0);
            }
            lastPos.Clear();
            foreach (var kv in seen) lastPos[kv.Key] = kv.Value;
        }

        void FightAll(InputFrame.Pad p, int t)
        {
            var live = LiveNow();
            var pc = PlayerController(live);
            Vector3? me = pc?.GameObject.Transform.Position;
            float health = pc != null ? GetFloat(Stats(pc), "Health") : float.NaN;
            bool alive = me != null && (float.IsNaN(health) ? me.Value.y <= 1500 && PlayerAlive(live) : health > 0);
            // The game over card is up without the player dying too (the Mausoleum's urn broken).
            foreach (var o in live)
                if (o is NBehaviour g && g.GameObject != null && g.GameObject.ActiveInHierarchy && g.Enabled && TypeName(g.Managed) == "LevelGameOverGUI") { alive = false; break; }
            if (!alive && wasAlive) Console.WriteLine($"BOT knocked out at frame {frame} fighting {LastBossName ?? "?"}");
            if (alive && !wasAlive) Console.WriteLine($"BOT attempt starts at frame {frame}");
            wasAlive = alive;
            if (!alive) { if (t % 90 < 6) p.Buttons |= PadButton.A; return; }
            TrackPlayer(pc);
            TrackMeter(pc);
            var level = GameType("Level")?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (TypeName(pc.Managed) == "PlanePlayerController") PlanePolicy(p, t, live, pc, me.Value);
            else if (level != null && GameType("PlatformingLevel")?.IsInstanceOfType(level) == true) RunPolicy(p, t, live, pc, me.Value);
            else GroundPolicy(p, t, live, pc, me.Value, level);
            if (LastBossName != null && !targetsSeen.Contains(LastBossName)) { targetsSeen.Add(LastBossName); Console.WriteLine($"BOT target {LastBossName} at frame {frame}"); }
            // A presses the policies asked for: a held jump, then a separate press (a parry in the air).
            if (t < holdAUntil || t < pressAUntil) p.Buttons |= PadButton.A;
        }

        /// <summary>EX shots and supers: save five cards for a super, then spend three as EX shots, and again.</summary>
        void SpecialMoves(InputFrame.Pad p, int t, NBehaviour pc, bool busy)
        {
            if (busy || t % 30 >= 4) return;
            var stats = Stats(pc);
            float meter = GetFloat(stats, "SuperMeter"), max = GetFloat(stats, "SuperMeterMax");
            if (wantSuper && meter >= max && max > 0)
            {
                p.Buttons |= PadButton.B;
                if (t % 30 == 0) superPresses++;
            }
            else if (!wantSuper && meter >= 10f && t % 120 < 4)
            {
                p.Buttons |= PadButton.B;
                if (t % 30 == 0) exPresses++;
            }
        }

        bool wantSuper = true;
        int exLeft;
        float lastMeter = float.NaN;

        /// <summary>Supers and EX shots as the meter spends them (any player kind): a full meter emptied, or one card.</summary>
        void TrackMeter(NBehaviour pc)
        {
            var stats = Stats(pc);
            float meter = GetFloat(stats, "SuperMeter"), max = GetFloat(stats, "SuperMeterMax");
            if (float.IsNaN(meter)) return;
            if (!float.IsNaN(lastMeter) && meter < lastMeter - 0.5f)
            {
                if (lastMeter >= max - 0.5f && meter <= 0.5f && wantSuper)
                {
                    supersUsed++; wantSuper = false; exLeft = 3;
                    Console.WriteLine($"BOT super used at frame {frame} (meter {lastMeter:0} -> {meter:0})");
                }
                else if (lastMeter - meter >= 9.5f)
                {
                    exUsed++;
                    if (!wantSuper && --exLeft <= 0) wantSuper = true;
                }
            }
            lastMeter = meter;
        }
        int supersUsed, exUsed;

        /// <summary>Starts a parry: a jump now, a second press once the jump button is up and the target is close.</summary>
        void StartParry(int t)
        {
            if (parryArm || t < holdAUntil + 20) return;
            holdAUntil = t + 8; parryArm = true; parryDeadline = t + 45;
            parryTries++;
        }

        void ArmedParry(int t, Vector3 body, Vector3? target)
        {
            if (!parryArm) return;
            if (t > parryDeadline) { parryArm = false; return; }
            if (t < holdAUntil + 2) return;
            if (target != null && (target.Value - body).magnitude < 120f) { pressAUntil = t + 4; parryArm = false; }
        }

        void GroundPolicy(InputFrame.Pad p, int t, List<NObject> live, NBehaviour pc, Vector3 pos, object level)
        {
            p.Buttons |= PadButton.X;
            var body = pos + new Vector3(0, 60, 0);
            var boss = HittableBoss(pos, live);
            string lname = level?.GetType().Name ?? "";
            bool parryLevel = lname.StartsWith("Chess") || lname.StartsWith("Mausoleum");
            var pink = NearestParryable(live, body, parryLevel ? 900f : 420f);
            if (traceBot && t % 60 == 0)
            {
                int np = 0, nproj = 0;
                foreach (var o in live) if (o is NBehaviour b && b.Managed != null && b.GameObject != null && b.GameObject.ActiveInHierarchy) { if (Parryable(b)) np++; if (GameType("AbstractProjectile").IsInstanceOfType(b.Managed)) nproj++; }
                Console.WriteLine($"BOT trace {frame}: parryable {np}, projectiles {nproj}, pink {pink}");
            }
            ArmedParry(t, body, pink);
            float dir = 0; const float edge = 500f;
            if (boss != null && !parryLevel)
            {
                float dx = boss.Value.x - pos.x, adx = Math.Abs(dx);
                dir = adx < 300 ? -Math.Sign(dx) : adx > 500 ? Math.Sign(dx) : 0;
            }
            Threats(live, body, boss, 110f, out bool threat, out bool jump, out var away);
            if (threat && pink != null && (pink.Value - body).magnitude < 160f) { StartParry(t); jump = false; }
            else if (threat && !jump) dir = Math.Sign(away.x);
            // Parry targets: go under them (in King's Leap and the Mausoleum parries are the only damage).
            if (!threat && pink != null && (parryLevel || (t / 600) % 3 == 2))
            {
                float pdx = pink.Value.x - pos.x;
                dir = Math.Abs(pdx) > 40 ? Math.Sign(pdx) : 0;
                if (Math.Abs(pdx) < 140 && pink.Value.y - body.y < 320 && pink.Value.y - body.y > -80) StartParry(t);
            }
            // Every hittable part below the screen (the Devil's pit after his first phase, a "GO" sign over the
            // hole): walk to the middle and drop, no jumping.
            bool below = false;
            if (boss != null && Camera.main != null)
            {
                var vp = Camera.main.WorldToViewportPoint(boss.Value);
                if (vp.y < -0.1f) { below = true; var cx = Camera.main.transform.position.x; dir = Math.Abs(cx - pos.x) > 30 ? Math.Sign(cx - pos.x) : 0; }
            }
            if (pos.x < -edge && dir < 0) dir = 0;
            if (pos.x > edge && dir > 0) dir = 0;
            if (threat && dir != 0 && ((pos.x < -edge + 40 && dir < 0) || (pos.x > edge - 40 && dir > 0)) && dashCooldown == 0) { dir = -dir; dashUntil = t + 4; dashCooldown = 90; dashes++; }
            p.LX = dir;
            // Standing in range of the boss: lock (RB) and aim at its hit area in eight directions.
            if (boss != null && dir == 0 && !threat && !parryLevel)
            {
                var d = new Vector2(boss.Value.x - body.x, boss.Value.y - body.y);
                float ang = (float)Math.Atan2(d.y, d.x);
                int oct = (int)Math.Round(ang / (Math.PI / 4));
                p.Buttons |= PadButton.RightShoulder;
                p.LX = (float)Math.Round(Math.Cos(oct * Math.PI / 4));
                p.LY = (float)Math.Round(Math.Sin(oct * Math.PI / 4));
            }
            if (jump && t >= jumpCooldown && !below) { holdAUntil = t + 16; jumpCooldown = t + 34; }
            if (dashCooldown > 0) dashCooldown--;
            // A dash now and then (the dash charms), away from the nearest shot or towards the middle.
            if (!threat && t % 420 == 210 && dashCooldown == 0) { dashUntil = t + 4; dashCooldown = 90; dashes++; if (p.LX == 0) p.LX = pos.x > 0 ? -1 : 1; }
            if (t < dashUntil) p.Buttons |= PadButton.Y;
            // The other weapon now and then.
            if (!threat && t % 900 == 450) switches++;
            if (!threat && t % 900 >= 450 && t % 900 < 454) p.Buttons |= SwitchButton;
            SpecialMoves(p, t, pc, threat || parryArm);
        }

        void PlanePolicy(InputFrame.Pad p, int t, List<NObject> live, NBehaviour pc, Vector3 pos)
        {
            p.Buttons |= PadButton.X;
            var boss = HittableBoss(pos, live);
            // Level coordinates are pixels around the camera: stay on the left, level with the boss.
            var cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var goal = boss != null ? new Vector2(Math.Max(cam.x - 560, Math.Min(boss.Value.x - 520, cam.x + 200)), boss.Value.y) : new Vector2(cam.x - 400, cam.y);
            var move = new Vector2(goal.x - pos.x, goal.y - pos.y) / 120f;
            Threats(live, pos, boss, 120f, out bool threat, out _, out var away);
            if (threat) move = away.normalized * 1.5f + move * 0.3f;
            if (move.magnitude > 1) move.Normalize();
            p.LX = move.x; p.LY = move.y;
            var pink = NearestParryable(live, pos, 90f);
            if (pink != null && t % 20 < 4) { p.Buttons |= PadButton.A; if (t % 20 == 0) parryTries++; }
            if (t % 900 >= 450 && t % 900 < 454 && !threat) { p.Buttons |= SwitchButton; if (t % 900 == 450) switches++; }
            SpecialMoves(p, t, pc, threat);
        }

        void RunPolicy(InputFrame.Pad p, int t, List<NObject> live, NBehaviour pc, Vector3 pos)
        {
            p.Buttons |= PadButton.X;
            var body = pos + new Vector3(0, 60, 0);
            if (pos.x > runBest + 4f) { runBest = pos.x; runLastProgress = t; }
            if (t % 600 == 0) Console.WriteLine($"BOT run at frame {frame}: x {pos.x:0}, furthest {runBest:0}");
            // Stuck a long time: back off a little, then run again.
            if (t - runLastProgress > 480 && runBackUntil < t) { runBackUntil = t + 50; runLastProgress = t; }
            p.LX = t < runBackUntil ? -1 : 1;
            Threats(live, body, null, 150f, out bool threat, out bool jump, out _);
            var pink = NearestParryable(live, body, 300f);
            ArmedParry(t, body, pink);
            if (pink != null && Math.Abs(pink.Value.x - pos.x) < 120 && pink.Value.y > body.y - 40) StartParry(t);
            // Jump when blocked, when a shot or enemy comes along the ground, and every so often (gaps).
            bool blocked = t - runLastProgress > 20;
            if ((jump || blocked || t % 96 == 0) && t >= jumpCooldown && !parryArm) { holdAUntil = t + 18; jumpCooldown = t + 36; }
            // Enemies above and ahead: shoot diagonally up while running.
            foreach (var o in live)
                if (o is NGameObject g && g.ActiveInHierarchy && g.Tag == "Enemy")
                {
                    var q = g.Transform.Position;
                    if (q.x > pos.x && q.x - pos.x < 500 && q.y - body.y > 120) { p.LY = 1; break; }
                }
            if (dashCooldown > 0) dashCooldown--;
            if (blocked && t - runLastProgress > 60 && dashCooldown == 0) { dashUntil = t + 4; dashCooldown = 120; dashes++; }
            if (t < dashUntil) p.Buttons |= PadButton.Y;
            if (!threat && t % 900 >= 450 && t % 900 < 454) { p.Buttons |= SwitchButton; if (t % 900 == 450) switches++; }
            SpecialMoves(p, t, pc, threat || parryArm);
        }

        /// <summary>Switch Weapon on the game's gamepad map.</summary>
        static PadButton SwitchButton = PadButton.LeftShoulder;
    }
}
