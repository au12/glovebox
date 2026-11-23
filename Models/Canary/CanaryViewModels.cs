using CarCareTracker.Canary;

namespace CarCareTracker.Models
{
    /// <summary>Everything the CANary section of a vehicle page shows.</summary>
    public class CanaryViewModel
    {
        public Vehicle Vehicle { get; set; } = new();
        public CanaryDevice? Device { get; set; }
        public CanarySettings Settings { get; set; } = new();
        public CanaryWeek Week { get; set; } = new();
        public CanaryFormat Format { get; set; } = new(true);
        public CanaryCodeBook CodeBook { get; set; } = new(string.Empty);
        public CanaryClock Clock { get; set; } = new(0);
        /// <summary>Whether this server asks for a login, and so whether the device needs an API key.</summary>
        public bool AuthEnabled { get; set; }
        /// <summary>The address this page was opened with: what to type into the device.</summary>
        public string ServerUrl { get; set; } = string.Empty;
        public bool CanEdit { get; set; }
        public string OdometerUnit { get; set; } = "mi";
        public int TripCount { get; set; }
        public long RawBytes { get; set; }
        /// <summary>Drives whose time of day is unknown because the device had not yet learned the time.</summary>
        public int UndatedTrips { get; set; }
    }

    /// <summary>One drive opened from the raw-data section.</summary>
    public class CanaryTripViewModel
    {
        public int VehicleId { get; set; }
        public CanaryTrip Trip { get; set; } = new();
        public CanaryProfile? Profile { get; set; }
        public CanaryFormat Format { get; set; } = new(true);
        public CanaryCodeBook CodeBook { get; set; } = new(string.Empty);
        public DateTime When { get; set; }
        public bool HasRaw { get; set; }
        public bool CanEdit { get; set; }
        public bool TimeKnown => Trip.Start > 0;
    }
}
