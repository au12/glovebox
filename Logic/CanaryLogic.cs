using CarCareTracker.Canary;
using CarCareTracker.External.Interfaces;
using CarCareTracker.Helper;
using CarCareTracker.Models;
using System.Security.Claims;
using System.Text.Json;

namespace CarCareTracker.Logic
{
    public interface ICanaryLogic
    {
        /// <summary>Handles a device check-in: remembers what it reported and records the odometer.</summary>
        CanaryCheckinResult Checkin(int vehicleId, JsonElement payload, ClaimsPrincipal user, int userId);
        /// <summary>Called when a drive has been received and analysed in full.</summary>
        Task TripReceivedAsync(int vehicleId, CanaryTrip trip);
        /// <summary>"mi" or "km": the unit this vehicle's odometer records are kept in.</summary>
        string OdometerUnit(int vehicleId, ClaimsPrincipal user);
        /// <summary>Hourly housekeeping: raw-data retention, re-analysis after an update, the weekly summary.</summary>
        Task HousekeepingAsync();
    }

    public sealed class CanaryCheckinResult
    {
        public bool HaveProfile { get; set; }
        public bool OdometerRecorded { get; set; }
        public int OdometerValue { get; set; }
        public string OdometerUnit { get; set; } = "mi";
    }

    public class CanaryLogic : ICanaryLogic
    {
        public const string OdometerTag = "canary";
        private const double KmToMiles = 0.621371;

        private readonly CanaryStore _store;
        private readonly CanaryReportBuilder _reports;
        private readonly IVehicleDataAccess _vehicles;
        private readonly IOdometerRecordDataAccess _odometerRecords;
        private readonly IVehicleLogic _vehicleLogic;
        private readonly IConfigHelper _config;
        private readonly IEventLogic _events;
        private readonly INtfyNotificationLogic _ntfy;
        private readonly ILogger<CanaryLogic> _logger;

        public CanaryLogic(CanaryStore store, CanaryReportBuilder reports, IVehicleDataAccess vehicles, IOdometerRecordDataAccess odometerRecords,
            IVehicleLogic vehicleLogic, IConfigHelper config, IEventLogic events, INtfyNotificationLogic ntfy, ILogger<CanaryLogic> logger)
        {
            _store = store;
            _reports = reports;
            _vehicles = vehicles;
            _odometerRecords = odometerRecords;
            _vehicleLogic = vehicleLogic;
            _config = config;
            _events = events;
            _ntfy = ntfy;
            _logger = logger;
        }

        public string OdometerUnit(int vehicleId, ClaimsPrincipal user)
        {
            var settings = _store.GetSettings(vehicleId);
            if (settings.OdometerUnit is "mi" or "km")
            {
                return settings.OdometerUnit;
            }
            /* Glovebox keeps distances in miles when fuel economy is in MPG, and in kilometres otherwise. */
            return _config.GetUserConfig(user).UseMPG ? "mi" : "km";
        }

        public CanaryCheckinResult Checkin(int vehicleId, JsonElement payload, ClaimsPrincipal user, int userId)
        {
            var previous = _store.GetDevice(vehicleId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var device = ReadDevice(payload);
            device.SeenAt = now;
            device.FirstSeenAt = previous?.FirstSeenAt > 0 ? previous.FirstSeenAt : now;
            device.Checkins = (previous?.Checkins ?? 0) + 1;
            device.LastOdometerRecorded = previous?.LastOdometerRecorded ?? 0;
            device.LastOdometerRecordedAt = previous?.LastOdometerRecordedAt ?? 0;

            var settings = _store.GetSettings(vehicleId);
            if (settings.OdometerUnit is not ("mi" or "km"))
            {
                /* fix the unit on first contact, so a later change of the user's display preference cannot shift the records */
                settings.OdometerUnit = OdometerUnit(vehicleId, user);
                _store.SaveSettings(vehicleId, settings);
            }
            var result = new CanaryCheckinResult { OdometerUnit = settings.OdometerUnit, HaveProfile = _store.HasProfile(device.ProfileHash) };
            try
            {
                if (settings.RecordOdometer && device.OdometerKm is > 0)
                {
                    int value = ToVehicleUnit(device.OdometerKm.Value, settings);
                    result.OdometerValue = value;
                    result.OdometerRecorded = RecordOdometer(vehicleId, value, device, user, userId);
                    if (result.OdometerRecorded)
                    {
                        device.LastOdometerRecorded = value;
                        device.LastOdometerRecordedAt = now;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CANary: odometer for vehicle {vehicleId} could not be recorded: {ex.Message}");
            }
            _store.SaveDevice(vehicleId, device);
            return result;
        }

        private static int ToVehicleUnit(double km, CanarySettings settings)
        {
            double value = settings.OdometerUnit == "mi" ? km * KmToMiles : km;
            /* odometers show whole units and never round up */
            return (int)Math.Floor(value + 1e-6) + settings.OdometerOffset;
        }

        /// <summary>
        /// Writes the reading to the vehicle's odometer records when it is higher than anything on file.
        /// A day gets one CANary record that is moved forward on later check-ins, so the device reporting
        /// on every Wi-Fi connection does not fill the log with near-identical rows.
        /// </summary>
        private bool RecordOdometer(int vehicleId, int value, CanaryDevice device, ClaimsPrincipal user, int userId)
        {
            int highest = _vehicleLogic.GetMaxMileage(vehicleId);
            if (value <= highest)
            {
                return false;
            }
            var clock = new CanaryClock(device.TzOffsetMin);
            var date = (device.OdometerTime > 0 ? clock.Local(device.OdometerTime) : clock.Now).Date;
            var records = _odometerRecords.GetOdometerRecordsByVehicleId(vehicleId);
            var today = records.Where(x => x.Date.Date == date && x.Tags.Contains(OdometerTag)).OrderByDescending(x => x.Mileage).FirstOrDefault();
            string userName = user.Identity?.Name ?? string.Empty;
            if (today != null)
            {
                today.Mileage = value;
                _odometerRecords.SaveOdometerRecordToVehicle(today);
                _events.PublishEvent(userId, WebHookPayload.FromOdometerRecord(today, "odometerrecord.update.api", userName));
                return true;
            }
            int last = records.Any() ? records.Max(x => x.Mileage) : 0;
            var record = new OdometerRecord
            {
                VehicleId = vehicleId,
                Date = date,
                /* a vehicle with no history starts here rather than appearing to have driven its whole life in a day */
                InitialMileage = last > 0 ? last : (highest > 0 ? highest : value),
                Mileage = value,
                Notes = string.IsNullOrWhiteSpace(device.Name) || device.Name == "CANary" ? "Read by CANary" : $"Read by CANary ({device.Name})",
                Tags = new List<string> { OdometerTag }
            };
            _odometerRecords.SaveOdometerRecordToVehicle(record);
            _events.PublishEvent(userId, WebHookPayload.FromOdometerRecord(record, "odometerrecord.add.api", userName));
            return true;
        }

        private static CanaryDevice ReadDevice(JsonElement p)
        {
            string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            double? Number(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            bool Flag(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var device = new CanaryDevice
            {
                Device = Text(p, "device"),
                Name = Text(p, "name"),
                Fw = Text(p, "fw"),
                Board = Text(p, "board"),
                Vin = Text(p, "vin"),
                OdometerKm = Number(p, "odometer_km"),
                OdometerTime = (long)(Number(p, "odometer_time") ?? 0),
                BatteryV = Number(p, "battery_v"),
                State = Text(p, "state"),
                TzOffsetMin = (int)(Number(p, "tz_offset_min") ?? 0),
                PendingTrips = (int)(Number(p, "pending_trips") ?? 0),
                Worst = Text(p, "worst"),
                Mil = Flag(p, "mil")
            };
            if (p.TryGetProperty("profile", out var profile))
            {
                device.ProfileId = Text(profile, "id");
                device.ProfileName = Text(profile, "name");
                device.ProfileHash = Text(profile, "hash");
            }
            if (p.TryGetProperty("storage", out var storage))
            {
                device.StorageTotal = (long)(Number(storage, "total") ?? 0);
                device.StorageFree = (long)(Number(storage, "free") ?? 0);
            }
            if (p.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in findings.EnumerateArray())
                {
                    device.Findings.Add(new CanaryDeviceFinding
                    {
                        Id = Text(f, "id"),
                        Title = Text(f, "title"),
                        Headline = Text(f, "headline") is { Length: > 0 } headline ? headline : null,
                        Category = Text(f, "category"),
                        Status = Text(f, "status"),
                        Value = Number(f, "value"),
                        Unit = Text(f, "unit"),
                        Dec = (int)(Number(f, "dec") ?? 1),
                        Text = Text(f, "text"),
                        Tpl = Text(f, "tpl") is { Length: > 0 } tpl ? tpl : null
                    });
                }
            }
            if (p.TryGetProperty("dtcs", out var codes) && codes.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in codes.EnumerateArray())
                {
                    var code = CanaryLog.ReadCode(c);
                    if (code != null)
                    {
                        device.Codes.Add(code);
                    }
                }
            }
            return device;
        }

        /* ───── alerts ───── */

        private sealed class AlertState
        {
            /// <summary>Check id or code key, to the severity and time it was last announced.</summary>
            public Dictionary<string, AlertEntry> Sent { get; set; } = new();
            public string? LastDigestWeek { get; set; }
        }
        private sealed class AlertEntry
        {
            public int Severity { get; set; }
            public long At { get; set; }
        }
        private const long RepeatAfterSeconds = 14 * 86400;
        private const long StaleAfterSeconds = 3 * 86400;
        private static readonly SemaphoreSlim AlertLock = new(1, 1);

        private AlertState LoadAlerts(int vehicleId) => _store.ReadState<AlertState>(vehicleId, "alerts.json") ?? new AlertState();
        private void SaveAlerts(int vehicleId, AlertState state)
        {
            try
            {
                _store.WriteState(vehicleId, "alerts.json", state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"CANary: alert state for vehicle {vehicleId} could not be saved: {ex.Message}");
            }
        }

        private static string VehicleName(Vehicle vehicle) => $"{vehicle.Year} {vehicle.Make} {vehicle.Model}".Trim();

        /// <summary>
        /// A drive arrived. Announces a warning or critical finding, or a trouble code, the first time it
        /// appears and again only if it is still there two weeks later or has become more serious.
        /// </summary>
        public async Task TripReceivedAsync(int vehicleId, CanaryTrip trip)
        {
            /* several drives can finish uploading within a second: one at a time, or two of them
               would both decide the same finding is news */
            await AlertLock.WaitAsync();
            try
            {
                var settings = _store.GetSettings(vehicleId);
                var vehicle = _vehicles.GetVehicleById(vehicleId);
                if (!settings.Notify || vehicle == null || vehicle.Id == default)
                {
                    return;
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (trip.Start > 0 && now - trip.Start > StaleAfterSeconds)
                {
                    return; /* a backlog from days ago is history, not news: the weekly summary covers it */
                }
                var profile = _store.ProfileFor(vehicleId, trip.ProfileUsed.Length > 0 ? trip.ProfileUsed : trip.ProfileHash);
                var format = new CanaryFormat(settings.OdometerUnit != "km");
                var state = LoadAlerts(vehicleId);
                var lines = new List<string>();
                var top = CanarySeverity.Advisory;
                bool Due(string key, CanarySeverity severity)
                {
                    state.Sent.TryGetValue(key, out var entry);
                    return entry == null || (int)severity > entry.Severity || now - entry.At >= RepeatAfterSeconds;
                }
                foreach (var result in trip.Checks.Where(x => x.Status >= CanarySeverity.Warning && x.Status.IsFinding()).OrderByDescending(x => x.Status))
                {
                    var check = profile?.Checks.FirstOrDefault(x => x.Id == result.Id);
                    if (check == null || settings.Muted.Contains(check.Id) || !Due(check.Id, result.Status))
                    {
                        continue;
                    }
                    lines.Add($"{check.Headline(result.Status, result.Low)}: {format.Sentence(check.Template(result.Status, result.Low), result.Value, check.Unit, check.Dec)}");
                    state.Sent[check.Id] = new AlertEntry { Severity = (int)result.Status, At = now };
                    if (result.Status > top)
                    {
                        top = result.Status;
                    }
                }
                foreach (var code in trip.Codes.Where(x => x.Kind != "pending"))
                {
                    string key = $"code:{code.Code}|{code.Module ?? code.Ecu}";
                    if (!Due(key, CanarySeverity.Warning))
                    {
                        continue;
                    }
                    lines.Add($"Trouble code {code.Code} stored{(string.IsNullOrEmpty(code.Module) ? string.Empty : $" in {code.Module}")}");
                    state.Sent[key] = new AlertEntry { Severity = (int)CanarySeverity.Warning, At = now };
                    if (top < CanarySeverity.Warning)
                    {
                        top = CanarySeverity.Warning;
                    }
                }
                if (lines.Count == 0)
                {
                    return;
                }
                SaveAlerts(vehicleId, state);
                string title = $"{VehicleName(vehicle)}: {(top == CanarySeverity.Critical ? "needs attention now" : "CANary found something")}";
                await _ntfy.SendVehicleAlertAsync(vehicle, title, string.Join("\n", lines), top == CanarySeverity.Critical ? 5 : 3,
                    new List<string> { top == CanarySeverity.Critical ? "rotating_light" : "warning" }, "canary");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"CANary: alert for vehicle {vehicleId} failed: {ex.Message}");
            }
            finally
            {
                AlertLock.Release();
            }
        }

        public async Task HousekeepingAsync()
        {
            /* Data of a vehicle that was deleted goes with it. Only when the garage can be read and still
               holds other vehicles, so a database hiccup can never look like "everything was deleted". */
            var garage = new HashSet<int>();
            try
            {
                garage = _vehicles.GetVehicles().Select(x => x.Id).ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"CANary: the vehicle list could not be read: {ex.Message}");
            }
            foreach (int vehicleId in _store.Vehicles())
            {
                try
                {
                    if (garage.Count > 0 && !garage.Contains(vehicleId))
                    {
                        _store.DeleteVehicle(vehicleId);
                        _logger.LogInformation($"CANary: removed the data of deleted vehicle {vehicleId}");
                        continue;
                    }
                    _store.ApplyRetention(vehicleId);
                    int redone = _store.Reanalyze(vehicleId);
                    if (redone > 0)
                    {
                        _logger.LogInformation($"CANary: analysed {redone} drive(s) of vehicle {vehicleId} again with the current code");
                    }
                    await WeeklyDigestAsync(vehicleId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"CANary: housekeeping for vehicle {vehicleId} failed: {ex.Message}");
                }
            }
        }

        /// <summary>Once a week, on the morning after the week ends, a short summary of the finished week.</summary>
        private async Task WeeklyDigestAsync(int vehicleId)
        {
            var settings = _store.GetSettings(vehicleId);
            if (!settings.Notify || !settings.WeeklyDigest)
            {
                return;
            }
            var clock = _reports.ClockFor(vehicleId);
            var thisWeek = CanaryClock.WeekStart(clock.Now, CanaryReportBuilder.FirstDay(settings));
            if (clock.Now < thisWeek.AddHours(8))
            {
                return; /* not before 8 in the morning */
            }
            var finished = thisWeek.AddDays(-7);
            string key = finished.ToString("yyyy-MM-dd");
            var state = LoadAlerts(vehicleId);
            if (state.LastDigestWeek == key)
            {
                return;
            }
            state.LastDigestWeek = key;
            SaveAlerts(vehicleId, state);
            var report = _reports.Build(vehicleId, finished);
            var vehicle = _vehicles.GetVehicleById(vehicleId);
            if (report.Stats.Drives == 0 || vehicle == null || vehicle.Id == default)
            {
                return;
            }
            var format = new CanaryFormat(settings.OdometerUnit != "km");
            var lines = new List<string> { $"{report.Stats.Drives} {(report.Stats.Drives == 1 ? "drive" : "drives")}, {format.Distance(report.Stats.DistanceKm)}." };
            foreach (var finding in report.Findings.Take(4))
            {
                lines.Add($"{finding.Headline}: {format.Sentence(finding)}");
            }
            if (report.Codes.Count > 0)
            {
                lines.Add($"Trouble codes: {string.Join(", ", report.Codes.Select(x => x.Code.Code))}");
            }
            int priority = report.Worst == CanarySeverity.Critical ? 4 : report.Worst == CanarySeverity.Warning ? 3 : 2;
            string tag = report.Worst == CanarySeverity.Ok ? "white_check_mark" : report.Worst.IsFinding() ? "warning" : "bird";
            await _ntfy.SendVehicleAlertAsync(vehicle, $"{VehicleName(vehicle)}, last week: {report.Headline.ToLowerInvariant()}", string.Join("\n", lines), priority, new List<string> { tag }, "canary");
        }
    }

    /// <summary>Runs CANary's housekeeping once an hour.</summary>
    public class CanaryWorker : BackgroundService
    {
        private readonly ICanaryLogic _logic;
        private readonly ILogger<CanaryWorker> _logger;
        public CanaryWorker(ICanaryLogic logic, ILogger<CanaryWorker> logger)
        {
            _logic = logic;
            _logger = logger;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _logic.HousekeepingAsync();
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError($"CANary: background worker stopped: {ex.Message}");
            }
        }
    }
}
