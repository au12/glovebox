namespace CarCareTracker.Models
{
    /// <summary>
    /// How a reminder's early (heads-up) notification window is determined.
    /// </summary>
    public enum ReminderEarlyNotice
    {
        /// <summary>Use the defaults from Settings &gt; Notifications.</summary>
        Default = 0,
        /// <summary>Use the distance/days stored on the reminder itself.</summary>
        Custom = 1,
        /// <summary>Never send a heads-up for this reminder.</summary>
        Off = 2
    }
}
