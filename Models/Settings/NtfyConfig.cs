namespace CarCareTracker.Models
{
    /// <summary>
    /// Per-user push notification settings for ntfy (https://ntfy.sh or self-hosted).
    /// Stored inside UserConfig so every user can point at their own topic.
    /// </summary>
    public class NtfyConfig
    {
        public bool Enabled { get; set; } = false;
        public string ServerUrl { get; set; } = "https://ntfy.sh";
        public string Topic { get; set; } = string.Empty;
        /// <summary>
        /// Optional access token (tk_...). Takes precedence over username/password.
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        /// <summary>
        /// Send a heads-up when a reminder enters its early-notice window.
        /// </summary>
        public bool NotifyHeadsUp { get; set; } = true;
        /// <summary>
        /// Send when a reminder becomes "Due soon" (ReminderUrgency.Urgent).
        /// </summary>
        public bool NotifyDueSoon { get; set; } = false;
        /// <summary>
        /// Send when a reminder becomes "Urgent" (ReminderUrgency.VeryUrgent).
        /// </summary>
        public bool NotifyUrgent { get; set; } = true;
        /// <summary>
        /// Send when a reminder becomes overdue (ReminderUrgency.PastDue).
        /// </summary>
        public bool NotifyOverdue { get; set; } = true;
        /// <summary>
        /// Default early-notice distance (in each vehicle's own unit) applied to reminders that use the default.
        /// </summary>
        public int HeadsUpDistance { get; set; } = 500;
        /// <summary>
        /// Default early-notice days applied to reminders that use the default.
        /// </summary>
        public int HeadsUpDays { get; set; } = 14;
        /// <summary>
        /// Repeat overdue notifications every N days. 0 = only once.
        /// </summary>
        public int OverdueRepeatDays { get; set; } = 0;
        public bool QuietHoursEnabled { get; set; } = false;
        public int QuietHoursStart { get; set; } = 21;
        public int QuietHoursEnd { get; set; } = 8;
        /// <summary>
        /// Public URL of this Glovebox instance, used as the click target of notifications.
        /// </summary>
        public string AppUrl { get; set; } = string.Empty;
        /// <summary>
        /// A copy that sends to a different server, leaving this instance (often a cached config) untouched.
        /// </summary>
        public NtfyConfig WithServerUrl(string serverUrl)
        {
            var copy = (NtfyConfig)MemberwiseClone();
            copy.ServerUrl = serverUrl;
            return copy;
        }
    }
}
