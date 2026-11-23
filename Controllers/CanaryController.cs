using CarCareTracker.Canary;
using CarCareTracker.External.Interfaces;
using CarCareTracker.Filter;
using CarCareTracker.Helper;
using CarCareTracker.Logic;
using CarCareTracker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace CarCareTracker.Controllers
{
    /// <summary>
    /// CANary: the device-facing API a dongle talks to (/api/canary/...), and the pages that turn what
    /// it uploads into a weekly health report with the raw data behind it.
    /// </summary>
    [Authorize]
    public class CanaryController : Controller
    {
        private readonly CanaryStore _store;
        private readonly CanaryReportBuilder _reports;
        private readonly CanaryCodeBook _codeBook;
        private readonly ICanaryLogic _logic;
        private readonly IVehicleDataAccess _vehicles;
        private readonly IUserLogic _userLogic;
        private readonly IConfigHelper _config;
        private readonly ILogger<CanaryController> _logger;

        public CanaryController(CanaryStore store, CanaryReportBuilder reports, CanaryCodeBook codeBook, ICanaryLogic logic,
            IVehicleDataAccess vehicles, IUserLogic userLogic, IConfigHelper config, ILogger<CanaryController> logger)
        {
            _store = store;
            _reports = reports;
            _codeBook = codeBook;
            _logic = logic;
            _vehicles = vehicles;
            _userLogic = userLogic;
            _config = config;
            _logger = logger;
        }

        private int GetUserID() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "-1");

        /// <summary>The device reads a few fixed field names, so replies are written out exactly rather than left to the app's JSON conventions.</summary>
        private ContentResult Reply(object value, int status = 200)
        {
            Response.StatusCode = status;
            return Content(JsonSerializer.Serialize(value), "application/json");
        }
        private ContentResult Refuse(string error, int status = 400) => Reply(new Dictionary<string, object> { ["ok"] = false, ["error"] = error }, status);

        private bool VehicleExists(int vehicleId)
        {
            var vehicle = _vehicles.GetVehicleById(vehicleId);
            return vehicle != null && vehicle.Id != default;
        }

        /* ───────────── device API ───────────── */

        /// <summary>Tells a device that this server understands CANary, as opposed to plain LubeLogger.</summary>
        [TypeFilter(typeof(APIKeyFilter), Arguments = new object[] { HouseholdPermission.View })]
        [HttpGet]
        [Route("/api/canary/hello")]
        public IActionResult Hello()
        {
            return Reply(new Dictionary<string, object> { ["canary_api"] = 1, ["server"] = "Glovebox", ["version"] = StaticHelper.GloveboxVersion });
        }

        /// <summary>
        /// A device checks in every time it joins Wi-Fi: who it is, the odometer, and how the last drive
        /// went. The odometer is recorded here; the reply says whether the vehicle profile is needed.
        /// </summary>
        [TypeFilter(typeof(APIKeyFilter), Arguments = new object[] { HouseholdPermission.Edit })]
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        [Route("/api/canary/checkin")]
        public async Task<IActionResult> Checkin(int vehicleId)
        {
            if (!VehicleExists(vehicleId))
            {
                return Refuse("no such vehicle", 404);
            }
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(Request.Body);
            }
            catch (JsonException)
            {
                return Refuse("check-in is not valid JSON");
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Refuse("check-in is not valid JSON");
                }
                var result = _logic.Checkin(vehicleId, doc.RootElement, User, GetUserID());
                return Reply(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["have_profile"] = result.HaveProfile,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["odometer"] = new Dictionary<string, object> { ["recorded"] = result.OdometerRecorded, ["value"] = result.OdometerValue, ["unit"] = result.OdometerUnit }
                });
            }
        }

        /// <summary>Stores a vehicle profile. Profiles are shared by every vehicle that uses the same one.</summary>
        [TypeFilter(typeof(APIKeyFilter), Arguments = new object[] { HouseholdPermission.Edit })]
        [HttpPut]
        [Route("/api/canary/profile")]
        public async Task<IActionResult> PutProfile(string hash)
        {
            if (!CanaryStore.ValidHash(hash))
            {
                return Refuse("bad profile hash");
            }
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer);
            if (buffer.Length == 0 || buffer.Length > 1024 * 1024)
            {
                return Refuse("profile is empty or too large");
            }
            if (!_store.SaveProfile(hash, buffer.ToArray(), out string error))
            {
                return Refuse(error);
            }
            /* drives that arrived before their profile can be analysed now */
            foreach (int vehicleId in _store.Vehicles())
            {
                _store.Reanalyze(vehicleId);
            }
            return Reply(new Dictionary<string, object> { ["ok"] = true });
        }

        /// <summary>How much of a drive's raw log the server already has, so an interrupted upload resumes.</summary>
        [TypeFilter(typeof(APIKeyFilter), Arguments = new object[] { HouseholdPermission.Edit })]
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpGet]
        [Route("/api/canary/trips/{id}/status")]
        public IActionResult TripStatus(int vehicleId, string id)
        {
            if (!CanaryStore.ValidTripId(id))
            {
                return Refuse("bad trip id");
            }
            if (!VehicleExists(vehicleId))
            {
                return Refuse("no such vehicle", 404);
            }
            var (received, complete) = _store.UploadStatus(vehicleId, id);
            return Reply(new Dictionary<string, object> { ["ok"] = true, ["received"] = received, ["complete"] = complete });
        }

        /// <summary>Receives one piece of a drive's raw log. The last piece starts the analysis.</summary>
        [TypeFilter(typeof(APIKeyFilter), Arguments = new object[] { HouseholdPermission.Edit })]
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        [Route("/api/canary/trips/{id}/chunk")]
        public async Task<IActionResult> TripChunk(int vehicleId, string id, long offset, long total, string? crc, long start = 0)
        {
            if (!CanaryStore.ValidTripId(id))
            {
                return Refuse("bad trip id");
            }
            if (!VehicleExists(vehicleId))
            {
                return Refuse("no such vehicle", 404);
            }
            if (Request.ContentLength > CanaryStore.MaxChunkBytes)
            {
                return Refuse("chunk too large", 413);
            }
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer);
            if (buffer.Length > CanaryStore.MaxChunkBytes)
            {
                return Refuse("chunk too large", 413);
            }
            uint.TryParse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint checksum);
            CanaryStore.ChunkResult result;
            try
            {
                result = _store.AppendChunk(vehicleId, id, offset, total, checksum, buffer.ToArray(), start);
            }
            catch (IOException ex)
            {
                _logger.LogError($"CANary: could not store a chunk of {id}: {ex.Message}");
                return Refuse("the server could not store the data", 500);
            }
            if (result.Error != null)
            {
                return Reply(new Dictionary<string, object> { ["ok"] = false, ["error"] = result.Error, ["received"] = result.Received }, 400);
            }
            if (result.Trip != null)
            {
                var trip = result.Trip;
                _ = Task.Run(() => _logic.TripReceivedAsync(vehicleId, trip));
            }
            return Reply(new Dictionary<string, object> { ["ok"] = true, ["received"] = result.Received, ["complete"] = result.Complete });
        }

        /* ───────────── pages ───────────── */

        private CanaryFormat FormatFor(int vehicleId)
        {
            var unit = _logic.OdometerUnit(vehicleId, User);
            return new CanaryFormat(unit != "km");
        }

        private bool CanEdit(int vehicleId) => User.IsInRole(nameof(UserData.IsRootUser)) || _userLogic.UserCanEditVehicle(GetUserID(), vehicleId, HouseholdPermission.Edit);

        /// <summary>The CANary section of a vehicle page: the weekly report and the raw data behind it.</summary>
        [TypeFilter(typeof(CollaboratorFilter))]
        [HttpGet]
        public IActionResult Report(int vehicleId, string? week = null)
        {
            DateTime? start = DateTime.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            var model = Shell(vehicleId);
            model.Week = _reports.Build(vehicleId, start);
            return PartialView("_Report", model);
        }

        private CanaryViewModel Shell(int vehicleId)
        {
            var trips = _store.TripsSnapshot(vehicleId);
            return new CanaryViewModel
            {
                Vehicle = _vehicles.GetVehicleById(vehicleId),
                Device = _store.GetDevice(vehicleId),
                Settings = _store.GetSettings(vehicleId),
                Format = FormatFor(vehicleId),
                CodeBook = _codeBook,
                Clock = _reports.ClockFor(vehicleId),
                AuthEnabled = _config.GetServerAuthEnabled(),
                ServerUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}",
                CanEdit = CanEdit(vehicleId),
                OdometerUnit = _logic.OdometerUnit(vehicleId, User),
                TripCount = trips.Count,
                RawBytes = _store.RawBytesOnDisk(vehicleId),
                UndatedTrips = trips.Count(x => x.Start <= 0)
            };
        }

        /// <summary>The settings dialog for a vehicle.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, false, HouseholdPermission.Edit })]
        [HttpGet]
        public IActionResult Settings(int vehicleId)
        {
            return PartialView("_Settings", Shell(vehicleId));
        }

        /// <summary>How many findings the latest week has: the small count beside the section's name.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true })]
        [HttpGet]
        public IActionResult Badge(int vehicleId)
        {
            if (!_store.HasDevice(vehicleId) && _store.TripsSnapshot(vehicleId).Count == 0)
            {
                return Reply(new Dictionary<string, object> { ["connected"] = false, ["count"] = 0, ["worst"] = "nodata" });
            }
            var week = _reports.Build(vehicleId);
            int count = week.Findings.Count() + (week.Codes.Count > 0 ? 1 : 0);
            return Reply(new Dictionary<string, object> { ["connected"] = true, ["count"] = count, ["worst"] = week.Worst.Name() });
        }

        /// <summary>One drive: its numbers, what the checks found, and the charts' frame.</summary>
        [TypeFilter(typeof(CollaboratorFilter))]
        [HttpGet]
        public IActionResult Trip(int vehicleId, string tripId)
        {
            var trip = _store.GetTrip(vehicleId, tripId);
            if (trip == null)
            {
                return NotFound();
            }
            var model = new CanaryTripViewModel
            {
                VehicleId = vehicleId,
                Trip = trip,
                Profile = _store.ProfileFor(vehicleId, trip.ProfileUsed.Length > 0 ? trip.ProfileUsed : trip.ProfileHash),
                Format = FormatFor(vehicleId),
                CodeBook = _codeBook,
                When = _reports.ClockFor(vehicleId).TripTime(trip),
                HasRaw = _store.HasRaw(vehicleId, tripId),
                CanEdit = CanEdit(vehicleId)
            };
            return PartialView("_Trip", model);
        }

        /// <summary>Chart data for a drive, read from its raw log and reduced to a few hundred points per signal.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true })]
        [HttpGet]
        public IActionResult Series(int vehicleId, string tripId, string signals, int points = 500)
        {
            var ids = (signals ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(12).ToList();
            using var raw = _store.OpenRaw(vehicleId, tripId);
            if (raw == null || ids.Count == 0)
            {
                return Reply(new Dictionary<string, object> { ["ok"] = false, ["error"] = "The raw data of this drive is no longer stored." }, 404);
            }
            var format = FormatFor(vehicleId);
            var (duration, series) = CanaryLog.ReadSeries(raw, ids, points);
            var profile = _store.ProfileFor(vehicleId, _store.GetTrip(vehicleId, tripId)?.ProfileHash);
            return Reply(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["duration"] = duration,
                ["series"] = series.Select(s =>
                {
                    int dec = profile?.Signals.FirstOrDefault(x => x.Id == s.Id)?.Dec ?? 1;
                    var unit = format.Convert(0, s.Unit, dec);
                    double Convert(double v) => Math.Round(format.Convert(v, s.Unit, dec).Value, 4);
                    return new Dictionary<string, object>
                    {
                        ["id"] = s.Id,
                        ["name"] = s.Name,
                        ["unit"] = unit.Unit,
                        ["dec"] = unit.Dec,
                        ["t"] = s.T,
                        ["v"] = s.V.Select(Convert),
                        ["lo"] = s.Lo.Select(Convert),
                        ["hi"] = s.Hi.Select(Convert)
                    };
                })
            });
        }

        /// <summary>The raw log of a drive exactly as the device recorded it.</summary>
        [TypeFilter(typeof(CollaboratorFilter))]
        [HttpGet]
        public IActionResult Raw(int vehicleId, string tripId)
        {
            var raw = _store.OpenRaw(vehicleId, tripId);
            if (raw == null)
            {
                return NotFound();
            }
            return File(raw, "text/plain", $"{tripId}.cnl");
        }

        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        public IActionResult SaveSettings(int vehicleId, bool recordOdometer, string odometerUnit, int odometerOffset, bool weekStartsSunday, int rawRetentionMonths, bool notify, bool weeklyDigest)
        {
            var settings = _store.GetSettings(vehicleId);
            settings.RecordOdometer = recordOdometer;
            settings.OdometerUnit = odometerUnit == "km" ? "km" : "mi";
            settings.OdometerOffset = odometerOffset;
            settings.WeekStartsSunday = weekStartsSunday;
            settings.RawRetentionMonths = Math.Clamp(rawRetentionMonths, 0, 120);
            settings.Notify = notify;
            settings.WeeklyDigest = weeklyDigest;
            _store.SaveSettings(vehicleId, settings);
            _store.ApplyRetention(vehicleId);
            return Json(OperationResponse.Succeed("CANary settings saved"));
        }

        /// <summary>Stops or resumes reporting one check for this vehicle: for a known quirk the owner has decided to live with.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        public IActionResult Mute(int vehicleId, string checkId, bool mute)
        {
            var settings = _store.GetSettings(vehicleId);
            settings.Muted.RemoveAll(x => x == checkId);
            if (mute && !string.IsNullOrWhiteSpace(checkId) && checkId.Length <= 64)
            {
                settings.Muted.Add(checkId);
            }
            _store.SaveSettings(vehicleId, settings);
            return Json(OperationResponse.Succeed(mute ? "This check is no longer reported" : "This check is reported again"));
        }

        /// <summary>Creates an API key for the device and shows it once.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        public IActionResult CreateKey(int vehicleId)
        {
            if (!_config.GetServerAuthEnabled())
            {
                return Json(OperationResponse.Failed("This Glovebox has no login, so the device needs no key."));
            }
            var existing = _userLogic.GetAPIKeysByUserId(GetUserID()).Select(x => x.Name).ToHashSet();
            string name = $"CANary vehicle {vehicleId}";
            for (int n = 2; existing.Contains(name); n++)
            {
                name = $"CANary vehicle {vehicleId} ({n})";
            }
            return Json(_userLogic.CreateAPIKey(GetUserID(), name, new List<HouseholdPermission> { HouseholdPermission.View, HouseholdPermission.Edit }));
        }

        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Delete })]
        [HttpPost]
        public IActionResult DeleteTrip(int vehicleId, string tripId)
        {
            return Json(_store.DeleteTrip(vehicleId, tripId) ? OperationResponse.Succeed("Drive deleted") : OperationResponse.Failed("That drive was not found"));
        }

        /// <summary>Runs the analysis again over every stored raw log, for instance after a profile was corrected.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Edit })]
        [HttpPost]
        public IActionResult Reanalyze(int vehicleId)
        {
            int redone = _store.Reanalyze(vehicleId, true);
            return Json(OperationResponse.Succeed($"Analysed {redone} {(redone == 1 ? "drive" : "drives")} again"));
        }

        /// <summary>Removes the device and everything it uploaded for this vehicle. Odometer records stay.</summary>
        [TypeFilter(typeof(CollaboratorFilter), Arguments = new object[] { false, true, HouseholdPermission.Delete })]
        [HttpPost]
        public IActionResult Forget(int vehicleId)
        {
            _store.DeleteVehicle(vehicleId);
            return Json(OperationResponse.Succeed("CANary data removed"));
        }
    }
}
