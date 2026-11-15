using CarCareTracker.Filter;
using CarCareTracker.Helper;
using CarCareTracker.Models;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace CarCareTracker.Controllers
{
    public partial class VehicleController
    {
        /// <summary>
        /// Glovebox vehicle overview: what's coming up, recent history and key numbers.
        /// </summary>
        [TypeFilter(typeof(CollaboratorFilter))]
        [HttpGet]
        public IActionResult GetVehicleOverview(int vehicleId)
        {
            //respects "Auto Refresh Lapsed Recurring Reminders" exactly like the other tabs do.
            GetAndUpdateVehicleUrgentOrPastDueReminders(vehicleId);
            var vehicle = _dataAccess.GetVehicleById(vehicleId);
            var userConfig = _config.GetUserConfig(User);
            var records = _vehicleLogic.GetVehicleRecords(vehicleId);
            var latestOdometer = _gloveboxHelper.GetLatestOdometer(records);
            var averageDaily = _gloveboxHelper.GetAverageDailyDistance(records);
            var reminders = _reminderRecordDataAccess.GetReminderRecordsByVehicleId(vehicleId);
            var viewModel = new VehicleOverviewViewModel
            {
                Vehicle = vehicle,
                CurrentOdometer = latestOdometer.Mileage,
                LastOdometerDate = latestOdometer.Date,
                AverageDailyDistance = averageDaily,
                DistanceUnit = _gloveboxHelper.GetDistanceUnit(vehicle, userConfig),
                DueItems = _gloveboxHelper.GetDueItems(reminders, latestOdometer.Mileage, averageDaily, userConfig.Ntfy, DateTime.Now),
                RecentHistory = _gloveboxHelper.GetRecentHistory(records, 6),
                ShowCosts = !userConfig.HideCostTracking,
                HasVehicleImageMap = !string.IsNullOrWhiteSpace(vehicle.MapLocation),
                CustomWidgetsConfigured = _fileHelper.WidgetsExist(),
                NotificationsEnabled = userConfig.Ntfy?.Enabled ?? false,
                VisibleTabs = userConfig.VisibleTabs,
                ServiceCount = records.ServiceRecords.Count + records.CollisionRecords.Count + records.UpgradeRecords.Count
            };
            var notes = _noteDataAccess.GetNotesByVehicleId(vehicleId);
            viewModel.NoteCount = notes.Count;
            viewModel.PinnedNotes = notes.Where(x => x.Pinned).OrderBy(x => x.Description).Take(4).ToList();
            if (userConfig.VisibleTabs.Contains(ImportMode.PlanRecord))
            {
                viewModel.OpenPlanCount = _planRecordDataAccess.GetPlanRecordsByVehicleId(vehicleId).Count(x => x.Progress != PlanProgress.Done);
            }
            //costs
            viewModel.TotalCost = _vehicleLogic.GetVehicleTotalCost(records);
            var thisYear = DateTime.Now.Year;
            viewModel.CostThisYear = records.ServiceRecords.Where(x => x.Date.Year == thisYear).Sum(x => x.Cost)
                + records.CollisionRecords.Where(x => x.Date.Year == thisYear).Sum(x => x.Cost)
                + records.UpgradeRecords.Where(x => x.Date.Year == thisYear).Sum(x => x.Cost)
                + records.GasRecords.Where(x => x.Date.Year == thisYear).Sum(x => x.Cost)
                + records.TaxRecords.Where(x => x.Date.Year == thisYear).Sum(x => x.Cost);
            var maxMileage = _vehicleLogic.GetMaxMileage(records);
            var minMileage = _vehicleLogic.GetMinMileage(records);
            var totalDistance = maxMileage - minMileage;
            viewModel.CostPerDistance = totalDistance > 0 ? viewModel.TotalCost / totalDistance : 0;
            //fuel economy
            if (records.GasRecords.Any())
            {
                var mileageData = _gasHelper.GetGasRecordViewModels(records.GasRecords, userConfig.UseMPG, userConfig.UseUKMPG, vehicle.IsElectric);
                var averageMPG = _gasHelper.GetAverageGasMileage(mileageData, userConfig.UseMPG);
                var unit = StaticHelper.GetFuelEconomyUnit(vehicle.IsElectric, vehicle.UseHours, userConfig.UseMPG, userConfig.UseUKMPG);
                if (unit == "l/100km" && userConfig.PreferredGasMileageUnit == "km/l" && decimal.TryParse(averageMPG, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal parsedAverage) && parsedAverage != 0)
                {
                    averageMPG = (100 / parsedAverage).ToString("F");
                    unit = "km/l";
                }
                //one decimal is plenty for an at-a-glance figure
                if (decimal.TryParse(averageMPG, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal roundedAverage))
                {
                    averageMPG = roundedAverage == 0 ? string.Empty : roundedAverage.ToString("N1");
                }
                viewModel.AverageFuelEconomy = averageMPG;
                viewModel.FuelEconomyUnit = unit;
                viewModel.LastFuelDate = records.GasRecords.Max(x => x.Date);
            }
            //collaborators
            viewModel.Collaborators = new VehicleCollaboratorViewModel
            {
                Collaborators = _userLogic.GetCollaboratorsForVehicle(vehicleId),
                CanModifyCollaborators = _userLogic.UserCanDirectlyEditVehicle(GetUserID(), vehicleId)
            };
            return PartialView("Overview/_Overview", viewModel);
        }
        /// <summary>
        /// Counts behind the reminder badge in the navigation.
        /// </summary>
        [TypeFilter(typeof(CollaboratorFilter))]
        [HttpGet]
        public IActionResult GetVehicleReminderSummary(int vehicleId)
        {
            GetAndUpdateVehicleUrgentOrPastDueReminders(vehicleId);
            var reminders = GetRemindersAndUrgency(vehicleId, DateTime.Now);
            return Json(new
            {
                overdue = reminders.Count(x => x.Urgency == ReminderUrgency.PastDue),
                urgent = reminders.Count(x => x.Urgency == ReminderUrgency.VeryUrgent),
                dueSoon = reminders.Count(x => x.Urgency == ReminderUrgency.Urgent),
                total = reminders.Count
            });
        }
        /// <summary>
        /// Other vehicles the user can jump to from the app bar.
        /// </summary>
        [HttpGet]
        public IActionResult GetVehicleSwitcher(int vehicleId)
        {
            var vehicles = _dataAccess.GetVehicles();
            if (!User.IsInRole(nameof(UserData.IsRootUser)))
            {
                vehicles = _userLogic.FilterUserVehicles(vehicles, GetUserID());
            }
            var userConfig = _config.GetUserConfig(User);
            if (userConfig.HideSoldVehicles)
            {
                vehicles.RemoveAll(x => !string.IsNullOrWhiteSpace(x.SoldDate) && x.Id != vehicleId);
            }
            var result = vehicles
                .OrderBy(x => string.IsNullOrWhiteSpace(x.SoldDate) ? 0 : 1)
                .ThenByDescending(x => x.Year)
                .Select(x => new VehicleSwitcherItem
                {
                    Id = x.Id,
                    Name = GloveboxFormat.VehicleName(x),
                    Identifier = StaticHelper.GetVehicleIdentifier(x),
                    ImageLocation = x.ImageLocation,
                    IsSold = !string.IsNullOrWhiteSpace(x.SoldDate)
                }).ToList();
            ViewData["CurrentVehicleId"] = vehicleId;
            return PartialView("Overview/_VehicleSwitcher", result);
        }
    }
}
