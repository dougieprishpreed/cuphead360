using System;
using System.Collections;
using System.Collections.Generic;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    // Unity lifecycle semantics the engine must reproduce, checked through the
    // real (patched) UnityEngine assemblies. Each expectation is Unity's
    // documented behaviour.
    public class ProbeBehaviour : MonoBehaviour
    {
        public static readonly List<string> Events = new List<string>();
        public int value = 1;                 // serialized
        [SerializeField] int hidden = 2;      // serialized
        [NonSerialized] public int transientValue = 3;
        public ProbeBehaviour partner;        // reference inside the hierarchy
        public Transform childRef;
        public int updates, fixedUpdates;
        public int Hidden { get => hidden; set => hidden = value; }

        void Awake() => Events.Add("Awake " + name);
        void OnEnable() => Events.Add("OnEnable " + name);
        void Start() => Events.Add("Start " + name);
        void Update() { updates++; if (updates == 1) Events.Add("Update " + name); }
        void FixedUpdate() => fixedUpdates++;
        void OnDisable() => Events.Add("OnDisable " + name);
        void OnDestroy() => Events.Add("OnDestroy " + name);
        void Ping(int x) => Events.Add("Ping " + x);
    }

    public class RoutineBehaviour : MonoBehaviour
    {
        public static readonly List<string> Log = new List<string>();
        IEnumerator Start()
        {
            Log.Add("start f" + Time.frameCount);
            yield return null;
            Log.Add("after null f" + Time.frameCount);
            yield return new WaitForSeconds(0.5f);
            Log.Add("after wait t" + Time.time.ToString("0.00"));
            yield return StartCoroutine(Inner());
            Log.Add("after inner f" + Time.frameCount);
        }
        IEnumerator Inner()
        {
            Log.Add("inner f" + Time.frameCount);
            yield return null;
            yield return null;
            Log.Add("inner end f" + Time.frameCount);
        }
        public IEnumerator Forever() { while (true) { Log.Add("tick f" + Time.frameCount); yield return null; } }
    }

    internal static class SelfTest
    {
        static int fails, passes;
        static void Check(bool ok, string what)
        {
            if (ok) passes++; else fails++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        }

        const float Dt = 1f / 60f;
        static void Frames(int n) { for (int i = 0; i < n; i++) PlayerLoop.Frame(Dt); }

        public static int Run()
        {
            Cuphead360.Engine.Scenes.Create("selftest", 0);
            var ev = ProbeBehaviour.Events;

            // Awake + OnEnable inside AddComponent; Start before the first Update.
            var a = new GameObject("A");
            var pa = a.AddComponent<ProbeBehaviour>();
            Check(string.Join(",", ev) == "Awake A,OnEnable A", "AddComponent runs Awake then OnEnable immediately: " + string.Join(",", ev));
            ev.Clear();
            Frames(1);
            Check(string.Join(",", ev) == "Start A,Update A", "Start precedes the first Update: " + string.Join(",", ev));
            Check(a.GetComponent<ProbeBehaviour>() == pa && a.GetComponent<Rigidbody2D>() == null, "GetComponent<T> fast path");
            Check(pa.GetInstanceID() != 0 && pa.GetInstanceID() != a.GetInstanceID(), "component and GameObject have distinct instance ids");

            // Deactivation: OnDisable; reactivation: OnEnable, no second Awake/Start.
            ev.Clear();
            a.SetActive(false);
            int u = pa.updates;
            Frames(2);
            a.SetActive(true);
            Frames(1);
            Check(string.Join(",", ev) == "OnDisable A,OnEnable A" && pa.updates == u + 1, "SetActive(false/true): " + string.Join(",", ev));

            // Fixed steps: 0.02 s fixed step at 60 fps -> 50 per second.
            int f0 = pa.fixedUpdates;
            Frames(60);
            Check(Math.Abs(pa.fixedUpdates - f0 - 50) <= 1, "FixedUpdate runs 50 times per second: " + (pa.fixedUpdates - f0));

            // Transforms.
            var parent = new GameObject("P").transform;
            var child = new GameObject("C").transform;
            parent.position = new Vector3(10, 0, 0);
            parent.localScale = new Vector3(2, 2, 1);
            child.SetParent(parent, false);
            child.localPosition = new Vector3(1, 1, 0);
            Check(Near(child.position, new Vector3(12, 2, 0)), "child world position through parent TRS: " + child.position);
            parent.rotation = Quaternion.Euler(0, 0, 90);
            Check(Near(child.position, new Vector3(8, 2, 0)), "parent rotation moves the child: " + child.position);
            child.SetParent(null, true);
            Check(Near(child.position, new Vector3(8, 2, 0)) && child.parent == null, "SetParent(null, true) keeps the world position");
            Check(Near(Quaternion.Euler(30, 45, 60).eulerAngles, new Vector3(30, 45, 60)), "Euler round trip: " + Quaternion.Euler(30, 45, 60).eulerAngles);
            Check(parent.Find("C") == null && child.root == child, "Find/root after reparenting");

            // Coroutines.
            var r = new GameObject("R").AddComponent<RoutineBehaviour>();
            int start = Time.frameCount;
            Frames(70);
            var log = string.Join(" | ", RoutineBehaviour.Log);
            Console.WriteLine("  routine: " + log);
            Check(RoutineBehaviour.Log.Count == 6 && RoutineBehaviour.Log[0] == "start f" + (start + 1) && RoutineBehaviour.Log[1] == "after null f" + (start + 2),
                  "IEnumerator Start: runs to first yield in Start, yield null resumes next frame");
            Check(RoutineBehaviour.Log[3] == RoutineBehaviour.Log[3] && RoutineBehaviour.Log[4].StartsWith("inner end"), "nested StartCoroutine yields until the inner routine ends");
            int before = RoutineBehaviour.Log.Count;
            var forever = r.StartCoroutine(r.Forever());
            int ticks = RoutineBehaviour.Log.Count;
            Check(ticks == before + 1, "StartCoroutine runs the body to its first yield at once");
            Frames(3);
            r.StopCoroutine(forever);
            Frames(3);
            Check(RoutineBehaviour.Log.Count - ticks == 3, "a yield-null loop resumes once per frame until StopCoroutine: " + (RoutineBehaviour.Log.Count - ticks));
            r.StartCoroutine(r.Forever());
            ticks = RoutineBehaviour.Log.Count;
            r.gameObject.SetActive(false);
            r.gameObject.SetActive(true);
            Frames(3);
            Check(RoutineBehaviour.Log.Count == ticks, "deactivating a GameObject stops its coroutines");

            // SendMessage.
            ev.Clear();
            a.SendMessage("Ping", 7);
            Check(string.Join(",", ev) == "Ping 7", "SendMessage with an argument");

            // Instantiate: serialized fields copied, references inside remapped, Awake on the copy.
            var tmpl = new GameObject("T");
            var tc = new GameObject("TC");
            tc.transform.SetParent(tmpl.transform, false);
            var tp = tmpl.AddComponent<ProbeBehaviour>();
            tp.value = 42; tp.Hidden = 43; tp.transientValue = 44; tp.partner = tp; tp.childRef = tc.transform;
            ev.Clear();
            var copy = UnityEngine.Object.Instantiate(tp, new Vector3(5, 6, 0), Quaternion.identity);
            Check(copy != tp && copy.name == "T(Clone)" && copy.value == 42 && copy.Hidden == 43 && copy.transientValue == 3,
                  "Instantiate copies serialized fields only");
            Check(copy.partner == copy && copy.childRef != tc.transform && copy.childRef.parent == copy.transform,
                  "Instantiate remaps references inside the copied hierarchy");
            Check(string.Join(",", ev) == "Awake T(Clone),OnEnable T(Clone)" && Near(copy.transform.position, new Vector3(5, 6, 0)),
                  "the copy is awake at the given position: " + string.Join(",", ev));

            // Destroy: deferred to the end of the frame; then fake null.
            ev.Clear();
            UnityEngine.Object.Destroy(a);
            Check(a != null && ev.Count == 0, "Destroy is deferred");
            Frames(1);
            Check(a == null && pa == null && (object)pa != null && pa.GetInstanceID() == 0, "destroyed objects compare equal to null");
            var aev = ev.FindAll(e => e.EndsWith(" A"));
            Check(string.Join(",", aev) == "OnDisable A,OnDestroy A", "Destroy calls OnDisable then OnDestroy: " + string.Join(",", aev));

            Console.WriteLine($"SELFTEST {(fails == 0 ? "PASS" : "FAIL")}: {passes} passed, {fails} failed; missing engine methods reached: {Icall.MissingCount}");
            foreach (var kv in Icall.Report()) Console.WriteLine("  missing " + kv.Key + " x" + kv.Value);
            return fails == 0 ? 0 : 1;
        }

        static bool Near(Vector3 a, Vector3 b) => (a - b).magnitude < 1e-3f;
    }
}
