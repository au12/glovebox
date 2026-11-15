namespace CarCareTracker.Models
{
    /// <summary>
    /// A reminder prepared for display: urgency, how far through its interval it is, and where the
    /// early-notice (heads-up) point sits.
    /// </summary>
    public class DueItem
    {
        public int Id { get; set; }
        public int VehicleId { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public ReminderUrgency Urgency { get; set; } = ReminderUrgency.NotUrgent;
        /// <summary>Metric the user picked (Date, Odometer or Both).</summary>
        public ReminderMetric UserMetric { get; set; } = ReminderMetric.Date;
        /// <summary>Metric that currently drives the urgency.</summary>
        public ReminderMetric Metric { get; set; } = ReminderMetric.Date;
        public DateTime Date { get; set; }
        public int Mileage { get; set; }
        public int DueDays { get; set; }
        public int DueMileage { get; set; }
        public bool IsRecurring { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        /// <summary>Share (0..1) of the interval already used. Null when the interval length is unknown.</summary>
        public double? Progress { get; set; }
        /// <summary>Position (0..1) of the heads-up point on the interval gauge.</summary>
        public double? HeadsUpMark { get; set; }
        /// <summary>Rough number of days until due, using the vehicle's recent daily distance for odometer reminders.</summary>
        public int? EstimatedDays { get; set; }
        public int EffectiveHeadsUpDistance { get; set; }
        public int EffectiveHeadsUpDays { get; set; }
        public bool InHeadsUpWindow { get; set; }
        public bool MuteNotifications { get; set; }
        public bool HasDate => UserMetric == ReminderMetric.Date || UserMetric == ReminderMetric.Both;
        public bool HasOdometer => UserMetric == ReminderMetric.Odometer || UserMetric == ReminderMetric.Both;
        /// <summary>Sort key: lower is sooner.</summary>
        public double SortKey { get; set; }
    }

    public class HistoryItem
    {
        public int Id { get; set; }
        public ImportMode RecordType { get; set; }
        public DateTime Date { get; set; }
        public int Mileage { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public decimal Cost { get; set; }
        public int AttachmentCount { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class VehicleOverviewViewModel
    {
        public Vehicle Vehicle { get; set; } = new Vehicle();
        public int CurrentOdometer { get; set; }
        public DateTime? LastOdometerDate { get; set; }
        public double AverageDailyDistance { get; set; }
        public string DistanceUnit { get; set; } = "mi";
        public List<DueItem> DueItems { get; set; } = new List<DueItem>();
        public List<HistoryItem> RecentHistory { get; set; } = new List<HistoryItem>();
        public List<Note> PinnedNotes { get; set; } = new List<Note>();
        public bool ShowCosts { get; set; } = true;
        public decimal TotalCost { get; set; }
        public decimal CostThisYear { get; set; }
        public decimal CostPerDistance { get; set; }
        public string AverageFuelEconomy { get; set; } = string.Empty;
        public string FuelEconomyUnit { get; set; } = string.Empty;
        public DateTime? LastFuelDate { get; set; }
        public int ServiceCount { get; set; }
        public int OpenPlanCount { get; set; }
        public int NoteCount { get; set; }
        public bool HasVehicleImageMap { get; set; }
        public bool CustomWidgetsConfigured { get; set; }
        public bool NotificationsEnabled { get; set; }
        public List<ImportMode> VisibleTabs { get; set; } = new List<ImportMode>();
        public VehicleCollaboratorViewModel Collaborators { get; set; } = new VehicleCollaboratorViewModel();
    }

    public class VehicleSwitcherItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Identifier { get; set; } = string.Empty;
        public string ImageLocation { get; set; } = string.Empty;
        public bool IsSold { get; set; }
    }

    /// <summary>
    /// Reminder notification stages, in increasing order of severity.
    /// </summary>
    public enum NotificationStage
    {
        None = 0,
        HeadsUp = 1,
        DueSoon = 2,
        Urgent = 3,
        Overdue = 4
    }

    public class NotificationState
    {
        public string Signature { get; set; } = string.Empty;
        public NotificationStage Stage { get; set; } = NotificationStage.None;
        public DateTime LastSentUtc { get; set; }
    }
}

namespace CarCareTracker.Models
{
    public class OdometerViewModel
    {
        public int Value { get; set; }
        public string Unit { get; set; } = "mi";
        /// <summary>sm, md or lg</summary>
        public string Size { get; set; } = "md";
        /// <summary>Animate the drums rolling into place.</summary>
        public bool Roll { get; set; }
        public int MinDigits { get; set; } = 6;
    }
}
