using System;
using System.Collections.Generic;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Ledger of engine methods the game reached that the engine does not
    /// implement yet. A missing method returns default(T) and is recorded once
    /// per id with a hit count, so a run reports exactly what it depended on.
    /// </summary>
    public static class Icall
    {
        static readonly Dictionary<string, int> hits = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly List<string> order = new List<string>();

        /// <summary>When set, the first reach of a missing method throws instead of returning default.</summary>
        public static bool Strict;

        public static void Missing(string id)
        {
            if (hits.TryGetValue(id, out int n)) { hits[id] = n + 1; return; }
            hits[id] = 1;
            order.Add(id);
            Log.Line("MISSING " + id);
            if (Strict) throw new NotImplementedException("engine method not implemented: " + id);
        }

        public static IEnumerable<KeyValuePair<string, int>> Report()
        {
            foreach (var id in order) yield return new KeyValuePair<string, int>(id, hits[id]);
        }

        public static int MissingCount => order.Count;
    }

    public static class Log
    {
        public static Action<string> Sink = Console.WriteLine;
        public static void Line(string s) => Sink(s);
    }
}
