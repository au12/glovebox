using CarCareTracker.Models;

namespace CarCareTracker.Helper
{
    /// <summary>
    /// Shared calculations behind the Glovebox garage, overview and notifications:
    /// due items with interval progress, odometer trends and recent history.
    /// </summary>
    public interface IGloveboxHelper
    {
        (int Mileage, DateTime? Date) GetLatestOdometer(VehicleRecords records);
        double GetAverageDailyDistance(VehicleRecords records);
        List<DueItem> GetDueItems(List<ReminderRecord> reminders, int currentMileage, double averageDailyDistance, NtfyConfig notificationDefaults, DateTime now);
        List<HistoryItem> GetRecentHistory(VehicleRecords records, int take);
        string GetDistanceUnit(Vehicle vehicle, UserConfig userConfig);
        (int Distance, int Days) GetEffectiveHeadsUp(ReminderRecord reminder, NtfyConfig notificationDefaults);
        bool IsInHeadsUpWindow(ReminderRecord reminder, int currentMileage, DateTime now, NtfyConfig notificationDefaults);
    }
    public class GloveboxHelper : IGloveboxHelper
    {
        private readonly IReminderHelper _reminderHelper;
        private const double DaysPerMonth = 30.4375;
        public GloveboxHelper(IReminderHelper reminderHelper)
        {
            _reminderHelper = reminderHelper;
        }
        private static IEnumerable<(DateTime Date, int Mileage)> GetMileagePoints(VehicleRecords records)
        {
            return records.ServiceRecords.Select(x => (x.Date, x.Mileage))
                .Concat(records.CollisionRecords.Select(x => (x.Date, x.Mileage)))
                .Concat(records.UpgradeRecords.Select(x => (x.Date, x.Mileage)))
                .Concat(records.GasRecords.Select(x => (x.Date, x.Mileage)))
                .Concat(records.OdometerRecords.Select(x => (x.Date, x.Mileage)))
                .Where(x => x.Mileage > 0);
        }
        public (int Mileage, DateTime? Date) GetLatestOdometer(VehicleRecords records)
        {
            var points = GetMileagePoints(records).ToList();
            if (!points.Any())
            {
                return (0, null);
            }
            var maxMileage = points.Max(x => x.Mileage);
            var latestDate = points.Where(x => x.Mileage == maxMileage).Max(x => x.Date);
            return (maxMileage, latestDate);
        }
        public double GetAverageDailyDistance(VehicleRecords records)
        {
            var points = GetMileagePoints(records).OrderBy(x => x.Date).ThenBy(x => x.Mileage).ToList();
            if (points.Count < 2)
            {
                return 0;
            }
            //prefer the last 12 months so the estimate follows current driving habits.
            var recent = points.Where(x => x.Date >= DateTime.Now.AddDays(-365)).ToList();
            var sample = recent.Count >= 2 && (recent.Last().Date - recent.First().Date).TotalDays >= 30 ? recent : points;
            var first = sample.First();
            var last = sample.Last();
            var days = (last.Date - first.Date).TotalDays;
            var distance = sample.Max(x => x.Mileage) - first.Mileage;
            if (days < 14 || distance <= 0)
            {
                return 0;
            }
            return distance / days;
        }
        private static int? GetIntervalMileage(ReminderRecord reminder)
        {
            if (!reminder.IsRecurring)
            {
                return null;
            }
            var interval = reminder.ReminderMileageInterval != ReminderMileageInterval.Other ? (int)reminder.ReminderMileageInterval : reminder.CustomMileageInterval;
            return interval > 0 ? interval : null;
        }
        private static double? GetIntervalDays(ReminderRecord reminder)
        {
            if (!reminder.IsRecurring)
            {
                return null;
            }
            if (reminder.ReminderMonthInterval != ReminderMonthInterval.Other)
            {
                return (int)reminder.ReminderMonthInterval * DaysPerMonth;
            }
            if (reminder.CustomMonthInterval <= 0)
            {
                return null;
            }
            return reminder.CustomMonthIntervalUnit == ReminderIntervalUnit.Days ? reminder.CustomMonthInterval : reminder.CustomMonthInterval * DaysPerMonth;
        }
        public (int Distance, int Days) GetEffectiveHeadsUp(ReminderRecord reminder, NtfyConfig notificationDefaults)
        {
            switch (reminder.EarlyNotice)
            {
                case ReminderEarlyNotice.Off:
                    return (0, 0);
                case ReminderEarlyNotice.Custom:
                    return (Math.Max(0, reminder.EarlyNoticeDistance), Math.Max(0, reminder.EarlyNoticeDays));
                default:
                    var defaults = notificationDefaults ?? new NtfyConfig();
                    return (Math.Max(0, defaults.HeadsUpDistance), Math.Max(0, defaults.HeadsUpDays));
            }
        }
        public bool IsInHeadsUpWindow(ReminderRecord reminder, int currentMileage, DateTime now, NtfyConfig notificationDefaults)
        {
            var (distance, days) = GetEffectiveHeadsUp(reminder, notificationDefaults);
            bool hasOdometer = reminder.Metric == ReminderMetric.Odometer || reminder.Metric == ReminderMetric.Both;
            bool hasDate = reminder.Metric == ReminderMetric.Date || reminder.Metric == ReminderMetric.Both;
            if (hasOdometer && distance > 0 && reminder.Mileage - currentMileage <= distance)
            {
                return true;
            }
            if (hasDate && days > 0 && (reminder.Date.Date - now.Date).TotalDays <= days)
            {
                return true;
            }
            return false;
        }
        public List<DueItem> GetDueItems(List<ReminderRecord> reminders, int currentMileage, double averageDailyDistance, NtfyConfig notificationDefaults, DateTime now)
        {
            var viewModels = _reminderHelper.GetReminderRecordViewModels(reminders, currentMileage, now);
            var results = new List<DueItem>();
            foreach (var viewModel in viewModels)
            {
                var reminder = reminders.First(x => x.Id == viewModel.Id);
                var item = new DueItem
                {
                    Id = viewModel.Id,
                    VehicleId = viewModel.VehicleId,
                    Description = viewModel.Description,
                    Notes = viewModel.Notes,
                    Urgency = viewModel.Urgency,
                    UserMetric = viewModel.UserMetric,
                    Metric = viewModel.Metric,
                    Date = viewModel.Date,
                    Mileage = viewModel.Mileage,
                    DueDays = (reminder.Date.Date - now.Date).Days,
                    DueMileage = reminder.Mileage - currentMileage,
                    IsRecurring = viewModel.IsRecurring,
                    Tags = viewModel.Tags,
                    MuteNotifications = reminder.MuteNotifications
                };
                var (headsUpDistance, headsUpDays) = GetEffectiveHeadsUp(reminder, notificationDefaults);
                item.EffectiveHeadsUpDistance = headsUpDistance;
                item.EffectiveHeadsUpDays = headsUpDays;
                item.InHeadsUpWindow = item.Urgency == ReminderUrgency.NotUrgent && IsInHeadsUpWindow(reminder, currentMileage, now, notificationDefaults);
                //estimated days until due
                var estimates = new List<double>();
                if (item.HasDate)
                {
                    estimates.Add(item.DueDays);
                }
                if (item.HasOdometer && averageDailyDistance > 0)
                {
                    estimates.Add(item.DueMileage / averageDailyDistance);
                }
                if (estimates.Any())
                {
                    item.EstimatedDays = (int)Math.Round(estimates.Min());
                }
                //interval progress (only known for recurring reminders)
                double? mileageProgress = null;
                double? dateProgress = null;
                var intervalMileage = GetIntervalMileage(reminder);
                var intervalDays = GetIntervalDays(reminder);
                if (item.HasOdometer && intervalMileage.HasValue)
                {
                    mileageProgress = Math.Clamp(1 - (double)item.DueMileage / intervalMileage.Value, 0, 1);
                }
                if (item.HasDate && intervalDays.HasValue)
                {
                    dateProgress = Math.Clamp(1 - item.DueDays / intervalDays.Value, 0, 1);
                }
                if (mileageProgress.HasValue || dateProgress.HasValue)
                {
                    bool useMileage = mileageProgress.HasValue && (!dateProgress.HasValue || mileageProgress.Value >= dateProgress.Value);
                    item.Progress = useMileage ? mileageProgress : dateProgress;
                    double? mark = null;
                    if (useMileage && headsUpDistance > 0)
                    {
                        mark = 1 - (double)headsUpDistance / intervalMileage!.Value;
                    }
                    else if (!useMileage && headsUpDays > 0 && intervalDays.HasValue)
                    {
                        mark = 1 - headsUpDays / intervalDays.Value;
                    }
                    if (mark.HasValue && mark.Value > 0.02 && mark.Value < 0.98)
                    {
                        item.HeadsUpMark = mark;
                    }
                }
                if (item.Urgency == ReminderUrgency.PastDue)
                {
                    item.Progress = item.Progress.HasValue ? 1 : null;
                }
                int urgencyRank = item.Urgency switch
                {
                    ReminderUrgency.PastDue => 0,
                    ReminderUrgency.VeryUrgent => 1,
                    ReminderUrgency.Urgent => 2,
                    _ => item.InHeadsUpWindow ? 3 : 4
                };
                item.SortKey = urgencyRank * 1_000_000d + (item.EstimatedDays ?? 99_999);
                results.Add(item);
            }
            return results.OrderBy(x => x.SortKey).ThenBy(x => x.Description).ToList();
        }
        public List<HistoryItem> GetRecentHistory(VehicleRecords records, int take)
        {
            var items = records.ServiceRecords.Select(x => ToHistory(x, ImportMode.ServiceRecord))
                .Concat(records.CollisionRecords.Select(x => ToHistory(x, ImportMode.RepairRecord)))
                .Concat(records.UpgradeRecords.Select(x => ToHistory(x, ImportMode.UpgradeRecord)));
            return items.OrderByDescending(x => x.Date).ThenByDescending(x => x.Mileage).Take(take).ToList();
        }
        private static HistoryItem ToHistory(GenericRecord record, ImportMode importMode)
        {
            return new HistoryItem
            {
                Id = record.Id,
                RecordType = importMode,
                Date = record.Date,
                Mileage = record.Mileage,
                Description = record.Description,
                Notes = record.Notes,
                Cost = record.Cost,
                AttachmentCount = record.Files?.Count ?? 0,
                Tags = record.Tags ?? new List<string>()
            };
        }
        public string GetDistanceUnit(Vehicle vehicle, UserConfig userConfig)
        {
            if (vehicle.UseHours)
            {
                return "h";
            }
            return userConfig.UseMPG ? "mi" : "km";
        }
    }
}
