using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarCareTracker.Canary
{
    /// <summary>
    /// Where CANary data lives on the server: plain files under data/canary, so they work the same with
    /// either database and can be backed up, inspected or removed by hand.
    ///
    ///   profiles/&lt;hash&gt;.json           vehicle profiles as the device uploaded them
    ///   v&lt;vehicle&gt;/device.json          the device's last check-in
    ///   v&lt;vehicle&gt;/settings.json        the owner's choices for this vehicle
    ///   v&lt;vehicle&gt;/trips/&lt;id&gt;.cnl.part   an upload in progress
    ///   v&lt;vehicle&gt;/trips/&lt;id&gt;.cnl.gz     a complete raw log
    ///   v&lt;vehicle&gt;/trips/&lt;id&gt;.json       the server's analysis of that log
    /// </summary>
    public sealed class CanaryStore
    {
        public const int MaxChunkBytes = 8 * 1024 * 1024;
        /// <summary>A day of non-stop driving records well under this; anything larger is a mistake or mischief.</summary>
        public const long MaxTripBytes = 256L * 1024 * 1024;
        private static readonly Regex TripIdPattern = new("^[0-9A-Za-z][0-9A-Za-z-]{5,47}$", RegexOptions.Compiled);
        private static readonly Regex HashPattern = new("^[0-9a-f]{8}$", RegexOptions.Compiled);

        private readonly string _root;
        private readonly ConcurrentDictionary<int, object> _locks = new();
        private readonly ConcurrentDictionary<int, List<CanaryTrip>> _trips = new();
        private readonly ConcurrentDictionary<string, CanaryProfile?> _profiles = new();

        public CanaryStore(string root)
        {
            _root = root;
        }

        public static bool ValidTripId(string? id) => id != null && TripIdPattern.IsMatch(id);
        public static bool ValidHash(string? hash) => hash != null && HashPattern.IsMatch(hash);

        private object LockFor(int vehicleId) => _locks.GetOrAdd(vehicleId, _ => new object());
        private string VehicleDir(int vehicleId) => Path.Combine(_root, $"v{vehicleId}");
        private string TripsDir(int vehicleId) => Path.Combine(VehicleDir(vehicleId), "trips");
        private string PartPath(int vehicleId, string tripId) => Path.Combine(TripsDir(vehicleId), tripId + ".cnl.part");
        private string RawPath(int vehicleId, string tripId) => Path.Combine(TripsDir(vehicleId), tripId + ".cnl.gz");
        private string SummaryPath(int vehicleId, string tripId) => Path.Combine(TripsDir(vehicleId), tripId + ".json");
        private string ProfilePath(string hash) => Path.Combine(_root, "profiles", hash + ".json");

        private static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        private static T? ReadJson<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), CanaryJson.Options) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }

        /* ───── device and settings ───── */

        public bool HasDevice(int vehicleId) => File.Exists(Path.Combine(VehicleDir(vehicleId), "device.json"));

        public CanaryDevice? GetDevice(int vehicleId) => ReadJson<CanaryDevice>(Path.Combine(VehicleDir(vehicleId), "device.json"));

        public void SaveDevice(int vehicleId, CanaryDevice device)
        {
            lock (LockFor(vehicleId))
            {
                WriteAtomic(Path.Combine(VehicleDir(vehicleId), "device.json"), JsonSerializer.Serialize(device, CanaryJson.Options));
            }
        }

        public CanarySettings GetSettings(int vehicleId) => ReadJson<CanarySettings>(Path.Combine(VehicleDir(vehicleId), "settings.json")) ?? new CanarySettings();

        public void SaveSettings(int vehicleId, CanarySettings settings)
        {
            lock (LockFor(vehicleId))
            {
                WriteAtomic(Path.Combine(VehicleDir(vehicleId), "settings.json"), JsonSerializer.Serialize(settings, CanaryJson.Options));
            }
        }

        /// <summary>Small pieces of per-vehicle state kept beside the data, such as which alerts were sent.</summary>
        public T? ReadState<T>(int vehicleId, string name) where T : class => ReadJson<T>(Path.Combine(VehicleDir(vehicleId), name));

        public void WriteState<T>(int vehicleId, string name, T value) where T : class
        {
            lock (LockFor(vehicleId))
            {
                WriteAtomic(Path.Combine(VehicleDir(vehicleId), name), JsonSerializer.Serialize(value, CanaryJson.Options));
            }
        }

        /* ───── profiles ───── */

        public bool HasProfile(string hash) => ValidHash(hash) && File.Exists(ProfilePath(hash));

        /// <summary>FNV-1a, 32 bit: the hash the device computes over the same bytes.</summary>
        public static string ProfileHash(ReadOnlySpan<byte> json)
        {
            uint h = 2166136261u;
            foreach (byte b in json)
            {
                h ^= b;
                h *= 16777619u;
            }
            return h.ToString("x8");
        }

        public bool SaveProfile(string hash, byte[] json, out string error)
        {
            error = string.Empty;
            if (!ValidHash(hash))
            {
                error = "bad profile hash";
                return false;
            }
            if (ProfileHash(json) != hash)
            {
                error = "profile does not match its hash";
                return false;
            }
            try
            {
                CanaryProfile.Parse(Encoding.UTF8.GetString(json), hash);
            }
            catch (JsonException)
            {
                error = "profile is not valid JSON";
                return false;
            }
            Directory.CreateDirectory(Path.Combine(_root, "profiles"));
            File.WriteAllBytes(ProfilePath(hash), json);
            _profiles.TryRemove(hash, out _);
            return true;
        }

        public CanaryProfile? GetProfile(string? hash)
        {
            if (!ValidHash(hash))
            {
                return null;
            }
            return _profiles.GetOrAdd(hash!, h =>
            {
                try
                {
                    return File.Exists(ProfilePath(h)) ? CanaryProfile.Parse(File.ReadAllText(ProfilePath(h)), h) : null;
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    return null;
                }
            });
        }

        /// <summary>The profile a log was recorded with or, failing that, the one the vehicle's device uses now.</summary>
        public CanaryProfile? ProfileFor(int vehicleId, string? hash)
        {
            return GetProfile(hash) ?? GetProfile(GetDevice(vehicleId)?.ProfileHash);
        }

        /* ───── uploads ───── */

        /// <summary>Bytes of a trip's raw log the server already holds, and whether that is all of it.</summary>
        public (long Received, bool Complete) UploadStatus(int vehicleId, string tripId)
        {
            lock (LockFor(vehicleId))
            {
                var done = GetTrips(vehicleId).FirstOrDefault(x => x.Id == tripId);
                if (done != null)
                {
                    return (done.RawSize, true);
                }
                var part = new FileInfo(PartPath(vehicleId, tripId));
                return (part.Exists ? part.Length : 0, false);
            }
        }

        public sealed class ChunkResult
        {
            public long Received;
            public bool Complete;
            public string? Error;
            /// <summary>Set when this chunk finished a trip.</summary>
            public CanaryTrip? Trip;
        }

        /// <summary>
        /// Appends one chunk of a raw log. Uploads resume: a chunk is only accepted at the offset the
        /// server has reached, and its checksum must match. The last chunk triggers the analysis.
        /// </summary>
        public ChunkResult AppendChunk(int vehicleId, string tripId, long offset, long total, uint crc, byte[] body, long startIfUnknown)
        {
            lock (LockFor(vehicleId))
            {
                var done = GetTrips(vehicleId).FirstOrDefault(x => x.Id == tripId);
                if (done != null)
                {
                    return new ChunkResult { Received = done.RawSize, Complete = true };
                }
                if (total <= 0 || offset < 0 || body.Length > MaxChunkBytes)
                {
                    return new ChunkResult { Error = "bad chunk" };
                }
                if (total > MaxTripBytes)
                {
                    return new ChunkResult { Error = "drive too large" };
                }
                Directory.CreateDirectory(TripsDir(vehicleId));
                string part = PartPath(vehicleId, tripId);
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (offset != have || offset + body.Length > total)
                {
                    /* out of step: say where we are and let the device continue from there */
                    return new ChunkResult { Received = have };
                }
                if (body.Length > 0)
                {
                    if (Crc32(body) != crc)
                    {
                        return new ChunkResult { Received = have, Error = "checksum mismatch" };
                    }
                    using var file = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None);
                    file.Write(body, 0, body.Length);
                    have += body.Length;
                }
                if (have < total)
                {
                    return new ChunkResult { Received = have };
                }
                var trip = Complete(vehicleId, tripId, startIfUnknown);
                return new ChunkResult { Received = have, Complete = true, Trip = trip };
            }
        }

        private CanaryTrip Complete(int vehicleId, string tripId, long startIfUnknown)
        {
            string part = PartPath(vehicleId, tripId), raw = RawPath(vehicleId, tripId);
            long size = new FileInfo(part).Length;
            using (var input = File.OpenRead(part))
            using (var output = new GZipStream(File.Create(raw + ".tmp"), CompressionLevel.Optimal))
            {
                input.CopyTo(output);
            }
            File.Move(raw + ".tmp", raw, true);
            File.Delete(part);
            var trip = Analyze(vehicleId, tripId, startIfUnknown) ?? new CanaryTrip { Id = tripId };
            trip.Id = tripId; /* the file name is the authority, whatever the header says */
            trip.RawSize = size;
            trip.ReceivedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveTrip(vehicleId, trip);
            return trip;
        }

        private CanaryTrip? Analyze(int vehicleId, string tripId, long startIfUnknown)
        {
            try
            {
                using var log = CanaryLog.OpenRaw(RawPath(vehicleId, tripId));
                var trip = CanaryLog.Analyze(log, header => ProfileFor(vehicleId, header.ProfileHash), startIfUnknown);
                trip.ProfileUsed = ProfileFor(vehicleId, trip.ProfileHash)?.Hash ?? string.Empty;
                return trip;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return null;
            }
        }

        private void SaveTrip(int vehicleId, CanaryTrip trip)
        {
            WriteAtomic(SummaryPath(vehicleId, trip.Id), JsonSerializer.Serialize(trip, CanaryJson.Options));
            var list = GetTrips(vehicleId);
            lock (list)
            {
                list.RemoveAll(x => x.Id == trip.Id);
                list.Add(trip);
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }

        /* ───── trips ───── */

        /// <summary>Every analysed drive of a vehicle, oldest first. Loaded once, then kept in memory.</summary>
        public List<CanaryTrip> GetTrips(int vehicleId)
        {
            return _trips.GetOrAdd(vehicleId, id =>
            {
                var list = new List<CanaryTrip>();
                string dir = TripsDir(id);
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        var trip = ReadJson<CanaryTrip>(file);
                        if (trip != null && ValidTripId(trip.Id))
                        {
                            list.Add(trip);
                        }
                    }
                }
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
                return list;
            });
        }

        /// <summary>A snapshot that is safe to enumerate while uploads continue.</summary>
        public List<CanaryTrip> TripsSnapshot(int vehicleId)
        {
            var list = GetTrips(vehicleId);
            lock (list)
            {
                return list.ToList();
            }
        }

        public CanaryTrip? GetTrip(int vehicleId, string tripId) => ValidTripId(tripId) ? TripsSnapshot(vehicleId).FirstOrDefault(x => x.Id == tripId) : null;

        public bool HasRaw(int vehicleId, string tripId) => ValidTripId(tripId) && File.Exists(RawPath(vehicleId, tripId));

        public Stream? OpenRaw(int vehicleId, string tripId) => HasRaw(vehicleId, tripId) ? CanaryLog.OpenRaw(RawPath(vehicleId, tripId)) : null;

        /// <summary>
        /// Analyses again every drive whose summary came from older code, or that was analysed before its
        /// vehicle profile arrived. Returns how many were redone.
        /// </summary>
        public int Reanalyze(int vehicleId, bool all = false)
        {
            int redone = 0;
            lock (LockFor(vehicleId))
            {
                foreach (var old in TripsSnapshot(vehicleId))
                {
                    bool stale = all || old.V < CanaryTrip.CurrentVersion || (old.Checks.Count == 0 && ProfileFor(vehicleId, old.ProfileHash) != null);
                    if (!stale || old.RawDeleted || !HasRaw(vehicleId, old.Id))
                    {
                        continue;
                    }
                    var fresh = Analyze(vehicleId, old.Id, old.Start);
                    if (fresh == null)
                    {
                        continue;
                    }
                    fresh.Id = old.Id;
                    fresh.RawSize = old.RawSize;
                    fresh.ReceivedAt = old.ReceivedAt;
                    SaveTrip(vehicleId, fresh);
                    redone++;
                }
            }
            return redone;
        }

        public bool DeleteTrip(int vehicleId, string tripId)
        {
            if (!ValidTripId(tripId))
            {
                return false;
            }
            lock (LockFor(vehicleId))
            {
                foreach (var path in new[] { SummaryPath(vehicleId, tripId), RawPath(vehicleId, tripId), PartPath(vehicleId, tripId) })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                var list = GetTrips(vehicleId);
                lock (list)
                {
                    return list.RemoveAll(x => x.Id == tripId) > 0;
                }
            }
        }

        /// <summary>Removes raw logs older than the owner's limit. The analysis of each drive stays.</summary>
        public int ApplyRetention(int vehicleId)
        {
            int months = GetSettings(vehicleId).RawRetentionMonths;
            if (months <= 0)
            {
                return 0;
            }
            long cutoff = DateTimeOffset.UtcNow.AddMonths(-months).ToUnixTimeSeconds();
            int removed = 0;
            lock (LockFor(vehicleId))
            {
                foreach (var trip in TripsSnapshot(vehicleId))
                {
                    long when = trip.Start > 0 ? trip.Start : trip.ReceivedAt;
                    if (trip.RawDeleted || when >= cutoff)
                    {
                        continue;
                    }
                    string raw = RawPath(vehicleId, trip.Id);
                    if (File.Exists(raw))
                    {
                        File.Delete(raw);
                    }
                    trip.RawDeleted = true;
                    SaveTrip(vehicleId, trip);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>Bytes of raw logs kept on disk for a vehicle.</summary>
        public long RawBytesOnDisk(int vehicleId)
        {
            string dir = TripsDir(vehicleId);
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.cnl.*").Sum(x => x.Length) : 0;
        }

        /// <summary>Forgets a vehicle entirely: called when the vehicle itself is deleted.</summary>
        public void DeleteVehicle(int vehicleId)
        {
            lock (LockFor(vehicleId))
            {
                string dir = VehicleDir(vehicleId);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
                _trips.TryRemove(vehicleId, out _);
            }
        }

        /// <summary>Vehicles that have a device or data.</summary>
        public List<int> Vehicles()
        {
            var ids = new List<int>();
            if (!Directory.Exists(_root))
            {
                return ids;
            }
            foreach (var dir in Directory.EnumerateDirectories(_root, "v*"))
            {
                if (int.TryParse(Path.GetFileName(dir).AsSpan(1), out int id))
                {
                    ids.Add(id);
                }
            }
            return ids;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        /// <summary>CRC-32 as in zip and gzip: what the device sends with every chunk.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }
    }
}
