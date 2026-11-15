namespace CarCareTracker.Models
{
    public class ReminderRecordInputViewModel: ReminderRecordInput
    {
        public bool UseHours { get; set; }
        public int CurrentOdometer { get; set; }
    }
}
