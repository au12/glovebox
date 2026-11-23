using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CarCareTracker.Canary
{
    /// <summary>
    /// Reads a CANary raw log (.cnl): a text file with a header naming the signals, one line per
    /// sample ("time index value") and the occasional event line ("!time kind json"). A value holds
    /// until the next sample of the same signal; a signal that falls silent goes stale after a time
    /// that depends on how often it is polled.
    /// </summary>
    public static class CanaryLog
    {
        public sealed class Signal
        {
            public int Index;
            public string Id = string.Empty;
            public string Unit = string.Empty;
            public string Rate = "medium";
            public string Name = string.Empty;
            /// <summary>Milliseconds without a sample before the value no longer counts; 0 = never.</summary>
            public int StaleMs => Rate switch { "fast" => 5000, "medium" => 5000, "slow" => 30000, "rare" => 180000, _ => 0 };
        }

        public sealed class Header
        {
            public string Device = string.Empty;
            public string Fw = string.Empty;
            public string Vin = string.Empty;
            public string ProfileId = string.Empty;
            public string ProfileHash = string.Empty;
            public string Trip = string.Empty;
            public long Start;
            public List<Signal> Signals = new();
        }

        public static Stream OpenRaw(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        }

        private static bool ParseHeaderLine(string line, Header header)
        {
            if (line.StartsWith("#sig ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', 6, StringSplitOptions.None);
                if (parts.Length >= 5 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < 4096)
                {
                    header.Signals.Add(new Signal
                    {
                        Index = index,
                        Id = parts[2],
                        Unit = parts[3] == "-" ? string.Empty : parts[3],
                        Rate = parts[4],
                        Name = parts.Length > 5 ? parts[5].Trim() : parts[2]
                    });
                }
                return true;
            }
            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            switch (key)
            {
                case "#device": header.Device = value; break;
                case "#fw": header.Fw = value; break;
                case "#vin": header.Vin = value == "-" ? string.Empty : value; break;
                case "#trip": header.Trip = value; break;
                case "#start": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out header.Start); break;
                case "#profile":
                    var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    header.ProfileId = p.Length > 0 ? p[0] : string.Empty;
                    header.ProfileHash = p.Length > 1 ? p[1] : string.Empty;
                    break;
            }
            return true;
        }

        /// <summary>Reads only the header: cheap, and enough to know which profile a log needs.</summary>
        public static Header ReadHeader(Stream log)
        {
            var header = new Header();
            using var reader = new StreamReader(log, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                if (line[0] != '#')
                {
                    break;
                }
                ParseHeaderLine(line, header);
            }
            return header;
        }

        /// <summary>
        /// Replays a log through the health checks and gathers everything the weekly report needs.
        /// <paramref name="profileFor"/> returns the profile for a hash (or the best stand-in);
        /// <paramref name="startIfUnknown"/> is used when the device did not know the time while recording.
        /// </summary>
        public static CanaryTrip Analyze(Stream log, Func<Header, CanaryProfile?> profileFor, long startIfUnknown = 0)
        {
            var header = new Header();
            var trip = new CanaryTrip();
            using var reader = new StreamReader(log, Encoding.UTF8, false, 1 << 16, leaveOpen: true);

            double[] values = Array.Empty<double>();
            long[] sampleAt = Array.Empty<long>();
            int[] staleMs = Array.Empty<int>();
            SignalAccumulator?[] stats = Array.Empty<SignalAccumulator?>();
            CanaryCheckEngine? engine = null;
            double[] held = Array.Empty<double>();
            int speedIndex = -1, fuelRateIndex = -1, rpmIndex = -1, odoIndex = -1;
            long nextTick = 500, lastTime = 0, samples = 0, bytes = 0;
            var codes = new Dictionary<string, CanaryCode>();
            var columns = new Dictionary<string, int>();
            CanaryProfile? profile = null;
            bool started = false;

            void Start()
            {
                started = true;
                int size = header.Signals.Count == 0 ? 0 : header.Signals.Max(x => x.Index) + 1;
                values = new double[size];
                held = new double[size];
                sampleAt = new long[size];
                staleMs = new int[size];
                stats = new SignalAccumulator?[size];
                Array.Fill(values, double.NaN);
                Array.Fill(sampleAt, -1);
                var byId = columns;
                foreach (var s in header.Signals)
                {
                    staleMs[s.Index] = s.StaleMs;
                    stats[s.Index] = new SignalAccumulator { Def = s };
                    byId[s.Id] = s.Index;
                }
                speedIndex = byId.GetValueOrDefault("speed", -1);
                fuelRateIndex = byId.GetValueOrDefault("fuel_rate", -1);
                rpmIndex = byId.GetValueOrDefault("rpm", -1);
                odoIndex = byId.GetValueOrDefault("odometer", -1);
                profile = profileFor(header);
                if (profile != null)
                {
                    engine = new CanaryCheckEngine(profile.Checks, id => byId.GetValueOrDefault(id, -1));
                }
            }

            void Tick(long t)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    held[i] = sampleAt[i] >= 0 && (staleMs[i] == 0 || t - sampleAt[i] <= staleMs[i]) ? values[i] : double.NaN;
                }
                double seconds = t / 1000.0;
                engine?.Tick(held, seconds);
                double speed = speedIndex >= 0 ? held[speedIndex] : double.NaN;
                if (!double.IsNaN(speed))
                {
                    trip.DistanceKm += speed * CanaryCheckEngine.TickS / 3600.0;
                    if (speed > trip.MaxSpeed)
                    {
                        trip.MaxSpeed = speed;
                    }
                }
                if (fuelRateIndex >= 0 && !double.IsNaN(held[fuelRateIndex]))
                {
                    trip.FuelL += held[fuelRateIndex] * CanaryCheckEngine.TickS / 3600.0;
                }
                if (rpmIndex >= 0 && held[rpmIndex] > 300 && !(speed >= 1))
                {
                    trip.IdleS += CanaryCheckEngine.TickS;
                }
                for (int i = 0; i < stats.Length; i++)
                {
                    var acc = stats[i];
                    if (acc != null && !double.IsNaN(held[i]))
                    {
                        acc.TickSum += held[i];
                        acc.Ticks++;
                    }
                }
            }

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                bytes += line.Length + 1;
                if (line.Length == 0)
                {
                    continue;
                }
                char first = line[0];
                if (first == '#')
                {
                    if (!started)
                    {
                        ParseHeaderLine(line, header);
                    }
                    continue;
                }
                if (!started)
                {
                    Start();
                }
                if (first == '!')
                {
                    int s1 = line.IndexOf(' ');
                    int s2 = s1 < 0 ? -1 : line.IndexOf(' ', s1 + 1);
                    if (s1 < 0 || !long.TryParse(line.AsSpan(1, s1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long eventTime))
                    {
                        continue;
                    }
                    if (eventTime > lastTime)
                    {
                        lastTime = eventTime;
                    }
                    string kind = s2 < 0 ? line[(s1 + 1)..] : line[(s1 + 1)..s2];
                    string json = s2 < 0 ? "{}" : line[(s2 + 1)..];
                    ReadEvent(kind, json, trip, codes);
                    continue;
                }
                if (first < '0' || first > '9')
                {
                    continue;
                }
                var span = line.AsSpan();
                int a = span.IndexOf(' ');
                if (a < 0)
                {
                    continue;
                }
                var rest = span[(a + 1)..];
                int b = rest.IndexOf(' ');
                if (b < 0
                    || !long.TryParse(span[..a], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)
                    || !int.TryParse(rest[..b], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    || !double.TryParse(rest[(b + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || index < 0 || index >= values.Length || t < 0)
                {
                    continue; /* a torn line from a power cut is skipped, not fatal */
                }
                while (nextTick <= t)
                {
                    Tick(nextTick);
                    nextTick += 500;
                }
                values[index] = value;
                sampleAt[index] = t;
                stats[index]?.Add(value);
                samples++;
                if (t > lastTime)
                {
                    lastTime = t;
                }
            }
            if (!started)
            {
                Start();
            }
            while (nextTick <= lastTime)
            {
                Tick(nextTick);
                nextTick += 500;
            }

            double duration = lastTime / 1000.0;
            trip.Id = header.Trip;
            trip.Device = header.Device;
            trip.Fw = header.Fw;
            trip.Vin = header.Vin;
            trip.Profile = header.ProfileId;
            trip.ProfileHash = header.ProfileHash;
            trip.Start = header.Start > 0 ? header.Start : startIfUnknown;
            trip.DurationS = Math.Round(duration, 1);
            trip.DistanceKm = Math.Round(trip.DistanceKm, 3);
            trip.FuelL = Math.Round(trip.FuelL, 3);
            trip.Samples = samples;
            trip.Bytes = bytes;
            trip.Codes = codes.Values.OrderBy(x => x.Code).ToList();
            foreach (var code in trip.Codes.Where(x => string.IsNullOrEmpty(x.Module)))
            {
                /* logs from early firmware name the control unit only by its short id */
                code.Module = profile?.ModuleName(code.Ecu) ?? (string.IsNullOrEmpty(code.Ecu) ? "Engine (OBD-II)" : code.Ecu);
            }
            foreach (var acc in stats)
            {
                if (acc == null || acc.N == 0)
                {
                    continue;
                }
                trip.Signals[acc.Def.Id] = new CanarySignalStats
                {
                    Name = acc.Def.Name,
                    Unit = acc.Def.Unit,
                    Rate = acc.Def.Rate,
                    Min = acc.Min,
                    Max = acc.Max,
                    Avg = acc.Ticks > 0 ? acc.TickSum / acc.Ticks : acc.Sum / acc.N,
                    First = acc.First,
                    Last = acc.Last,
                    N = acc.N
                };
            }
            if (odoIndex >= 0 && stats[odoIndex] is { N: > 0 } odo)
            {
                trip.OdoStartKm = odo.First;
                trip.OdoEndKm = odo.Last;
            }
            if (engine != null)
            {
                trip.Checks = engine.Finish(duration,
                    id => columns.TryGetValue(id, out int i) && stats[i] is { N: > 0 },
                    id => columns.TryGetValue(id, out int i) && stats[i] != null ? stats[i]!.Def.Name : id);
                var findings = trip.Checks.Where(x => x.Status.IsFinding()).ToList();
                trip.Worst = findings.Count > 0 ? findings.Max(x => x.Status)
                    : trip.Checks.Any(x => x.Status == CanarySeverity.Ok) ? CanarySeverity.Ok : CanarySeverity.NoData;
            }
            return trip;
        }

        private static void ReadEvent(string kind, string json, CanaryTrip trip, Dictionary<string, CanaryCode> codes)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (kind == "dtc" && root.TryGetProperty("codes", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in list.EnumerateArray())
                    {
                        var code = ReadCode(c);
                        if (code == null)
                        {
                            continue;
                        }
                        /* a code is often both pending and stored: keep the one that matters more */
                        string key = $"{code.Code}|{code.Ecu ?? code.Module}";
                        if (!codes.TryGetValue(key, out var seen) || KindRank(code.Kind) >= KindRank(seen.Kind))
                        {
                            codes[key] = code;
                        }
                    }
                }
                else if (kind == "end" && root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                {
                    trip.EndReason = reason.GetString();
                }
            }
            catch (JsonException)
            {
                /* an event cut short by a power loss carries nothing we can use */
            }
        }

        private static int KindRank(string kind) => kind switch { "stored" => 3, "permanent" => 2, _ => 1 };

        public static CanaryCode? ReadCode(JsonElement c)
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            string? Text(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Number(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
            /* early firmware wrote the kind as a number in log events: 1 stored, 2 pending, 3 permanent */
            string kind = Text("kind") ?? (Number("kind") switch { 2 => "pending", 3 => "permanent", _ => "stored" });
            return new CanaryCode
            {
                Code = code.GetString() ?? string.Empty,
                Kind = string.IsNullOrEmpty(kind) ? "stored" : kind,
                Ecu = Text("ecu"),
                Module = Text("module"),
                Ftb = Number("ftb"),
                Status = Number("status")
            };
        }

        private sealed class SignalAccumulator
        {
            public Signal Def = new();
            public long N, Ticks;
            public double Sum, TickSum, Min, Max, First, Last;
            public void Add(double v)
            {
                if (N == 0)
                {
                    First = Min = Max = v;
                }
                if (v < Min) Min = v;
                if (v > Max) Max = v;
                Last = v;
                Sum += v;
                N++;
            }
        }

        /// <summary>One signal reduced to a fixed number of buckets for charting: mean, low and high per bucket.</summary>
        public sealed class Series
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public List<double> T { get; set; } = new();
            public List<double> V { get; set; } = new();
            public List<double> Lo { get; set; } = new();
            public List<double> Hi { get; set; } = new();
        }

        /// <summary>Downsamples the chosen signals straight from the raw log.</summary>
        public static (double DurationS, List<Series> Series) ReadSeries(Stream log, IReadOnlyList<string> signalIds, int points)
        {
            points = Math.Clamp(points, 20, 1500);
            var header = new Header();
            using var reader = new StreamReader(log, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            var wanted = new Dictionary<int, int>(); /* log index -> position in signalIds */
            var raw = signalIds.Select(_ => new List<(long T, double V)>()).ToList();
            bool started = false;
            long lastTime = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                if (line[0] == '#')
                {
                    if (!started)
                    {
                        ParseHeaderLine(line, header);
                    }
                    continue;
                }
                if (!started)
                {
                    started = true;
                    foreach (var s in header.Signals)
                    {
                        int pos = -1;
                        for (int i = 0; i < signalIds.Count; i++)
                        {
                            if (signalIds[i] == s.Id)
                            {
                                pos = i;
                                break;
                            }
                        }
                        if (pos >= 0)
                        {
                            wanted[s.Index] = pos;
                        }
                    }
                }
                if (line[0] < '0' || line[0] > '9')
                {
                    continue;
                }
                var span = line.AsSpan();
                int a = span.IndexOf(' ');
                if (a < 0)
                {
                    continue;
                }
                var rest = span[(a + 1)..];
                int b = rest.IndexOf(' ');
                if (b < 0 || !long.TryParse(span[..a], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)
                    || !int.TryParse(rest[..b], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    continue;
                }
                if (t > lastTime)
                {
                    lastTime = t;
                }
                if (wanted.TryGetValue(index, out int which)
                    && double.TryParse(rest[(b + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    raw[which].Add((t, value));
                }
            }
            double duration = Math.Max(lastTime / 1000.0, 1);
            double bucketMs = duration * 1000.0 / points;
            var result = new List<Series>();
            for (int i = 0; i < signalIds.Count; i++)
            {
                var def = header.Signals.FirstOrDefault(x => x.Id == signalIds[i]);
                if (def == null || raw[i].Count == 0)
                {
                    continue;
                }
                var series = new Series { Id = def.Id, Name = def.Name, Unit = def.Unit };
                int bucket = -1, n = 0;
                double sum = 0, lo = 0, hi = 0;
                void Flush()
                {
                    if (n == 0)
                    {
                        return;
                    }
                    series.T.Add(Math.Round((bucket + 0.5) * bucketMs / 1000.0, 1));
                    series.V.Add(Math.Round(sum / n, 4));
                    series.Lo.Add(lo);
                    series.Hi.Add(hi);
                }
                foreach (var (t, v) in raw[i])
                {
                    int bk = Math.Min(points - 1, (int)(t / bucketMs));
                    if (bk != bucket)
                    {
                        Flush();
                        bucket = bk;
                        n = 0;
                        sum = 0;
                    }
                    if (n == 0 || v < lo) lo = v;
                    if (n == 0 || v > hi) hi = v;
                    sum += v;
                    n++;
                }
                Flush();
                result.Add(series);
            }
            return (duration, result);
        }
    }
}
