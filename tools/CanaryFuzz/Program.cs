using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CarCareTracker.Canary;

// Mutation fuzzing of the CANary parsers. Exits non-zero on the first failure and saves the input.
int seconds = args.Length > 0 ? int.Parse(args[0]) : 60;
int seed = args.Length > 1 ? int.Parse(args[1]) : Environment.TickCount;
var rng = new Random(seed);
string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
string outDir = Path.Combine(Environment.CurrentDirectory, "fuzz-failures");

byte[] trip;
using (var gz = new GZipStream(File.OpenRead(Path.Combine(dir, "trip.cnl.gz")), CompressionMode.Decompress))
using (var ms = new MemoryStream())
{
    gz.CopyTo(ms);
    trip = ms.ToArray();
}
string profileText = File.ReadAllText(Path.Combine(dir, "profile.json"));
var profile = CanaryProfile.Parse(profileText, "fixture");
string[] exprs = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expr_vectors.json"))).RootElement.GetProperty("cases")
    .EnumerateArray().Select(c => c.GetProperty("expr").GetString()!).ToArray();
string[] varNames = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expr_vectors.json"))).RootElement.GetProperty("vars")
    .EnumerateArray().Select(x => x.GetString()!).ToArray();
string[] tokens = { "\n", " ", "!", "#", "#sig ", "#start ", "#profile ", "#trip ", "-", "e308", "NaN", "Infinity", "1e999", "-0", "9999999999999999999",
    "{", "}", "[", "]", "\"", ":", ",", "null", "true", "é", "\0", "\r\n", "!0 dtc {\"codes\":[{\"code\":\"P0299\",\"kind\":\"stored\"}]}\n",
    "!5 end {\"reason\":\"x\"}\n", "0 0 ", "-5 1 2\n", "2147483648 3 1\n", " 64 ", " -1 ", "(", ")", "?", "&&", "||", "/", "%", "min(", "max(", "abs(", "1/0" };

byte[] Mutate(byte[] src, int maxLen)
{
    var b = new List<byte>(src);
    int rounds = 1 + rng.Next(rng.Next(2) == 0 ? 4 : 40);
    for (int r = 0; r < rounds; r++)
    {
        int pos = b.Count == 0 ? 0 : rng.Next(b.Count);
        switch (rng.Next(9))
        {
            case 0: if (b.Count > 0) b[pos] ^= (byte)(1 << rng.Next(8)); break;
            case 1: if (b.Count > 0) b[pos] = (byte)rng.Next(256); break;
            case 2: b.Insert(pos, (byte)rng.Next(256)); break;
            case 3: if (b.Count > 0) b.RemoveRange(pos, Math.Min(b.Count - pos, 1 + rng.Next(64))); break;
            case 4: b.InsertRange(pos, Encoding.UTF8.GetBytes(tokens[rng.Next(tokens.Length)])); break;
            case 5: if (b.Count > 0) { int len = Math.Min(b.Count - pos, 1 + rng.Next(200)); b.InsertRange(rng.Next(b.Count), b.GetRange(pos, len)); } break;
            case 6: if (b.Count > pos) b.RemoveRange(pos, b.Count - pos); break; /* torn at power loss */
            case 7: { /* swap a number for an extreme one */ string[] n = { "0", "-1", "1e308", "-1e308", "NaN", "4294967296", "-9223372036854775808" }; b.InsertRange(pos, Encoding.ASCII.GetBytes(n[rng.Next(n.Length)])); } break;
            case 8: if (b.Count > 0) { int len = Math.Min(b.Count - pos, 1 + rng.Next(16)); for (int i = 0; i < len; i++) b[pos + i] = (byte)"0123456789 -.\n"[rng.Next(14)]; } break;
        }
    }
    if (b.Count > maxLen) b.RemoveRange(maxLen, b.Count - maxLen);
    return b.ToArray();
}

void Store(object value) => JsonSerializer.Serialize(value, CanaryJson.Options); /* how summaries are stored */
void Serialize(object value)
{
    Store(value);
    JsonSerializer.Serialize(value); /* how the controller replies */
}

var targets = new (string Name, Action<byte[]> Run, Func<byte[]> Next)[]
{
    ("raw log analysis", input =>
    {
        using var s = new MemoryStream(input);
        var t = CanaryLog.Analyze(s, h => rng.Next(4) == 0 ? null : profile, 1795000000);
        Store(t);
        using var s2 = new MemoryStream(input);
        var (dur, series) = CanaryLog.ReadSeries(s2, new[] { "rpm", "speed", "coolant_temp", "nope" }, 1 + rng.Next(600));
        Serialize(new { dur, series });
        using var s3 = new MemoryStream(input);
        CanaryLog.ReadHeader(s3);
    }, () => Mutate(rng.Next(3) == 0 ? trip.AsSpan(0, Math.Min(trip.Length, 20000)).ToArray() : trip, trip.Length + 4096)),
    ("profile", input =>
    {
        string text = Encoding.UTF8.GetString(input);
        CanaryProfile p;
        try { p = CanaryProfile.Parse(text, "fuzz0000"); }
        catch (JsonException) { return; } /* the one failure SaveProfile expects */
        using var s = new MemoryStream(trip, 0, Math.Min(trip.Length, 60000));
        Store(CanaryLog.Analyze(s, _ => p, 0));
    }, () => Mutate(Encoding.UTF8.GetBytes(profileText), 64000)),
    ("expression", input =>
    {
        var e = CanaryExpr.Compile(Encoding.UTF8.GetString(input), n => Array.IndexOf(varNames, n), out _);
        if (e == null) return;
        var vars = varNames.Select(_ => rng.Next(5) switch { 0 => double.NaN, 1 => 0, 2 => 1e308, 3 => -1e308, _ => rng.NextDouble() * 200 - 100 }).ToArray();
        e.Eval(vars);
    }, () => Mutate(Encoding.UTF8.GetBytes(exprs[rng.Next(exprs.Length)]), 2000)),
    ("trouble code", input =>
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            var c = CanaryLog.ReadCode(doc.RootElement);
            if (c != null) Serialize(c);
        }
        catch (JsonException) { }
    }, () => Mutate(Encoding.UTF8.GetBytes("{\"code\":\"P0299\",\"kind\":\"stored\",\"ecu\":\"engine\",\"module\":\"Engine\",\"ftb\":17,\"status\":9}"), 400)),
};

var clock = Stopwatch.StartNew();
long runs = 0;
byte[] current = Array.Empty<byte>();
string currentName = "";
long startedAt = 0;
/* a hang never returns to the loop, so a watchdog saves the input that caused it */
new Thread(() =>
{
    while (true)
    {
        Thread.Sleep(500);
        long since = Volatile.Read(ref startedAt);
        if (since != 0 && clock.ElapsedMilliseconds - since > 8000)
        {
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, $"{currentName.Replace(' ', '-')}-hang-{seed}.bin");
            File.WriteAllBytes(file, current);
            Console.WriteLine($"HANG {currentName}: over 8 s, input saved to {file}");
            Environment.Exit(2);
        }
    }
}) { IsBackground = true }.Start();
var perTarget = new long[targets.Length];
while (clock.Elapsed.TotalSeconds < seconds)
{
    int k = (int)(runs % targets.Length);
    var input = targets[k].Next();
    var one = Stopwatch.StartNew();
    current = input;
    currentName = targets[k].Name;
    Volatile.Write(ref startedAt, clock.ElapsedMilliseconds);
    try
    {
        targets[k].Run(input);
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, $"{targets[k].Name.Replace(' ', '-')}-{seed}-{runs}.bin");
        File.WriteAllBytes(file, input);
        Console.WriteLine($"FAIL {targets[k].Name}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n  input saved to {file}");
        return 1;
    }
    if (one.ElapsedMilliseconds > 3000)
    {
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, $"{targets[k].Name.Replace(' ', '-')}-slow-{seed}-{runs}.bin");
        File.WriteAllBytes(file, input);
        Console.WriteLine($"SLOW {targets[k].Name}: {one.ElapsedMilliseconds} ms, input saved to {file}");
        return 1;
    }
    Volatile.Write(ref startedAt, 0);
    perTarget[k]++;
    runs++;
}
Console.WriteLine($"seed {seed}: {runs} inputs in {seconds} s, no failures ({string.Join(", ", targets.Select((t, i) => $"{t.Name} {perTarget[i]}"))})");
return 0;
