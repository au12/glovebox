using CarCareTracker.Models;
using System.Globalization;

namespace CarCareTracker.Helper
{
    /// <summary>
    /// Human-friendly wording for due dates, distances and statuses, shared by views and notifications.
    /// </summary>
    public static class GloveboxFormat
    {
        /// <summary>
        /// "1 record", "12 records".
        /// </summary>
        public static string Count(int count, string one, string many)
        {
            return $"{count.ToString("N0")} {(count == 1 ? one : many)}";
        }
        public static string Distance(int value, string unit)
        {
            return $"{value.ToString("N0", CultureInfo.CurrentCulture)} {unit}";
        }
        /// <summary>
        /// "in 12 days", "in 3 weeks", "tomorrow", "today".
        /// </summary>
        public static string InDays(int days)
        {
            if (days <= 0) return "today";
            if (days == 1) return "tomorrow";
            if (days < 14) return $"in {days} days";
            if (days < 60) return $"in {(int)Math.Round(days / 7d)} weeks";
            if (days < 730)
            {
                var months = (int)Math.Round(days / 30.4375);
                return months <= 1 ? "in 1 month" : $"in {months} months";
            }
            var years = Math.Round(days / 365.25, 1);
            return $"in {years.ToString("0.#", CultureInfo.CurrentCulture)} years";
        }
        /// <summary>
        /// "3 days", "2 weeks", "4 months" (for overdue phrasing).
        /// </summary>
        public static string Span(int days)
        {
            days = Math.Abs(days);
            if (days == 1) return "1 day";
            if (days < 14) return $"{days} days";
            if (days < 60) return $"{(int)Math.Round(days / 7d)} weeks";
            if (days < 730)
            {
                var months = (int)Math.Round(days / 30.4375);
                return months <= 1 ? "1 month" : $"{months} months";
            }
            var years = Math.Round(days / 365.25, 1);
            return $"{years.ToString("0.#", CultureInfo.CurrentCulture)} years";
        }
        public static string ShortDate(DateTime date)
        {
            return date.ToShortDateString();
        }
        /// <summary>
        /// Headline describing when a reminder is due, e.g. "Due in 480 mi", "Overdue by 3 days",
        /// "Due in 480 mi or 3 weeks".
        /// </summary>
        public static string DueHeadline(DueItem item, string unit)
        {
            if (item.Urgency == ReminderUrgency.PastDue)
            {
                if (item.Metric == ReminderMetric.Odometer || (!item.HasDate && item.HasOdometer))
                {
                    return item.DueMileage == 0 ? "Due now" : $"Overdue by {Distance(Math.Abs(item.DueMileage), unit)}";
                }
                return item.DueDays == 0 ? "Due today" : $"Overdue by {Span(item.DueDays)}";
            }
            var parts = new List<string>();
            if (item.HasOdometer)
            {
                parts.Add(item.DueMileage <= 0 ? "now" : Distance(item.DueMileage, unit));
            }
            if (item.HasDate)
            {
                var when = InDays(item.DueDays);
                parts.Add(when.StartsWith("in ") ? when.Substring(3) : when);
            }
            if (parts.Count == 0)
            {
                return "Due";
            }
            var joined = string.Join(" or ", parts);
            if (joined == "today" || joined == "tomorrow" || joined == "now")
            {
                return $"Due {joined}";
            }
            return $"Due in {joined}";
        }
        /// <summary>
        /// Target line, e.g. "at 49,000 mi or on 3/12/2027".
        /// </summary>
        public static string DueTarget(DueItem item, string unit)
        {
            var parts = new List<string>();
            if (item.HasOdometer)
            {
                parts.Add($"at {Distance(item.Mileage, unit)}");
            }
            if (item.HasDate)
            {
                parts.Add($"on {ShortDate(item.Date)}");
            }
            return string.Join(" or ", parts);
        }
        /// <summary>
        /// Compact due phrase for tight spaces: "in 2,353 mi", "today", "overdue by 3 days".
        /// </summary>
        public static string DueShort(DueItem item, string unit)
        {
            var headline = DueHeadline(item, unit);
            if (headline.StartsWith("Due in ")) return headline.Substring(4);
            if (headline.StartsWith("Due ")) return headline.Substring(4);
            return char.ToLowerInvariant(headline[0]) + headline.Substring(1);
        }
        public static string StatusLabel(DueItem item)
        {
            return item.Urgency switch
            {
                ReminderUrgency.PastDue => "Overdue",
                ReminderUrgency.VeryUrgent => "Urgent",
                ReminderUrgency.Urgent => "Due soon",
                _ => item.InHeadsUpWindow ? "Heads-up" : "Upcoming"
            };
        }
        public static string StatusClass(DueItem item)
        {
            return item.Urgency switch
            {
                ReminderUrgency.PastDue => "overdue",
                ReminderUrgency.VeryUrgent => "urgent",
                ReminderUrgency.Urgent => "soon",
                _ => item.InHeadsUpWindow ? "info" : "ok"
            };
        }
        public static string StatusLabel(ReminderUrgency urgency)
        {
            return urgency switch
            {
                ReminderUrgency.PastDue => "Overdue",
                ReminderUrgency.VeryUrgent => "Urgent",
                ReminderUrgency.Urgent => "Due soon",
                _ => "Upcoming"
            };
        }
        public static string StatusClass(ReminderUrgency urgency)
        {
            return urgency switch
            {
                ReminderUrgency.PastDue => "overdue",
                ReminderUrgency.VeryUrgent => "urgent",
                ReminderUrgency.Urgent => "soon",
                _ => "ok"
            };
        }
        public static string RecordIcon(ImportMode importMode)
        {
            return importMode switch
            {
                ImportMode.ServiceRecord => "bi-wrench-adjustable",
                ImportMode.RepairRecord => "bi-tools",
                ImportMode.UpgradeRecord => "bi-stars",
                ImportMode.GasRecord => "bi-fuel-pump",
                ImportMode.TaxRecord => "bi-receipt",
                ImportMode.NoteRecord => "bi-journal-text",
                ImportMode.OdometerRecord => "bi-speedometer",
                ImportMode.ReminderRecord => "bi-bell",
                ImportMode.PlanRecord => "bi-kanban",
                ImportMode.SupplyRecord => "bi-box-seam",
                ImportMode.InspectionRecord => "bi-clipboard-check",
                ImportMode.EquipmentRecord => "bi-disc",
                _ => "bi-file-earmark"
            };
        }
        public static string RecordTypeName(ImportMode importMode)
        {
            return importMode switch
            {
                ImportMode.ServiceRecord => "Service",
                ImportMode.RepairRecord => "Repair",
                ImportMode.UpgradeRecord => "Upgrade",
                ImportMode.GasRecord => "Fuel",
                ImportMode.TaxRecord => "Tax",
                ImportMode.NoteRecord => "Note",
                ImportMode.OdometerRecord => "Odometer",
                ImportMode.ReminderRecord => "Reminder",
                ImportMode.PlanRecord => "Plan",
                ImportMode.SupplyRecord => "Supply",
                ImportMode.InspectionRecord => "Inspection",
                ImportMode.EquipmentRecord => "Equipment",
                _ => "Record"
            };
        }
        /// <summary>
        /// Tab id used by the vehicle page for a record type.
        /// </summary>
        public static string TabId(ImportMode importMode)
        {
            return importMode switch
            {
                ImportMode.ServiceRecord => "servicerecord",
                ImportMode.RepairRecord => "accident",
                ImportMode.UpgradeRecord => "upgrade",
                ImportMode.GasRecord => "gas",
                ImportMode.TaxRecord => "tax",
                ImportMode.NoteRecord => "notes",
                ImportMode.OdometerRecord => "odometer",
                ImportMode.ReminderRecord => "reminder",
                ImportMode.PlanRecord => "plan",
                ImportMode.SupplyRecord => "supply",
                ImportMode.InspectionRecord => "inspection",
                ImportMode.EquipmentRecord => "equipment",
                _ => "report"
            };
        }
        /// <summary>
        /// Attachment kind used by the in-app viewer: image, pdf, video, audio, text, markdown, csv, link, record or other.
        /// </summary>
        public static string FileKind(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return "other";
            if (location.StartsWith("::")) return "record";
            if (StaticHelper.GetAttachmentIsLink(location) && !location.StartsWith("/images")) return "link";
            var ext = Path.GetExtension(location.Split('?')[0]).TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "avif" or "svg" => "image",
                "pdf" => "pdf",
                "mp4" or "webm" or "mov" or "m4v" or "ogv" => "video",
                "mp3" or "m4a" or "wav" or "ogg" or "oga" or "aac" or "flac" => "audio",
                "md" or "markdown" => "markdown",
                "csv" or "tsv" => "csv",
                "txt" or "log" or "json" or "xml" or "yml" or "yaml" or "ini" or "cfg" or "conf" or "gpx" or "kml" => "text",
                _ => "other"
            };
        }
        public static string FileIcon(string location)
        {
            var kind = FileKind(location);
            return kind switch
            {
                "image" => "bi-file-earmark-image",
                "pdf" => "bi-file-earmark-pdf",
                "video" => "bi-file-earmark-play",
                "audio" => "bi-file-earmark-music",
                "markdown" or "text" => "bi-file-earmark-text",
                "csv" => "bi-file-earmark-spreadsheet",
                "link" => "bi-link-45deg",
                "record" => "bi-box-arrow-in-up-right",
                _ => StaticHelper.GetIconByFileExtension(location)
            };
        }
        /// <summary>
        /// Plain-text excerpt of a markdown note for list previews.
        /// </summary>
        public static string MarkdownExcerpt(string? markdown, int maxLength = 240)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            var text = markdown;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s{0,3}(#{1,6}|>|[-*+]\s\[[ xX]\]|[-*+]|\d+[.)])\s+", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[*_`~|]{1,3}", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > maxLength ? text.Substring(0, maxLength).TrimEnd() + "…" : text;
        }
        public static string Initials(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
            }
            return name.Trim().Substring(0, Math.Min(2, name.Trim().Length)).ToUpperInvariant();
        }
        /// <summary>
        /// True when the vehicle has a real photo rather than the stock "No Image" placeholder.
        /// </summary>
        public static bool HasPhoto(string imageLocation)
        {
            return !string.IsNullOrWhiteSpace(imageLocation) && !imageLocation.EndsWith("/defaults/noimage.png", StringComparison.OrdinalIgnoreCase);
        }
        public static string VehicleName(Vehicle vehicle)
        {
            return string.Join(" ", new[] { vehicle.Year != default ? vehicle.Year.ToString() : string.Empty, vehicle.Make, vehicle.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }
}
