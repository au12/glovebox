namespace CarCareTracker.Canary
{
    /// <summary>
    /// Turns device timestamps into the vehicle's local time. The server's own time zone is used when it
    /// has one; a server left on UTC (a container without TZ set) falls back to the offset the device reports.
    /// </summary>
    public sealed class CanaryClock
    {
        private readonly TimeZoneInfo? _zone;
        private readonly TimeSpan _fixed;

        public CanaryClock(int deviceOffsetMin)
        {
            var local = TimeZoneInfo.Local;
            if (local.BaseUtcOffset != TimeSpan.Zero || local.SupportsDaylightSavingTime || deviceOffsetMin == 0)
            {
                _zone = local;
            }
            else
            {
                _fixed = TimeSpan.FromMinutes(deviceOffsetMin);
            }
        }

        public DateTime Local(long unix)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            return _zone != null ? TimeZoneInfo.ConvertTimeFromUtc(utc, _zone) : DateTime.SpecifyKind(utc + _fixed, DateTimeKind.Unspecified);
        }

        public DateTime Now => Local(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        /// <summary>When a drive happened. A device that never learned the time leaves only the moment its log arrived.</summary>
        public DateTime TripTime(CanaryTrip trip) => Local(trip.Start > 0 ? trip.Start : trip.ReceivedAt);

        public static DateTime WeekStart(DateTime day, DayOfWeek first)
        {
            int back = ((int)day.DayOfWeek - (int)first + 7) % 7;
            return day.Date.AddDays(-back);
        }
    }

    public sealed class CanaryDay
    {
        public DateTime Date { get; set; }
        public List<CanaryTrip> Trips { get; set; } = new();
        public double DistanceKm => Trips.Sum(x => x.DistanceKm);
        public double DurationS => Trips.Sum(x => x.DurationS);
        public CanarySeverity Worst => Trips.Select(x => x.Worst).Where(x => x.IsFinding()).DefaultIfEmpty(Trips.Any(x => x.Worst == CanarySeverity.Ok) ? CanarySeverity.Ok : CanarySeverity.NoData).Max();
    }

    public sealed class CanaryTrendPoint
    {
        public DateTime Week { get; set; }
        public double? Value { get; set; }
        public CanarySeverity Status { get; set; } = CanarySeverity.NoData;
        public int Drives { get; set; }
    }

    /// <summary>One health check over one week.</summary>
    public sealed class CanaryWeekCheck
    {
        public CanaryCheck Check { get; set; } = new();
        public CanarySeverity Status { get; set; } = CanarySeverity.NoData;
        public double? Value { get; set; }
        public bool Low { get; set; }
        /// <summary>Seconds of the right driving conditions gathered over the week.</summary>
        public double SeenS { get; set; }
        /// <summary>Drives that contributed data.</summary>
        public int DrivesSeen { get; set; }
        /// <summary>Drives long enough to reach a verdict on their own.</summary>
        public int Drives { get; set; }
        /// <summary>Of those, the drives where the check tripped.</summary>
        public int DrivesFlagged { get; set; }
        public string? Missing { get; set; }
        public bool Muted { get; set; }
        /// <summary>new, worse, better, cleared, same or empty when there is nothing to compare with.</summary>
        public string Change { get; set; } = string.Empty;
        public CanarySeverity? PreviousStatus { get; set; }
        public double? PreviousValue { get; set; }
        /// <summary>Oldest first; the last point is this week.</summary>
        public List<CanaryTrendPoint> Trend { get; set; } = new();

        public string Headline => Check.Headline(Status, Low);
        public string? Template => Check.Template(Status, Low);
    }

    public sealed class CanaryWeekCode
    {
        public CanaryCode Code { get; set; } = new();
        public int Drives { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        /// <summary>Not seen in the four weeks before.</summary>
        public bool New { get; set; }
        public CanarySeverity Severity => Code.Kind == "pending" ? CanarySeverity.Advisory : CanarySeverity.Warning;
    }

    public sealed class CanaryWeekStats
    {
        public int Drives { get; set; }
        public int DaysDriven { get; set; }
        public double DistanceKm { get; set; }
        public double DurationS { get; set; }
        public double IdleS { get; set; }
        public double FuelL { get; set; }
        public double MaxSpeed { get; set; }
        public double? OdometerKm { get; set; }
    }

    public sealed class CanaryWeek
    {
        public DateTime Start { get; set; }
        public DateTime End => Start.AddDays(6);
        /// <summary>The week still in progress.</summary>
        public bool Current { get; set; }
        public DateTime? Previous { get; set; }
        public DateTime? Next { get; set; }
        public List<CanaryDay> Days { get; set; } = new();
        public CanaryWeekStats Stats { get; set; } = new();
        public CanaryWeekStats? PreviousStats { get; set; }
        public CanaryProfile? Profile { get; set; }
        public List<CanaryWeekCheck> Checks { get; set; } = new();
        public List<CanaryWeekCode> Codes { get; set; } = new();
        public CanarySeverity Worst { get; set; } = CanarySeverity.NoData;
        public string Headline { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;

        public IEnumerable<CanaryWeekCheck> Findings => Checks.Where(x => x.Status.IsFinding() && !x.Muted).OrderByDescending(x => x.Status);
        public IEnumerable<CanaryWeekCheck> Passed => Checks.Where(x => x.Status == CanarySeverity.Ok);
        public IEnumerable<CanaryWeekCheck> Waiting => Checks.Where(x => x.Status == CanarySeverity.NoData);
        public IEnumerable<CanaryWeekCheck> Unavailable => Checks.Where(x => x.Status == CanarySeverity.Unavailable);
        public IEnumerable<CanaryWeekCheck> MutedFindings => Checks.Where(x => x.Status.IsFinding() && x.Muted);
        /// <summary>What is different from the last week that had a verdict.</summary>
        public IEnumerable<CanaryWeekCheck> Changed => Checks.Where(x => !x.Muted && x.Change is "new" or "worse" or "better" or "cleared");
    }

    /// <summary>Builds the weekly report for a vehicle from the drives the server has analysed.</summary>
    public sealed class CanaryReportBuilder
    {
        public const int TrendWeeks = 8;
        private readonly CanaryStore _store;

        public CanaryReportBuilder(CanaryStore store)
        {
            _store = store;
        }

        public CanaryClock ClockFor(int vehicleId) => new(_store.GetDevice(vehicleId)?.TzOffsetMin ?? 0);

        public static DayOfWeek FirstDay(CanarySettings settings) => settings.WeekStartsSunday ? DayOfWeek.Sunday : DayOfWeek.Monday;

        /// <summary>The week of the most recent drive, or this week when there is none.</summary>
        public DateTime LatestWeek(int vehicleId)
        {
            var clock = ClockFor(vehicleId);
            var first = FirstDay(_store.GetSettings(vehicleId));
            var trips = _store.TripsSnapshot(vehicleId);
            var day = trips.Count > 0 ? trips.Max(clock.TripTime) : clock.Now;
            return CanaryClock.WeekStart(day, first);
        }

        public CanaryWeek Build(int vehicleId, DateTime? week = null)
        {
            var settings = _store.GetSettings(vehicleId);
            var clock = ClockFor(vehicleId);
            var first = FirstDay(settings);
            var start = CanaryClock.WeekStart(week ?? LatestWeek(vehicleId), first);
            var all = _store.TripsSnapshot(vehicleId).Select(t => (Trip: t, When: clock.TripTime(t))).OrderBy(x => x.When).ToList();
            List<CanaryTrip> In(DateTime from, int days) => all.Where(x => x.When >= from && x.When < from.AddDays(days)).Select(x => x.Trip).ToList();

            var trips = In(start, 7);
            var report = new CanaryWeek { Start = start, Current = CanaryClock.WeekStart(clock.Now, first) == start };
            if (all.Count > 0)
            {
                var earliest = CanaryClock.WeekStart(all[0].When, first);
                var latest = CanaryClock.WeekStart(all[^1].When, first);
                if (start > earliest)
                {
                    /* step back to the nearest earlier week that has drives */
                    report.Previous = all.Where(x => x.When < start).Select(x => CanaryClock.WeekStart(x.When, first)).Max();
                }
                if (start < latest)
                {
                    report.Next = all.Where(x => x.When >= start.AddDays(7)).Select(x => CanaryClock.WeekStart(x.When, first)).Min();
                }
            }

            for (int d = 0; d < 7; d++)
            {
                var date = start.AddDays(d);
                report.Days.Add(new CanaryDay { Date = date, Trips = all.Where(x => x.When.Date == date).Select(x => x.Trip).ToList() });
            }
            report.Stats = Stats(trips, report.Days);
            var before = In(start.AddDays(-7), 7);
            if (before.Count > 0)
            {
                report.PreviousStats = Stats(before, null);
            }

            /* The newest profile seen this week defines the checks; older weeks are judged by the same
               definitions so that a trend compares like with like. */
            var newest = trips.LastOrDefault();
            report.Profile = _store.ProfileFor(vehicleId, newest != null && newest.ProfileUsed.Length > 0 ? newest.ProfileUsed : newest?.ProfileHash);
            if (report.Profile != null)
            {
                var history = new List<(DateTime Week, List<CanaryTrip> Trips)>();
                for (int w = TrendWeeks; w >= 1; w--)
                {
                    var from = start.AddDays(-7 * w);
                    history.Add((from, In(from, 7)));
                }
                foreach (var check in report.Profile.Checks)
                {
                    var now = Evaluate(check, trips);
                    now.Muted = settings.Muted.Contains(check.Id);
                    foreach (var (weekStart, weekTrips) in history)
                    {
                        var then = weekTrips.Count > 0 ? Evaluate(check, weekTrips) : null;
                        now.Trend.Add(new CanaryTrendPoint { Week = weekStart, Value = then?.Value, Status = then?.Status ?? CanarySeverity.NoData, Drives = weekTrips.Count });
                    }
                    now.Trend.Add(new CanaryTrendPoint { Week = start, Value = now.Value, Status = now.Status, Drives = trips.Count });
                    var last = now.Trend.Take(now.Trend.Count - 1).LastOrDefault(x => x.Status == CanarySeverity.Ok || x.Status.IsFinding());
                    if (last != null)
                    {
                        now.PreviousStatus = last.Status;
                        now.PreviousValue = last.Value;
                    }
                    now.Change = Change(now.Status, last?.Status);
                    report.Checks.Add(now);
                }
            }

            /* Trouble codes seen during the week's drives. */
            var earlier = In(start.AddDays(-28), 28);
            var known = earlier.SelectMany(t => t.Codes).Select(Key).ToHashSet();
            foreach (var group in trips.SelectMany(t => t.Codes.Select(c => (Trip: t, Code: c))).GroupBy(x => Key(x.Code)))
            {
                var latest = group.Last();
                report.Codes.Add(new CanaryWeekCode
                {
                    Code = latest.Code,
                    Drives = group.Select(x => x.Trip.Id).Distinct().Count(),
                    First = clock.TripTime(group.First().Trip),
                    Last = clock.TripTime(latest.Trip),
                    New = earlier.Count > 0 && !known.Contains(group.Key)
                });
            }
            report.Codes = report.Codes.OrderByDescending(x => x.Severity).ThenBy(x => x.Code.Code).ToList();

            Verdict(report);
            return report;
        }

        private static string Key(CanaryCode c) => $"{c.Code}|{c.Module ?? c.Ecu}";

        private static CanaryWeekStats Stats(List<CanaryTrip> trips, List<CanaryDay>? days)
        {
            return new CanaryWeekStats
            {
                Drives = trips.Count,
                DaysDriven = days?.Count(x => x.Trips.Count > 0) ?? 0,
                DistanceKm = trips.Sum(x => x.DistanceKm),
                DurationS = trips.Sum(x => x.DurationS),
                IdleS = trips.Sum(x => x.IdleS),
                FuelL = trips.Sum(x => x.FuelL),
                MaxSpeed = trips.Count > 0 ? trips.Max(x => x.MaxSpeed) : 0,
                OdometerKm = trips.Where(x => x.OdoEndKm != null).Select(x => x.OdoEndKm).LastOrDefault()
            };
        }

        /// <summary>
        /// One check over a set of drives. Most statistics pool every drive as if it were one long drive,
        /// which lets a week reach verdicts no single short drive could. Statistics that only mean
        /// something within a drive ("time to warm up") report the worst drive instead.
        /// </summary>
        public static CanaryWeekCheck Evaluate(CanaryCheck check, List<CanaryTrip> trips)
        {
            var result = new CanaryWeekCheck { Check = check };
            var mine = trips.Select(t => t.Checks.FirstOrDefault(x => x.Id == check.Id)).Where(x => x != null).Select(x => x!).ToList();
            if (mine.Count == 0)
            {
                return result;
            }
            if (mine.All(x => x.Status == CanarySeverity.Unavailable))
            {
                result.Status = CanarySeverity.Unavailable;
                result.Missing = mine[^1].Missing;
                return result;
            }
            var withData = mine.Where(x => x.State != null && x.State.N > 0).ToList();
            result.DrivesSeen = withData.Count;
            result.Drives = mine.Count(x => x.Status == CanarySeverity.Ok || x.Status.IsFinding());
            result.DrivesFlagged = mine.Count(x => x.Status.IsFinding());
            result.SeenS = withData.Sum(x => x.State!.N) * CanaryCheckEngine.TickS;
            if (check.PerTrip)
            {
                var verdicts = mine.Where(x => (x.Status == CanarySeverity.Ok || x.Status.IsFinding()) && x.Value != null).ToList();
                if (verdicts.Count > 0)
                {
                    /* judge by today's thresholds, then take the worst drive */
                    var worst = verdicts.Select(x =>
                    {
                        CanaryCheckEngine.Classify(check, x.Value!.Value, out var status, out bool low);
                        return (x.Value, Status: status, Low: low);
                    }).OrderByDescending(x => x.Status).ThenByDescending(x => Math.Abs(x.Value!.Value)).First();
                    result.Status = worst.Status;
                    result.Value = worst.Value;
                    result.Low = worst.Low;
                }
                return result;
            }
            if (withData.Count == 0)
            {
                return result;
            }
            var pooled = CanaryCheckEngine.Evaluate(check, CanaryCheckEngine.Merge(check.Id, withData.Select(x => x.State!)), 0);
            result.Status = pooled.Status;
            result.Value = pooled.Value;
            result.Low = pooled.Low;
            return result;
        }

        private static string Change(CanarySeverity now, CanarySeverity? before)
        {
            bool nowVerdict = now == CanarySeverity.Ok || now.IsFinding();
            if (!nowVerdict || before == null)
            {
                return string.Empty;
            }
            if (now.IsFinding() && before == CanarySeverity.Ok) return "new";
            if (now == CanarySeverity.Ok && before.Value.IsFinding()) return "cleared";
            if (now > before) return "worse";
            if (now < before) return "better";
            return "same";
        }

        private static readonly string[] Count = { "No", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
        public static string CountWord(int n) => n >= 0 && n < Count.Length ? Count[n] : n.ToString();

        private static void Verdict(CanaryWeek report)
        {
            var findings = report.Findings.ToList();
            int things = findings.Count + (report.Codes.Count > 0 ? 1 : 0);
            int passed = report.Passed.Count();
            var worst = findings.Select(x => x.Status).Concat(report.Codes.Select(x => x.Severity)).DefaultIfEmpty(CanarySeverity.Ok).Max();
            int drives = report.Stats.Drives;
            string span = report.Current ? "so far this week" : "this week";
            if (drives == 0)
            {
                report.Worst = CanarySeverity.NoData;
                report.Headline = "No drives this week";
                report.Summary = report.Current ? "The report fills in as CANary uploads drives." : "CANary uploaded nothing for these days.";
                return;
            }
            if (things > 0)
            {
                report.Worst = worst;
                if (worst == CanarySeverity.Advisory)
                {
                    report.Headline = $"{CountWord(things)} {(things == 1 ? "thing" : "things")} to keep an eye on";
                    report.Summary = "Nothing needs doing yet. The trend lines below show whether it is getting worse.";
                }
                else
                {
                    report.Headline = $"{CountWord(things)} {(things == 1 ? "thing needs" : "things need")} attention";
                    var top = findings.FirstOrDefault();
                    report.Summary = worst == CanarySeverity.Critical && top != null
                        ? $"Most serious: {top.Headline}. Have it looked at before driving much further."
                        : "Nothing looks urgent, but it is worth having checked soon.";
                }
                return;
            }
            if (passed > 0)
            {
                report.Worst = CanarySeverity.Ok;
                report.Headline = "All clear";
                int waiting = report.Waiting.Count();
                report.Summary = $"{(passed == 1 ? "The one check" : $"All {passed} checks")} that had enough data {span} passed."
                    + (waiting > 0 ? $" {CountWord(waiting)} more {(waiting == 1 ? "needs" : "need")} more driving." : string.Empty);
                return;
            }
            report.Worst = CanarySeverity.NoData;
            report.Headline = "Not enough driving to judge";
            report.Summary = "The drives were too short for the checks to finish. Longer drives with a warm engine fill this in.";
        }
    }
}
