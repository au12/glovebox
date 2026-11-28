using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarCareTracker.Canary
{
    /// <summary>How serious a finding is. Ordered, so the larger value is the worse one.</summary>
    public enum CanarySeverity
    {
        Ok = 0,
        Advisory = 1,
        Warning = 2,
        Critical = 3,
        /// <summary>Not enough data to say.</summary>
        NoData = 100,
        /// <summary>The car does not report what this check needs.</summary>
        Unavailable = 101
    }

    public static class CanaryJson
    {
        /// <summary>
        /// A string from a device's JSON, or null. Text with broken UTF-8 (a name cut off in the middle
        /// of a character, say) counts as missing instead of failing the whole request.
        /// </summary>
        public static string? Str(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            try
            {
                return e.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            /* named literals: a stray infinity in a summary must never stop it being saved */
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public static string Name(this CanarySeverity s) => s switch
        {
            CanarySeverity.Ok => "ok",
            CanarySeverity.Advisory => "advisory",
            CanarySeverity.Warning => "warning",
            CanarySeverity.Critical => "critical",
            CanarySeverity.Unavailable => "unavailable",
            _ => "nodata"
        };

        public static CanarySeverity ParseSeverity(string? s) => s switch
        {
            "ok" => CanarySeverity.Ok,
            "advisory" => CanarySeverity.Advisory,
            "warning" => CanarySeverity.Warning,
            "critical" => CanarySeverity.Critical,
            "unavailable" => CanarySeverity.Unavailable,
            _ => CanarySeverity.NoData
        };

        /// <summary>True for advisory, warning and critical.</summary>
        public static bool IsFinding(this CanarySeverity s) => s >= CanarySeverity.Advisory && s <= CanarySeverity.Critical;

        /// <summary>NaN and infinity have no place in JSON; they become "no value".</summary>
        public static double? Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    /// <summary>One threshold of a check: "warning above 15".</summary>
    public sealed class CanaryLevel
    {
        public CanarySeverity Severity { get; set; }
        /// <summary>gt, ge, lt or le.</summary>
        public string Op { get; set; } = "gt";
        public double Value { get; set; }
        [JsonIgnore]
        public bool IsLow => Op.Length > 0 && Op[0] == 'l';
        public bool Hit(double v) => Op switch
        {
            "gt" => v > Value,
            "ge" => v >= Value,
            "lt" => v < Value,
            _ => v <= Value
        };
    }

    /// <summary>What a check says when it trips. A two-sided check has one of these for each direction.</summary>
    public sealed class CanaryCheckText
    {
        public string? Alert { get; set; }
        public string? Summary { get; set; }
        public string? Detail { get; set; }
        public List<string> Causes { get; set; } = new();
    }

    /// <summary>A health check as defined in a vehicle profile.</summary>
    public sealed class CanaryCheck
    {
        public string Id { get; set; } = string.Empty;
        /// <summary>Names the check, not the fault: "Fuel mixture at idle".</summary>
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public string Unit { get; set; } = string.Empty;
        public string Stat { get; set; } = "mean";
        public int Dec { get; set; } = 1;
        public double MinTimeS { get; set; } = 10;
        public double SettleS { get; set; }
        public string When { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public List<CanaryLevel> Levels { get; set; } = new();
        public string? Ok { get; set; }
        public string? Needs { get; set; }
        /// <summary>Default texts, also used for the "too high" side.</summary>
        public CanaryCheckText High { get; set; } = new();
        /// <summary>Texts for the "too low" side of a two-sided check; null when the check has one side.</summary>
        public CanaryCheckText? Low { get; set; }

        /// <summary>Stats that only make sense within one drive; a week reports the worst drive instead of pooling.</summary>
        [JsonIgnore]
        public bool PerTrip => Stat is "time_to" or "time" or "count" or "delta" or "first";

        public CanaryCheckText Says(bool low) => low && Low != null ? Low : High;
        public string Headline(CanarySeverity status, bool low) =>
            status.IsFinding() ? (Says(low).Alert ?? High.Alert ?? Title) : Title;
        public string? Template(CanarySeverity status, bool low)
        {
            if (status.IsFinding())
            {
                return Says(low).Summary ?? High.Summary ?? "Outside the expected range ({value}).";
            }
            return status == CanarySeverity.Ok ? (Ok ?? "Within the expected range ({value}).") : null;
        }
        public string? Detail(bool low) => Says(low).Detail ?? High.Detail;
        public List<string> Causes(bool low) => Says(low).Causes.Count > 0 ? Says(low).Causes : High.Causes;
    }

    public sealed class CanarySignalDef
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Rate { get; set; } = "medium";
        public int Dec { get; set; } = 1;
    }

    /// <summary>The parts of a vehicle profile the server needs: signal names and the health checks.</summary>
    public sealed class CanaryProfile
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public List<CanarySignalDef> Signals { get; set; } = new();
        public List<CanaryCheck> Checks { get; set; } = new();
        /// <summary>Control units by their short id: "abs" is "Brakes / ABS".</summary>
        public Dictionary<string, string> Modules { get; set; } = new();

        /// <summary>The name of a control unit, for codes that only carry its id.</summary>
        public string? ModuleName(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return id == "obd" ? "Engine (OBD-II)" : Modules.GetValueOrDefault(id);
        }

        /// <summary>Reads a resolved profile as the device uploads it.</summary>
        public static CanaryProfile Parse(string json, string hash)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("a profile is a JSON object");
            }
            var profile = new CanaryProfile { Id = Str(root, "id") ?? string.Empty, Name = Str(root, "name") ?? string.Empty, Hash = hash };
            if (root.TryGetProperty("ecus", out var ecus) && ecus.ValueKind == JsonValueKind.Object)
            {
                foreach (var ecu in ecus.EnumerateObject())
                {
                    var name = Str(ecu.Value, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        profile.Modules[ecu.Name] = name;
                    }
                }
            }
            if (root.TryGetProperty("signals", out var signals) && signals.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in signals.EnumerateArray())
                {
                    var id = Str(s, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    profile.Signals.Add(new CanarySignalDef
                    {
                        Id = id,
                        Name = Str(s, "name") ?? id,
                        Unit = Str(s, "unit") ?? string.Empty,
                        Group = Str(s, "group") ?? string.Empty,
                        Rate = Str(s, "rate") ?? "medium",
                        Dec = Decimals(Num(s, "dec", 1))
                    });
                }
            }
            if (root.TryGetProperty("monitors", out var monitors) && monitors.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in monitors.EnumerateArray())
                {
                    var id = Str(m, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    var check = new CanaryCheck
                    {
                        Id = id,
                        Title = Str(m, "title") ?? id,
                        Category = Str(m, "category") ?? "General",
                        Unit = Str(m, "unit") ?? string.Empty,
                        Stat = Str(m, "stat") ?? "mean",
                        Dec = Decimals(Num(m, "dec", 1)),
                        MinTimeS = Num(m, "min_time_s", 10),
                        SettleS = Num(m, "settle_s", 0),
                        When = Str(m, "when") ?? string.Empty,
                        Value = Str(m, "value") ?? string.Empty,
                        Ok = Str(m, "ok"),
                        Needs = Str(m, "needs"),
                        High = Text(m)
                    };
                    if (m.TryGetProperty("low", out var low) && low.ValueKind == JsonValueKind.Object)
                    {
                        check.Low = Text(low);
                    }
                    if (m.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var l in levels.EnumerateArray())
                        {
                            if (l.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }
                            var severity = CanaryJson.ParseSeverity(Str(l, "severity"));
                            if (!severity.IsFinding())
                            {
                                continue;
                            }
                            foreach (var op in new[] { "gt", "ge", "lt", "le" })
                            {
                                if (l.TryGetProperty(op, out var threshold) && threshold.ValueKind == JsonValueKind.Number && double.IsFinite(threshold.GetDouble()))
                                {
                                    check.Levels.Add(new CanaryLevel { Severity = severity, Op = op, Value = threshold.GetDouble() });
                                }
                            }
                        }
                    }
                    profile.Checks.Add(check);
                }
            }
            return profile;
        }

        /// <summary>Decimal places as rounding accepts them (the device stores them in a byte).</summary>
        private static int Decimals(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, 6) : 1;

        private static CanaryCheckText Text(JsonElement e)
        {
            var text = new CanaryCheckText { Alert = Str(e, "alert"), Summary = Str(e, "summary"), Detail = Str(e, "detail") };
            if (e.TryGetProperty("causes", out var causes) && causes.ValueKind == JsonValueKind.Array)
            {
                text.Causes.AddRange(causes.EnumerateArray().Select(CanaryJson.Str).OfType<string>());
            }
            return text;
        }
        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? CanaryJson.Str(v) : null;
        private static double Num(JsonElement e, string name, double fallback) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : fallback;
    }

    /// <summary>
    /// Everything a check has seen, in a form that can be stored and later merged with other drives.
    /// Mirrors the accumulators of the device's monitor engine.
    /// </summary>
    public sealed class CanaryCheckState
    {
        public const int Reservoir = 128;
        /// <summary>Half-second ticks that counted.</summary>
        public long N { get; set; }
        public long NTrue { get; set; }
        public long Edges { get; set; }
        public double Sum { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double First { get; set; }
        public double Last { get; set; }
        public bool Reached { get; set; }
        public double TimeToS { get; set; }
        /// <summary>The check does not apply to this drive.</summary>
        public bool Skip { get; set; }
        public long ResSeen { get; set; }
        public float[] Res { get; set; } = Array.Empty<float>();
    }

    public sealed class CanaryCheckResult
    {
        public string Id { get; set; } = string.Empty;
        public CanarySeverity Status { get; set; } = CanarySeverity.NoData;
        public double? Value { get; set; }
        /// <summary>True when a "less than" level of a two-sided check tripped.</summary>
        public bool Low { get; set; }
        /// <summary>Seconds of the right driving conditions that were seen.</summary>
        public double SeenS { get; set; }
        /// <summary>Why the check is unavailable, when it is.</summary>
        public string? Missing { get; set; }
        public CanaryCheckState? State { get; set; }
    }

    public sealed class CanarySignalStats
    {
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Rate { get; set; } = string.Empty;
        public double Min { get; set; }
        public double Max { get; set; }
        public double Avg { get; set; }
        public double First { get; set; }
        public double Last { get; set; }
        /// <summary>Samples in the log.</summary>
        public long N { get; set; }
    }

    public sealed class CanaryCode
    {
        public string Code { get; set; } = string.Empty;
        /// <summary>stored, pending or permanent.</summary>
        public string Kind { get; set; } = "stored";
        public string? Ecu { get; set; }
        public string? Module { get; set; }
        public int Ftb { get; set; }
        public int Status { get; set; }
    }

    /// <summary>One drive, as analysed by the server from its raw log.</summary>
    public sealed class CanaryTrip
    {
        public const int CurrentVersion = 1;
        /// <summary>Version of the analysis that produced this summary; older ones are redone.</summary>
        public int V { get; set; } = CurrentVersion;
        public string Id { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Fw { get; set; } = string.Empty;
        public string Vin { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string ProfileHash { get; set; } = string.Empty;
        /// <summary>Unix time the engine started. Zero when the device never learned the time.</summary>
        public long Start { get; set; }
        public double DurationS { get; set; }
        public double DistanceKm { get; set; }
        public double MaxSpeed { get; set; }
        public double FuelL { get; set; }
        public double IdleS { get; set; }
        public double? OdoStartKm { get; set; }
        public double? OdoEndKm { get; set; }
        public long Samples { get; set; }
        /// <summary>Size of the raw log in bytes as the device sent it (before compression here).</summary>
        public long RawSize { get; set; }
        /// <summary>Hash of the profile the checks were evaluated with; differs from ProfileHash when a stand-in was used.</summary>
        public string ProfileUsed { get; set; } = string.Empty;
        /// <summary>Characters of log text that were read.</summary>
        public long Bytes { get; set; }
        public long ReceivedAt { get; set; }
        public string? EndReason { get; set; }
        public bool RawDeleted { get; set; }
        public CanarySeverity Worst { get; set; } = CanarySeverity.NoData;
        public Dictionary<string, CanarySignalStats> Signals { get; set; } = new();
        public List<CanaryCheckResult> Checks { get; set; } = new();
        public List<CanaryCode> Codes { get; set; } = new();
    }

    /// <summary>What the device said the last time it checked in.</summary>
    public sealed class CanaryDevice
    {
        public string Device { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Fw { get; set; } = string.Empty;
        public string Board { get; set; } = string.Empty;
        public string Vin { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string ProfileHash { get; set; } = string.Empty;
        public double? OdometerKm { get; set; }
        public long OdometerTime { get; set; }
        public double? BatteryV { get; set; }
        public string State { get; set; } = string.Empty;
        public int TzOffsetMin { get; set; }
        public int PendingTrips { get; set; }
        public long StorageTotal { get; set; }
        public long StorageFree { get; set; }
        public string Worst { get; set; } = "nodata";
        public bool Mil { get; set; }
        public List<CanaryDeviceFinding> Findings { get; set; } = new();
        public List<CanaryCode> Codes { get; set; } = new();
        /// <summary>Server time of the check-in.</summary>
        public long SeenAt { get; set; }
        public long FirstSeenAt { get; set; }
        public int Checkins { get; set; }
        /// <summary>Odometer value last written to the vehicle's records, in the vehicle's unit.</summary>
        public int LastOdometerRecorded { get; set; }
        public long LastOdometerRecordedAt { get; set; }
        /// <summary>When the device read that recorded value, by the device's clock.</summary>
        public long LastOdometerReadAt { get; set; }
    }

    /// <summary>A check result as the device itself evaluated it on the latest drive.</summary>
    public sealed class CanaryDeviceFinding
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Headline { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Status { get; set; } = "nodata";
        public double? Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public int Dec { get; set; } = 1;
        public string Text { get; set; } = string.Empty;
        public string? Tpl { get; set; }
    }

    /// <summary>Per-vehicle choices for CANary.</summary>
    public sealed class CanarySettings
    {
        /// <summary>Write an odometer record whenever the device reports a higher reading.</summary>
        public bool RecordOdometer { get; set; } = true;
        /// <summary>"mi" or "km": the unit this vehicle's odometer records are kept in.</summary>
        public string OdometerUnit { get; set; } = string.Empty;
        /// <summary>Added to every reading before it is recorded, in the vehicle's unit: for a replaced instrument cluster.</summary>
        public int OdometerOffset { get; set; }
        /// <summary>Weeks run Sunday to Saturday instead of Monday to Sunday.</summary>
        public bool WeekStartsSunday { get; set; }
        /// <summary>Delete raw logs older than this many months; 0 keeps them for good.</summary>
        public int RawRetentionMonths { get; set; }
        /// <summary>Send an ntfy message when a new warning or critical finding appears.</summary>
        public bool Notify { get; set; } = true;
        /// <summary>Also send a short summary once the week has ended.</summary>
        public bool WeeklyDigest { get; set; } = true;
        /// <summary>Check ids the owner chose to stop hearing about.</summary>
        public List<string> Muted { get; set; } = new();
    }
}
