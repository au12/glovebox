using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarCareTracker.Canary
{
    /// <summary>
    /// Presents CANary values to a reader. The device records everything in metric; this converts to the
    /// reader's units and words numbers the way the device's own screen does.
    /// </summary>
    public sealed class CanaryFormat
    {
        public bool Imperial { get; }

        public CanaryFormat(bool imperial)
        {
            Imperial = imperial;
        }

        /// <summary>Converts a metric value. A leading "Δ" marks a difference rather than a level (40 Δ°C is 72 °F, not 104).</summary>
        public (double Value, string Unit, int Dec) Convert(double v, string? unit, int dec)
        {
            string u = unit ?? string.Empty;
            if (Imperial)
            {
                switch (u)
                {
                    case "°C": return (v * 1.8 + 32, "°F", dec);
                    case "Δ°C": return (v * 1.8, "°F", dec);
                    case "km": return (v * 0.621371, "mi", dec);
                    case "km/h": return (v * 0.621371, "mph", dec);
                    case "kPa": return (v * 0.1450377, "psi", Math.Abs(v * 0.1450377) >= 100 ? 0 : 1);
                    case "L": return (v * 0.2641721, "gal", Math.Min(2, dec + 1));
                    case "L/h": return (v * 0.2641721, "gal/h", Math.Min(2, dec + 1));
                    case "Nm": return (v * 0.7375621, "lb-ft", dec);
                }
            }
            if (u.StartsWith('Δ'))
            {
                u = u[1..];
            }
            return (v, u, dec);
        }

        public static string Number(double v, int dec)
        {
            double r = Math.Round(v, Math.Clamp(dec, 0, 6), MidpointRounding.AwayFromZero);
            if (r == 0)
            {
                r = 0; /* never "-0" */
            }
            return r.ToString("N" + Math.Clamp(dec, 0, 6), CultureInfo.CurrentCulture);
        }

        private static bool Tight(string unit) => unit.Length == 0 || unit == "%" || unit == "°";

        /// <summary>A reading with its unit: "3.6 psi", "4.3°", "7 min".</summary>
        public string Value(double? v, string? unit, int dec)
        {
            if (v == null || double.IsNaN(v.Value))
            {
                return "no reading";
            }
            if (unit == "s")
            {
                return Duration(v.Value);
            }
            var c = Convert(v.Value, unit, dec);
            return Number(c.Value, c.Dec) + (Tight(c.Unit) ? string.Empty : " ") + c.Unit;
        }

        public string Distance(double km, int dec = 0) => Value(km, "km", dec);
        public string Speed(double kmh) => Value(kmh, "km/h", 0);
        public string Volume(double litres) => Value(litres, "L", 1);

        /// <summary>Fuel economy for a stretch of driving, in the reader's convention.</summary>
        public string? Economy(double km, double litres)
        {
            if (km < 1 || litres <= 0.05)
            {
                return null;
            }
            return Imperial
                ? $"{Number(km * 0.621371 / (litres * 0.2641721), 1)} mpg"
                : $"{Number(litres / km * 100, 1)} L/100 km";
        }

        public static string Duration(double seconds)
        {
            long s = (long)Math.Max(0, Math.Round(seconds));
            if (s < 90)
            {
                return $"{s} s";
            }
            long m = s / 60, r = s % 60;
            if (m < 10 && r >= 5)
            {
                return $"{m} min {r} s";
            }
            if (s < 3600)
            {
                return $"{(long)Math.Round(s / 60.0)} min";
            }
            return $"{m / 60} h {m % 60} min";
        }

        /// <summary>How long a drive lasted: whole minutes, since nobody times a commute to the second.</summary>
        public static string DriveTime(double seconds)
        {
            return seconds < 60 ? Duration(seconds) : Duration(Math.Round(seconds / 60.0) * 60);
        }

        public static string Bytes(long n)
        {
            if (n >= 1_000_000_000) return $"{Number(n / 1e9, 1)} GB";
            if (n >= 1_000_000) return $"{Number(n / 1e6, 1)} MB";
            if (n >= 1_000) return $"{Number(n / 1e3, 0)} KB";
            return $"{n} bytes";
        }

        /// <summary>A check's sentence with the figure in the reader's units.</summary>
        public string Sentence(string? template, double? value, string? unit, int dec)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }
            /* the figure and its unit stay on one line */
            return template.Replace("{value}", Value(value, unit, dec).Replace(' ', '\u00A0'));
        }

        public string Sentence(CanaryWeekCheck c) => Sentence(c.Template, c.Value, c.Check.Unit, c.Check.Dec);

        public static string SeverityClass(CanarySeverity s) => s switch
        {
            CanarySeverity.Critical => "crit",
            CanarySeverity.Warning => "warn",
            CanarySeverity.Advisory => "adv",
            CanarySeverity.Ok => "ok",
            CanarySeverity.Unavailable => "off",
            _ => "wait"
        };

        public static string SeverityWord(CanarySeverity s) => s switch
        {
            CanarySeverity.Critical => "Critical",
            CanarySeverity.Warning => "Warning",
            CanarySeverity.Advisory => "Advisory",
            CanarySeverity.Ok => "Passed",
            CanarySeverity.Unavailable => "Not available",
            _ => "Waiting for data"
        };

        /* ───── limit gauge ───── */

        public sealed class Gauge
        {
            public List<(double Width, string Class)> Zones { get; } = new();
            public List<(double At, string Label)> Ticks { get; } = new();
            public double Needle { get; set; }
            public string Spoken { get; set; } = string.Empty;
        }

        private static int Decimals(double x) => x == Math.Floor(x) ? 0 : Math.Abs(x * 10 - Math.Round(x * 10)) < 1e-6 ? 1 : 2;

        /// <summary>
        /// Where a reading sits against a check's thresholds. The scale is piecewise: the healthy range
        /// gets a fixed share of the bar and each alert band an equal share of the rest, so the bands
        /// stay readable however the numbers are spaced. The same drawing as on the device.
        /// </summary>
        public Gauge? GaugeFor(CanaryCheck check, double? reading)
        {
            if (reading == null || check.Levels.Count == 0)
            {
                return null;
            }
            double v = reading.Value;
            var hi = check.Levels.Where(l => !l.IsLow).OrderBy(l => l.Value).ToList(); /* mild to severe */
            var lo = check.Levels.Where(l => l.IsLow).OrderBy(l => l.Value).ToList();  /* severe to mild */
            static double StepOf(List<CanaryLevel> a) => a.Count > 1 ? (a[^1].Value - a[0].Value) / (a.Count - 1) : Math.Max(Math.Abs(a[0].Value) * 0.25, 1e-6);
            bool both = lo.Count > 0 && hi.Count > 0;
            double okFrom, okTo;
            if (both)
            {
                okFrom = lo[^1].Value;
                okTo = hi[0].Value;
            }
            else if (hi.Count > 0)
            {
                double first = hi[0].Value, step = StepOf(hi);
                okTo = first;
                okFrom = first > 0 && first / step <= 6 ? 0 : first - 3 * step;
                if (v < okFrom) okFrom = v - step * 0.25;
            }
            else
            {
                double first = lo[^1].Value, step = StepOf(lo);
                okFrom = first;
                okTo = first < 0 && -first / step <= 6 ? 0 : first + 3 * step;
                if (v > okTo) okTo = v + step * 0.25;
            }
            double okWidth = both ? 40 : 46, sideWidth = both ? 30 : 54;
            int tickDec = check.Levels.Max(l => Decimals(l.Value));
            var gauge = new Gauge();
            var stops = new List<(double Value, double At)>();
            var spoken = new List<string>();
            double x = 0;
            if (lo.Count > 0)
            {
                double w = sideWidth / lo.Count;
                stops.Add((lo[0].Value - StepOf(lo), 0));
                foreach (var l in lo)
                {
                    gauge.Zones.Add((w, SeverityClass(l.Severity)));
                    x += w;
                    stops.Add((l.Value, x));
                    string label = Value(l.Value, check.Unit, tickDec);
                    gauge.Ticks.Add((x, label));
                    spoken.Add($"{SeverityWord(l.Severity)} below {label}");
                }
            }
            else
            {
                stops.Add((okFrom, 0));
            }
            gauge.Zones.Add((okWidth, "ok"));
            x += okWidth;
            if (hi.Count > 0)
            {
                double w = sideWidth / hi.Count;
                foreach (var h in hi)
                {
                    stops.Add((h.Value, x));
                    string label = Value(h.Value, check.Unit, tickDec);
                    gauge.Ticks.Add((x, label));
                    spoken.Add($"{SeverityWord(h.Severity)} above {label}");
                    gauge.Zones.Add((w, SeverityClass(h.Severity)));
                    x += w;
                }
                stops.Add((hi[^1].Value + StepOf(hi), 100));
            }
            else
            {
                stops.Add((okTo, 100));
            }
            double pos = v <= stops[0].Value ? 0 : 100;
            for (int i = 1; i < stops.Count; i++)
            {
                if (v <= stops[i].Value)
                {
                    var a = stops[i - 1];
                    var b = stops[i];
                    double span = b.Value - a.Value;
                    pos = v <= a.Value ? a.At : a.At + (b.At - a.At) * (v - a.Value) / (span == 0 ? 1 : span);
                    break;
                }
            }
            gauge.Needle = Math.Clamp(pos, 1, 99);
            gauge.Spoken = $"Reading {Value(v, check.Unit, check.Dec)}. {string.Join(", ", spoken)}.";
            return gauge;
        }

        /* ───── week-over-week trend ───── */

        public sealed class Trend
        {
            public string Path { get; set; } = string.Empty;
            public List<(double X, double Y, string Class, string Title)> Dots { get; } = new();
            public double Width { get; set; }
            public double Height { get; set; }
            /// <summary>"up from 3.1° four weeks ago", when there is something to compare.</summary>
            public string Words { get; set; } = string.Empty;
        }

        /// <summary>The smallest gap between two of a check's limits: the size of a change worth talking about.</summary>
        private static double Step(CanaryCheck check)
        {
            var limits = check.Levels.Select(l => l.Value).Distinct().OrderBy(v => v).ToList();
            double step = double.MaxValue;
            for (int i = 1; i < limits.Count; i++)
            {
                step = Math.Min(step, limits[i] - limits[i - 1]);
            }
            if (step == double.MaxValue)
            {
                step = limits.Count == 1 && Math.Abs(limits[0]) > 1e-9 ? Math.Abs(limits[0]) * 0.25 : 1;
            }
            return Math.Max(step, 1e-6);
        }

        /// <summary>A small line of weekly values, oldest to newest, with each week's verdict as a dot.</summary>
        public Trend? TrendFor(CanaryWeekCheck c, double width = 132, double height = 34)
        {
            var points = c.Trend.Select((p, i) => (Point: p, Index: i)).Where(x => x.Point.Value != null).ToList();
            if (points.Count < 2)
            {
                return null;
            }
            double min = points.Min(x => x.Point.Value!.Value), max = points.Max(x => x.Point.Value!.Value);
            /* Drawn against the check's own limits: a wobble far smaller than the gap between two limits
               is a flat line, not a cliff. */
            double step = Step(c.Check);
            if (max - min < step)
            {
                double mid = (max + min) / 2;
                min = mid - step / 2;
                max = mid + step / 2;
            }
            const double pad = 5;
            int n = c.Trend.Count;
            /* the line runs from the first week there is a figure for to this week, however few weeks that is */
            int start = points[0].Index, weeksShown = Math.Max(1, n - 1 - start);
            var trend = new Trend { Width = width, Height = height };
            var path = new List<string>();
            foreach (var (point, index) in points)
            {
                double px = pad + (width - 2 * pad) * (index - start) / weeksShown;
                double py = height - pad - (point.Value!.Value - min) / (max - min) * (height - 2 * pad);
                path.Add($"{(path.Count == 0 ? "M" : "L")}{px.ToString("0.#", CultureInfo.InvariantCulture)} {py.ToString("0.#", CultureInfo.InvariantCulture)}");
                trend.Dots.Add((px, py, SeverityClass(point.Status), $"Week of {point.Week:MMM d}: {Value(point.Value, c.Check.Unit, c.Check.Dec)}"));
            }
            trend.Path = string.Join(string.Empty, path);
            var firstPoint = points[0];
            var lastPoint = points[^1];
            if (lastPoint.Index == n - 1 && firstPoint.Index != lastPoint.Index)
            {
                double delta = lastPoint.Point.Value!.Value - firstPoint.Point.Value!.Value;
                var converted = Convert(Math.Abs(delta), c.Check.Unit != null && c.Check.Unit.Contains("°C") ? "Δ°C" : c.Check.Unit, c.Check.Dec);
                int weeks = lastPoint.Index - firstPoint.Index;
                string ago = weeks == 1 ? "last week" : $"{CanaryReportBuilder.CountWord(weeks).ToLowerInvariant()} weeks ago";
                bool flat = Math.Round(converted.Value, converted.Dec) == 0;
                trend.Words = flat ? $"Unchanged since {ago}"
                    : Math.Abs(delta) < step * 0.15 ? $"About the same as {ago}"
                    : $"{(delta > 0 ? "Up" : "Down")} {Number(converted.Value, converted.Dec)}{(Tight(converted.Unit) ? string.Empty : " ")}{converted.Unit} since {ago}";
            }
            return trend;
        }
    }

    /// <summary>Plain-language meanings of trouble codes, from the same table the device carries.</summary>
    public sealed class CanaryCodeBook
    {
        private readonly Dictionary<string, string> _codes = new();
        private readonly List<(Regex Pattern, string Text)> _patterns = new();
        private readonly List<(Regex Pattern, string Text)> _ranges = new();
        private readonly Dictionary<string, string> _failureTypes = new();

        public CanaryCodeBook(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("codes", out var codes))
                {
                    foreach (var c in codes.EnumerateObject())
                    {
                        _codes[c.Name] = c.Value.GetString() ?? string.Empty;
                    }
                }
                foreach (var (name, list) in new[] { ("patterns", _patterns), ("ranges", _ranges) })
                {
                    if (!root.TryGetProperty(name, out var items))
                    {
                        continue;
                    }
                    foreach (var p in items.EnumerateArray())
                    {
                        list.Add((new Regex(p.GetProperty("re").GetString() ?? "^$", RegexOptions.Compiled), p.GetProperty("text").GetString() ?? string.Empty));
                    }
                }
                if (root.TryGetProperty("failure_types", out var types))
                {
                    foreach (var t in types.EnumerateObject())
                    {
                        _failureTypes[t.Name] = t.Value.GetString() ?? string.Empty;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                /* without the table, codes are simply shown without a description */
            }
        }

        /// <summary>What a code means, and whether that is its exact meaning or only the system it belongs to.</summary>
        public (string Text, bool Exact) Describe(string code)
        {
            if (_codes.TryGetValue(code, out var text))
            {
                return (text, true);
            }
            foreach (var (pattern, template) in _patterns)
            {
                var m = pattern.Match(code);
                if (m.Success)
                {
                    return (Regex.Replace(template, @"\{(\d)\}", g => m.Groups[int.Parse(g.Groups[1].Value)].Value), true);
                }
            }
            foreach (var (pattern, system) in _ranges)
            {
                if (pattern.IsMatch(code))
                {
                    return (system, false);
                }
            }
            return ("No description available", false);
        }

        public string? FailureType(int ftb) => ftb > 0 && _failureTypes.TryGetValue(ftb.ToString("X2"), out var text) ? text : null;

        /// <summary>The line under a code: where it is stored, how it failed if the control unit says, and any caveat.</summary>
        public string Note(CanaryCode code)
        {
            var parts = new List<string>();
            string where = code.Module ?? code.Ecu ?? string.Empty;
            if (where.Length > 0)
            {
                parts.Add(where);
            }
            var failure = FailureType(code.Ftb);
            if (failure != null)
            {
                parts.Add($"Reported as: {failure}");
            }
            if (!Describe(code.Code).Exact)
            {
                parts.Add("The car maker defines the exact fault for this code");
            }
            return parts.Count > 0 ? string.Join(". ", parts) + "." : string.Empty;
        }
    }
}
