using System.IO.Compression;
using System.Text.Json;
using CarCareTracker.Canary;

// Self-test for the CANary analysis code. Exits non-zero when anything disagrees with the device.
int checks = 0, failures = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok)
    {
        failures++;
        Console.WriteLine($"  FAIL {what}");
    }
}

string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");

// 1. The expression language: every vector the firmware passes, this must pass too.
{
    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expr_vectors.json")));
    var names = doc.RootElement.GetProperty("vars").EnumerateArray().Select(x => x.GetString()!).ToList();
    foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        string source = c.GetProperty("expr").GetString()!;
        var vars = names.Select(_ => double.NaN).ToArray();
        foreach (var v in c.GetProperty("vars").EnumerateObject())
        {
            vars[names.IndexOf(v.Name)] = v.Value.ValueKind == JsonValueKind.Null ? double.NaN : v.Value.GetDouble();
        }
        var expr = CanaryExpr.Compile(source, n => names.IndexOf(n), out string error);
        bool wantError = c.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.True;
        if (wantError)
        {
            Check(expr == null, $"'{source}' should not compile");
            continue;
        }
        Check(expr != null, $"'{source}' failed to compile: {error}");
        if (expr == null)
        {
            continue;
        }
        double got = expr.Eval(vars);
        var expect = c.GetProperty("expect");
        if (expect.ValueKind == JsonValueKind.Null)
        {
            Check(double.IsNaN(got), $"'{source}' = {got}, expected no value");
        }
        else
        {
            Check(Math.Abs(got - expect.GetDouble()) < 1e-9, $"'{source}' = {got}, expected {expect.GetDouble()}");
        }
    }
    Console.WriteLine($"expressions: {checks} checks");
}

// 2. A recorded drive: the server must reach the verdicts the device reached on its own.
{
    int before = checks;
    var profile = CanaryProfile.Parse(File.ReadAllText(Path.Combine(dir, "profile.json")), "fixture");
    Check(profile.Checks.Count >= 15 && profile.Signals.Count >= 40, "profile has its checks and signals");
    Check(profile.Checks.First(x => x.Id == "fuel_trim_idle").Low != null, "two-sided check keeps its low side");

    CanaryTrip trip;
    using (var raw = new GZipStream(File.OpenRead(Path.Combine(dir, "trip.cnl.gz")), CompressionMode.Decompress))
    {
        trip = CanaryLog.Analyze(raw, _ => profile);
    }
    using var device = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "device-summary.json")));
    var d = device.RootElement;
    Check(trip.Id == d.GetProperty("id").GetString(), "trip id");
    Check(trip.Start == d.GetProperty("start").GetInt64(), "start time");
    Check(Math.Abs(trip.DurationS - d.GetProperty("duration_s").GetDouble()) <= 3, $"duration {trip.DurationS}");
    Check(Math.Abs(trip.DistanceKm - d.GetProperty("distance_km").GetDouble()) <= 0.2, $"distance {trip.DistanceKm}");
    Check(Math.Abs(trip.MaxSpeed - d.GetProperty("max_speed").GetDouble()) <= 1, $"top speed {trip.MaxSpeed}");
    Check(Math.Abs(trip.FuelL - d.GetProperty("fuel_l").GetDouble()) <= 0.05, $"fuel {trip.FuelL}");
    Check(trip.OdoEndKm != null && Math.Abs(trip.OdoEndKm.Value - d.GetProperty("odo_end_km").GetDouble()) <= 1, "odometer");
    Check(trip.Codes.Select(x => x.Code).OrderBy(x => x).SequenceEqual(d.GetProperty("dtcs").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).OrderBy(x => x)), "trouble codes");

    Check(trip.Codes.First(x => x.Code == "P0299").Kind == "pending" && trip.Codes.First(x => x.Code == "C10B8").Kind == "stored", "code kinds");
    Check(trip.Codes.First(x => x.Code == "C10B8").Module == "Brakes / ABS" && trip.Codes.First(x => x.Code == "P0299").Module == "Engine (OBD-II)", "codes name their control unit");
    Check(profile.ModuleName("abs") == "Brakes / ABS" && profile.ModuleName("obd") == "Engine (OBD-II)", "control unit names");

    // Logs from the first firmware builds wrote the kind as a number and left the control unit's name out.
    string early = "#CNL 1\n#device d\n#fw 0.0.9\n#vin X\n#profile p 00000000\n#trip 20260101T000000Z-aaaa\n#start 1790000000\n#sig 0 rpm rpm fast Engine speed\n"
        + "!0 dtc {\"codes\":[{\"code\":\"P0299\",\"kind\":2,\"src\":2024},{\"code\":\"P0299\",\"kind\":1,\"src\":2024},{\"code\":\"P0171\",\"kind\":2,\"src\":2024},"
        + "{\"code\":\"C10B8\",\"kind\":1,\"ecu\":\"abs\",\"status\":9},{\"code\":\"P0420\",\"kind\":3,\"src\":2024},{\"code\":\"U0121\",\"kind\":1,\"ecu\":\"mystery\"}]}\n"
        + "500 0 800\n1000 0 810\n!1500 end {\"reason\":\"ignition off\"}\n";
    var old = CanaryLog.Analyze(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(early)), _ => profile);
    string Kind(string code) => old.Codes.First(x => x.Code == code).Kind;
    Check(old.Codes.Count == 5, $"early log: {old.Codes.Count} codes, expected 5");
    Check(Kind("P0299") == "stored" && Kind("P0171") == "pending" && Kind("P0420") == "permanent", "early log: numeric kinds are understood, stored wins over pending");
    Check(old.Codes.First(x => x.Code == "C10B8").Module == "Brakes / ABS" && old.Codes.First(x => x.Code == "P0171").Module == "Engine (OBD-II)", "early log: control units are named from the profile");
    Check(old.Codes.First(x => x.Code == "U0121").Module == "mystery", "early log: an unknown control unit keeps its id");
    Check(old.EndReason == "ignition off" && old.Samples == 2, "early log: events and samples");

    foreach (var f in d.GetProperty("findings").EnumerateArray())
    {
        string id = f.GetProperty("id").GetString()!;
        var mine = trip.Checks.FirstOrDefault(x => x.Id == id);
        var check = profile.Checks.First(x => x.Id == id);
        Check(mine != null, $"{id}: missing from the server's analysis");
        if (mine == null)
        {
            continue;
        }
        double theirs = f.GetProperty("value").GetDouble();
        string theirStatus = f.GetProperty("status").GetString()!;
        // The device ticks on its own clock, the replay on an even grid: values agree closely, not exactly.
        double tolerance = Math.Max(Math.Abs(theirs) * 0.06, check.Unit == "s" ? 3 : 0.5);
        Check(mine.Value != null && Math.Abs(mine.Value.Value - theirs) <= tolerance, $"{id}: value {mine.Value} vs device {theirs}");
        bool nearThreshold = check.Levels.Any(l => Math.Abs(l.Value - theirs) <= tolerance);
        Check(mine.Status.Name() == theirStatus || nearThreshold, $"{id}: status {mine.Status.Name()} vs device {theirStatus}");
    }
    // A four-cylinder has one bank and this car reports no catalyst temperature: those checks are not
    // waiting for more driving, they cannot run on this car, and the report should say so.
    var bank = trip.Checks.First(x => x.Id == "fuel_trim_bank_split");
    Check(bank.Status == CanarySeverity.Unavailable && bank.Missing == "Total fuel trim, bank 2", $"a check on a reading the car never sends is unavailable ({bank.Status}, {bank.Missing})");
    Check(trip.Checks.First(x => x.Id == "catalyst_overheat").Status == CanarySeverity.Unavailable, "catalyst temperature is not reported by this car");
    Check(trip.Checks.Count(x => x.Status == CanarySeverity.Unavailable) == 2, "and only those two");

    var idle = trip.Checks.First(x => x.Id == "fuel_trim_idle");
    Check(idle.Status == CanarySeverity.Advisory && !idle.Low, "lean idle is an advisory on the high side");
    Check(profile.Checks.First(x => x.Id == "fuel_trim_idle").Headline(idle.Status, idle.Low) == "Running lean at idle", "headline follows the side");

    // 3. Merging drives: one drive merged alone is itself; two copies keep the statistics that should not move.
    foreach (var result in trip.Checks.Where(x => x.State != null && x.State.N > 0))
    {
        var check = profile.Checks.First(x => x.Id == result.Id);
        if (check.PerTrip)
        {
            continue; /* "time to warm up" and the like are judged drive by drive, never pooled */
        }
        var alone = CanaryCheckEngine.Evaluate(check, CanaryCheckEngine.Merge(check.Id, new[] { result.State! }), trip.DurationS);
        Check(alone.Status == result.Status && Nearly(alone.Value, result.Value, 1e-9), $"{check.Id}: merging one drive changes nothing");
        var twice = CanaryCheckEngine.Evaluate(check, CanaryCheckEngine.Merge(check.Id, new[] { result.State!, result.State! }), trip.DurationS);
        double slack = check.Stat.StartsWith('p') ? Math.Max(Math.Abs(result.Value ?? 0) * 0.15, 0.6) : 1e-6;
        Check(Nearly(twice.Value, result.Value, slack), $"{check.Id}: two identical drives give {twice.Value}, one gives {result.Value}");
    }
    Console.WriteLine($"recorded drive: {checks - before} checks ({trip.Samples} samples, {trip.Checks.Count} health checks)");

    // 4. Charts read straight from the raw log.
    using var again = new GZipStream(File.OpenRead(Path.Combine(dir, "trip.cnl.gz")), CompressionMode.Decompress);
    var (duration, series) = CanaryLog.ReadSeries(again, new[] { "rpm", "speed", "no_such_signal" }, 200);
    Check(series.Count == 2 && series[0].T.Count > 100 && series[0].T.Count <= 200, "series are downsampled");
    Check(Math.Abs(duration - trip.DurationS) <= 1, "series duration");
    Check(series[1].Hi.Max() >= trip.MaxSpeed - 1, "series keep the extremes");
}

Console.WriteLine(failures == 0 ? $"\n{checks} checks, all passed" : $"\n{checks} checks, {failures} FAILED");
return failures == 0 ? 0 : 1;

static bool Nearly(double? a, double? b, double tolerance) => a != null && b != null && Math.Abs(a.Value - b.Value) <= tolerance;
