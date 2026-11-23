using System.Text;

namespace CarCareTracker.Canary
{
    /// <summary>
    /// Runs the health checks of a vehicle profile over recorded data. A port of the device's
    /// monitor engine (canary_core/src/monitors.c): same half-second tick, same gate, settle time,
    /// statistics and reservoir sampling, so a drive evaluated here agrees with what the device
    /// showed on its own screen. What the server adds is the ability to merge many drives into a week.
    /// </summary>
    public sealed class CanaryCheckEngine
    {
        public const double TickS = 0.5;

        private sealed class Running
        {
            public CanaryCheck Check = new();
            public CanaryExpr? When;
            public CanaryExpr? Value;
            public string? Missing;
            public CanaryCheckState State = new();
            public float[] Res = new float[CanaryCheckState.Reservoir];
            public long GateRun;
            public bool PrevTrue;
            public uint Rng;
        }

        private readonly List<Running> _checks = new();

        /// <summary>
        /// Prepares the checks for one log. <paramref name="resolve"/> maps a signal id to its column
        /// in the values array; a check that needs a signal the log does not have is reported as
        /// unavailable instead of failing.
        /// </summary>
        public CanaryCheckEngine(IEnumerable<CanaryCheck> checks, Func<string, int> resolve)
        {
            foreach (var check in checks)
            {
                var running = new Running { Check = check, Rng = Seed(check.Id) };
                string? missing = null;
                int Resolve(string name)
                {
                    int index = resolve(name);
                    if (index < 0 && missing == null)
                    {
                        missing = name;
                    }
                    return index;
                }
                if (!string.IsNullOrWhiteSpace(check.When))
                {
                    running.When = CanaryExpr.Compile(check.When, Resolve, out _);
                    if (running.When == null)
                    {
                        running.Missing = missing ?? "its conditions";
                    }
                }
                if (running.Missing == null)
                {
                    running.Value = CanaryExpr.Compile(check.Value, Resolve, out _);
                    if (running.Value == null)
                    {
                        running.Missing = missing ?? "its value";
                    }
                }
                _checks.Add(running);
            }
        }

        /// <summary>FNV-1a of the id with the low bit set: the same seed the device uses.</summary>
        private static uint Seed(string id)
        {
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(id))
            {
                h ^= b;
                h *= 16777619u;
            }
            return h | 1u;
        }

        /// <summary>One half-second step. <paramref name="values"/> holds the current reading of every signal, NaN when stale.</summary>
        public void Tick(double[] values, double tripTimeS)
        {
            foreach (var r in _checks)
            {
                if (r.Missing != null || r.Value == null)
                {
                    continue;
                }
                double gate = r.When != null ? r.When.Eval(values) : 1.0;
                if (double.IsNaN(gate) || gate == 0)
                {
                    r.PrevTrue = false;
                    r.GateRun = 0;
                    continue;
                }
                if (++r.GateRun * TickS <= r.Check.SettleS)
                {
                    continue; /* still settling */
                }
                double v = r.Value.Eval(values);
                if (double.IsNaN(v))
                {
                    continue;
                }
                var s = r.State;
                if (r.Check.Stat == "time_to")
                {
                    /* "How long until X" has no answer when X already holds as the drive begins. */
                    if (s.N == 0 && v != 0)
                    {
                        s.Skip = true;
                    }
                    if (s.Skip)
                    {
                        continue;
                    }
                }
                if (s.N == 0)
                {
                    s.First = v;
                }
                s.Last = v;
                s.N++;
                s.Sum += v;
                if (s.Min == null || v < s.Min)
                {
                    s.Min = v;
                }
                if (s.Max == null || v > s.Max)
                {
                    s.Max = v;
                }
                bool isTrue = v != 0;
                if (isTrue)
                {
                    s.NTrue++;
                    if (!r.PrevTrue)
                    {
                        s.Edges++;
                    }
                    if (!s.Reached)
                    {
                        s.Reached = true;
                        s.TimeToS = tripTimeS;
                    }
                }
                r.PrevTrue = isTrue;
                s.ResSeen++;
                if (s.ResSeen <= CanaryCheckState.Reservoir)
                {
                    r.Res[s.ResSeen - 1] = (float)v;
                }
                else
                {
                    r.Rng = unchecked(r.Rng * 1664525u + 1013904223u);
                    uint j = (uint)((r.Rng >> 8) % (ulong)s.ResSeen);
                    if (j < CanaryCheckState.Reservoir)
                    {
                        r.Res[j] = (float)v;
                    }
                }
            }
        }

        /// <summary>The verdict of every check for the drive just replayed.</summary>
        public List<CanaryCheckResult> Finish(double tripTimeS)
        {
            var results = new List<CanaryCheckResult>();
            foreach (var r in _checks)
            {
                if (r.Missing != null)
                {
                    results.Add(new CanaryCheckResult { Id = r.Check.Id, Status = CanarySeverity.Unavailable, Missing = r.Missing });
                    continue;
                }
                int kept = (int)Math.Min(r.State.ResSeen, CanaryCheckState.Reservoir);
                r.State.Res = r.Res.AsSpan(0, kept).ToArray();
                var result = Evaluate(r.Check, r.State, tripTimeS);
                result.State = r.State;
                results.Add(result);
            }
            return results;
        }

        /// <summary>Applies a check's statistic and thresholds to what it has accumulated.</summary>
        public static CanaryCheckResult Evaluate(CanaryCheck check, CanaryCheckState state, double tripTimeS)
        {
            var result = new CanaryCheckResult { Id = check.Id, SeenS = state.N * TickS };
            bool enough = state.N > 0 && state.N * TickS >= check.MinTimeS;
            double v = Statistic(check, state, tripTimeS);
            if (!enough || double.IsNaN(v))
            {
                result.Status = CanarySeverity.NoData;
                result.Value = state.N > 0 ? CanaryJson.Clean(v) : null;
                return result;
            }
            result.Value = v;
            Classify(check, v, out var status, out bool low);
            result.Status = status;
            result.Low = low;
            return result;
        }

        public static void Classify(CanaryCheck check, double v, out CanarySeverity status, out bool low)
        {
            status = CanarySeverity.Ok;
            low = false;
            foreach (var level in check.Levels)
            {
                if (level.Hit(v) && level.Severity > status)
                {
                    status = level.Severity;
                    low = check.Low != null && level.IsLow;
                }
            }
        }

        private static double Statistic(CanaryCheck check, CanaryCheckState s, double tripTimeS)
        {
            if (s.N == 0)
            {
                return double.NaN;
            }
            switch (check.Stat)
            {
                case "mean": return s.Sum / s.N;
                case "min": return s.Min ?? double.NaN;
                case "max": return s.Max ?? double.NaN;
                case "p50": return Percentile(s, 0.50);
                case "p90": return Percentile(s, 0.90);
                case "p95": return Percentile(s, 0.95);
                case "p99": return Percentile(s, 0.99);
                case "last": return s.Last;
                case "first": return s.First;
                case "delta": return s.Last - s.First;
                case "range": return (s.Max ?? double.NaN) - (s.Min ?? double.NaN);
                case "time": return s.NTrue * TickS;
                case "fraction": return 100.0 * s.NTrue / s.N;
                /* Never reached: the elapsed time is a lower bound, which is what the thresholds test. */
                case "time_to": return s.Reached ? s.TimeToS : tripTimeS;
                case "count": return s.Edges;
                default: return double.NaN;
            }
        }

        private static double Percentile(CanaryCheckState s, double q)
        {
            int k = s.Res.Length;
            if (k <= 0)
            {
                return double.NaN;
            }
            var sorted = (float[])s.Res.Clone();
            Array.Sort(sorted);
            double pos = q * (k - 1);
            int lo = (int)Math.Floor(pos);
            int hi = lo + 1 < k ? lo + 1 : lo;
            return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
        }

        /// <summary>
        /// Combines the same check from several drives, oldest first, as if they had been one long drive.
        /// Sums, extremes and counts combine exactly; the percentile samples are merged in proportion
        /// to how much each drive saw, which keeps them a fair sample of the whole.
        /// </summary>
        public static CanaryCheckState Merge(string checkId, IEnumerable<CanaryCheckState> states)
        {
            var merged = new CanaryCheckState();
            uint rng = Seed(checkId);
            foreach (var s in states)
            {
                if (s.N == 0)
                {
                    continue;
                }
                if (merged.N == 0)
                {
                    merged.First = s.First;
                }
                merged.Last = s.Last;
                merged.Sum += s.Sum;
                merged.NTrue += s.NTrue;
                merged.Edges += s.Edges;
                if (s.Min != null && (merged.Min == null || s.Min < merged.Min))
                {
                    merged.Min = s.Min;
                }
                if (s.Max != null && (merged.Max == null || s.Max > merged.Max))
                {
                    merged.Max = s.Max;
                }
                merged.Res = MergeSamples(merged.Res, merged.ResSeen, s.Res, s.ResSeen, ref rng);
                merged.ResSeen += s.ResSeen;
                merged.N += s.N;
            }
            return merged;
        }

        private static float[] MergeSamples(float[] a, long seenA, float[] b, long seenB, ref uint rng)
        {
            if (a.Length == 0)
            {
                return (float[])b.Clone();
            }
            if (b.Length == 0)
            {
                return a;
            }
            /* While both sides still hold every value they saw, the union is exact. */
            if (a.Length == seenA && b.Length == seenB && a.Length + b.Length <= CanaryCheckState.Reservoir)
            {
                return a.Concat(b).ToArray();
            }
            var poolA = new List<float>(a);
            var poolB = new List<float>(b);
            int size = Math.Min(CanaryCheckState.Reservoir, a.Length + b.Length);
            var merged = new float[size];
            double shareA = (double)seenA / (seenA + seenB);
            for (int i = 0; i < size; i++)
            {
                rng = unchecked(rng * 1664525u + 1013904223u);
                bool fromA = poolB.Count == 0 || (poolA.Count > 0 && ((rng >> 8) & 0xFFFF) / 65536.0 < shareA);
                var pool = fromA ? poolA : poolB;
                rng = unchecked(rng * 1664525u + 1013904223u);
                int pick = (int)((rng >> 8) % (uint)pool.Count);
                merged[i] = pool[pick];
                pool[pick] = pool[^1];
                pool.RemoveAt(pool.Count - 1);
            }
            return merged;
        }
    }
}
