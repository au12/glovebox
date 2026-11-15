using System.Security.Claims;
using CarCareTracker.External.Interfaces;
using CarCareTracker.Helper;
using CarCareTracker.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarCareTracker.Logic
{
    public interface INtfyNotificationLogic
    {
        /// <summary>Ask the background worker to evaluate reminders soon (debounced).</summary>
        void RequestCheck();
        /// <summary>Waits until a check was requested or the timeout elapses.</summary>
        Task WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken);
        Task<int> RunCheckAsync(CancellationToken cancellationToken = default);
        Task<OperationResponse> SendTestAsync(NtfyConfig config, string vehicleName);
    }
    /// <summary>
    /// Sends reminder notifications to ntfy. Each reminder walks through stages
    /// (heads-up, due soon, urgent, overdue); a stage is only sent once per due cycle,
    /// and the cycle resets automatically when a recurring reminder rolls forward.
    /// </summary>
    public class NtfyNotificationLogic : INtfyNotificationLogic
    {
        private const string StatePath = "data/notifications.json";
        private readonly IConfigHelper _config;
        private readonly IVehicleDataAccess _vehicleDataAccess;
        private readonly IReminderRecordDataAccess _reminderRecordDataAccess;
        private readonly IUserRecordDataAccess _userRecordDataAccess;
        private readonly IUserConfigDataAccess _userConfigDataAccess;
        private readonly IUserLogic _userLogic;
        private readonly IVehicleLogic _vehicleLogic;
        private readonly IGloveboxHelper _gloveboxHelper;
        private readonly IReminderHelper _reminderHelper;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NtfyNotificationLogic> _logger;
        private readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, 1);
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public NtfyNotificationLogic(IConfigHelper config,
            IVehicleDataAccess vehicleDataAccess,
            IReminderRecordDataAccess reminderRecordDataAccess,
            IUserRecordDataAccess userRecordDataAccess,
            IUserConfigDataAccess userConfigDataAccess,
            IUserLogic userLogic,
            IVehicleLogic vehicleLogic,
            IGloveboxHelper gloveboxHelper,
            IReminderHelper reminderHelper,
            IHttpClientFactory httpClientFactory,
            ILogger<NtfyNotificationLogic> logger)
        {
            _config = config;
            _vehicleDataAccess = vehicleDataAccess;
            _reminderRecordDataAccess = reminderRecordDataAccess;
            _userRecordDataAccess = userRecordDataAccess;
            _userConfigDataAccess = userConfigDataAccess;
            _userLogic = userLogic;
            _vehicleLogic = vehicleLogic;
            _gloveboxHelper = gloveboxHelper;
            _reminderHelper = reminderHelper;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public void RequestCheck()
        {
            try
            {
                if (_signal.CurrentCount == 0)
                {
                    _signal.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                //already signalled
            }
        }
        public async Task WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                await _signal.WaitAsync(timeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static readonly ClaimsPrincipal RootPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.Name, "admin"),
            new(ClaimTypes.NameIdentifier, (-1).ToString()),
            new(ClaimTypes.Role, nameof(UserData.IsRootUser))
        }, "GloveboxNotifications"));
        private class Recipient
        {
            public string Key { get; set; } = string.Empty;
            public int UserId { get; set; }
            public NtfyConfig Config { get; set; } = new NtfyConfig();
            public bool UseMPG { get; set; } = true;
        }
        private List<Recipient> GetRecipients()
        {
            var recipients = new List<Recipient>();
            //read through the same cache the settings page writes to, so a just-saved change is picked up immediately
            var rootConfig = _config.GetUserConfig(RootPrincipal);
            if (rootConfig?.Ntfy != null)
            {
                recipients.Add(new Recipient { Key = "root", UserId = -1, Config = rootConfig.Ntfy, UseMPG = rootConfig.UseMPG });
            }
            if (_config.GetServerAuthEnabled())
            {
                foreach (var user in _userRecordDataAccess.GetUsers())
                {
                    try
                    {
                        var userConfig = _userConfigDataAccess.GetUserConfig(user.Id);
                        if (userConfig?.UserConfig?.Ntfy != null)
                        {
                            recipients.Add(new Recipient { Key = $"u{user.Id}", UserId = user.Id, Config = userConfig.UserConfig.Ntfy, UseMPG = userConfig.UserConfig.UseMPG });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Unable to read notification settings for user {user.Id}: {ex.Message}");
                    }
                }
            }
            return recipients.Where(x => x.Config.Enabled && !string.IsNullOrWhiteSpace(x.Config.Topic)).ToList();
        }

        private static bool InQuietHours(NtfyConfig config, DateTime now)
        {
            if (!config.QuietHoursEnabled || config.QuietHoursStart == config.QuietHoursEnd)
            {
                return false;
            }
            var hour = now.Hour;
            return config.QuietHoursStart < config.QuietHoursEnd
                ? hour >= config.QuietHoursStart && hour < config.QuietHoursEnd
                : hour >= config.QuietHoursStart || hour < config.QuietHoursEnd;
        }

        private NotificationStage GetStage(ReminderRecord reminder, ReminderRecordViewModel viewModel, NtfyConfig config, int currentMileage, DateTime now)
        {
            if (viewModel.Urgency == ReminderUrgency.PastDue && config.NotifyOverdue)
            {
                return NotificationStage.Overdue;
            }
            if (viewModel.Urgency >= ReminderUrgency.VeryUrgent && config.NotifyUrgent)
            {
                return NotificationStage.Urgent;
            }
            if (viewModel.Urgency >= ReminderUrgency.Urgent && config.NotifyDueSoon)
            {
                return NotificationStage.DueSoon;
            }
            if (config.NotifyHeadsUp && _gloveboxHelper.IsInHeadsUpWindow(reminder, currentMileage, now, config))
            {
                return NotificationStage.HeadsUp;
            }
            return NotificationStage.None;
        }

        /// <summary>
        /// Identifies one due cycle of a reminder: only the targets the reminder actually uses count,
        /// so editing an unused field doesn't restart its notifications.
        /// </summary>
        private static string Signature(ReminderRecord reminder)
        {
            return reminder.Metric switch
            {
                ReminderMetric.Odometer => $"Odometer|{reminder.Mileage}",
                ReminderMetric.Date => $"Date|{reminder.Date:yyyyMMdd}",
                _ => $"{reminder.Metric}|{reminder.Date:yyyyMMdd}|{reminder.Mileage}"
            };
        }

        private Dictionary<string, NotificationState> LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var text = File.ReadAllText(StatePath);
                    return JsonSerializer.Deserialize<Dictionary<string, NotificationState>>(text) ?? new Dictionary<string, NotificationState>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Notification state could not be read, starting fresh: {ex.Message}");
            }
            return new Dictionary<string, NotificationState>();
        }
        private void SaveState(Dictionary<string, NotificationState> state)
        {
            try
            {
                var directory = Path.GetDirectoryName(StatePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Notification state could not be saved: {ex.Message}");
            }
        }

        public async Task<int> RunCheckAsync(CancellationToken cancellationToken = default)
        {
            var recipients = GetRecipients();
            if (!recipients.Any())
            {
                return 0;
            }
            await _runLock.WaitAsync(cancellationToken);
            int sent = 0;
            try
            {
                var now = DateTime.Now;
                var state = LoadState();
                bool stateChanged = false;
                var allVehicles = _vehicleDataAccess.GetVehicles().Where(x => string.IsNullOrWhiteSpace(x.SoldDate)).ToList();
                var mileageCache = new Dictionary<int, int>();
                var reminderCache = new Dictionary<int, List<ReminderRecord>>();
                var liveKeys = new HashSet<string>();
                foreach (var recipient in recipients)
                {
                    var vehicles = recipient.UserId == -1 ? allVehicles : _userLogic.FilterUserVehicles(allVehicles, recipient.UserId);
                    bool quiet = InQuietHours(recipient.Config, now);
                    foreach (var vehicle in vehicles)
                    {
                        if (!reminderCache.TryGetValue(vehicle.Id, out var reminders))
                        {
                            reminders = _reminderRecordDataAccess.GetReminderRecordsByVehicleId(vehicle.Id);
                            reminderCache[vehicle.Id] = reminders;
                        }
                        if (!reminders.Any())
                        {
                            continue;
                        }
                        if (!mileageCache.TryGetValue(vehicle.Id, out var currentMileage))
                        {
                            currentMileage = _vehicleLogic.GetMaxMileage(vehicle.Id);
                            mileageCache[vehicle.Id] = currentMileage;
                        }
                        var viewModels = _reminderHelper.GetReminderRecordViewModels(reminders, currentMileage, now);
                        var pending = new List<(ReminderRecord Reminder, ReminderRecordViewModel ViewModel, NotificationStage Stage)>();
                        foreach (var reminder in reminders)
                        {
                            var key = $"{recipient.Key}|{reminder.Id}";
                            liveKeys.Add(key);
                            if (reminder.MuteNotifications)
                            {
                                continue;
                            }
                            var viewModel = viewModels.First(x => x.Id == reminder.Id);
                            var signature = Signature(reminder);
                            state.TryGetValue(key, out var existing);
                            if (existing != null && existing.Signature != signature)
                            {
                                //reminder rolled forward or was edited: start a new cycle.
                                state.Remove(key);
                                existing = null;
                                stateChanged = true;
                            }
                            var stage = GetStage(reminder, viewModel, recipient.Config, currentMileage, now);
                            if (stage == NotificationStage.None)
                            {
                                continue;
                            }
                            bool shouldSend = existing == null || stage > existing.Stage;
                            if (!shouldSend && existing != null && stage == NotificationStage.Overdue && recipient.Config.OverdueRepeatDays > 0)
                            {
                                shouldSend = (DateTime.UtcNow - existing.LastSentUtc).TotalDays >= recipient.Config.OverdueRepeatDays;
                            }
                            if (shouldSend)
                            {
                                pending.Add((reminder, viewModel, stage));
                            }
                        }
                        if (!pending.Any() || quiet)
                        {
                            continue;
                        }
                        var unit = vehicle.UseHours ? "h" : (recipient.UseMPG ? "mi" : "km");
                        var result = await SendForVehicleAsync(recipient.Config, vehicle, pending, currentMileage, unit, now);
                        if (result)
                        {
                            sent++;
                            foreach (var item in pending)
                            {
                                state[$"{recipient.Key}|{item.Reminder.Id}"] = new NotificationState { Signature = Signature(item.Reminder), Stage = item.Stage, LastSentUtc = DateTime.UtcNow };
                            }
                            stateChanged = true;
                        }
                    }
                }
                //forget reminders that no longer exist for the recipients we checked
                var recipientKeys = recipients.Select(x => x.Key + "|").ToList();
                var staleKeys = state.Keys.Where(x => recipientKeys.Any(y => x.StartsWith(y)) && !liveKeys.Contains(x)).ToList();
                foreach (var staleKey in staleKeys)
                {
                    state.Remove(staleKey);
                    stateChanged = true;
                }
                if (stateChanged)
                {
                    SaveState(state);
                }
                if (sent > 0)
                {
                    _logger.LogInformation($"ntfy: sent {sent} reminder notification(s)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"ntfy: reminder check failed: {ex.Message}");
            }
            finally
            {
                _runLock.Release();
            }
            return sent;
        }

        private static string StageTag(NotificationStage stage)
        {
            return stage switch
            {
                NotificationStage.Overdue => "red_circle",
                NotificationStage.Urgent => "orange_circle",
                NotificationStage.DueSoon => "yellow_circle",
                _ => "large_blue_circle"
            };
        }
        private static int StagePriority(NotificationStage stage)
        {
            return stage switch
            {
                NotificationStage.Overdue => 4,
                NotificationStage.Urgent => 4,
                _ => 3
            };
        }
        private DueItem ToDueItem(ReminderRecord reminder, ReminderRecordViewModel viewModel, int currentMileage, DateTime now)
        {
            return new DueItem
            {
                Id = reminder.Id,
                Description = reminder.Description,
                Urgency = viewModel.Urgency,
                UserMetric = reminder.Metric,
                Metric = viewModel.Metric,
                Date = reminder.Date,
                Mileage = reminder.Mileage,
                DueDays = (reminder.Date.Date - now.Date).Days,
                DueMileage = reminder.Mileage - currentMileage
            };
        }
        private string GetClickUrl(NtfyConfig config, int vehicleId)
        {
            var baseUrl = !string.IsNullOrWhiteSpace(config.AppUrl) ? config.AppUrl : _config.GetServerDomain();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return string.Empty;
            }
            return $"{baseUrl.TrimEnd('/')}/Vehicle/Index?vehicleId={vehicleId}&tab=reminder";
        }
        private async Task<bool> SendForVehicleAsync(NtfyConfig config, Vehicle vehicle, List<(ReminderRecord Reminder, ReminderRecordViewModel ViewModel, NotificationStage Stage)> pending, int currentMileage, string unit, DateTime now)
        {
            var vehicleName = GloveboxFormat.VehicleName(vehicle);
            var identifier = StaticHelper.GetVehicleIdentifier(vehicle);
            var vehicleLabel = string.IsNullOrWhiteSpace(identifier) ? vehicleName : $"{vehicleName} ({identifier})";
            var topStage = pending.Max(x => x.Stage);
            string title;
            var body = new StringBuilder();
            if (pending.Count == 1)
            {
                var item = pending[0];
                var due = ToDueItem(item.Reminder, item.ViewModel, currentMileage, now);
                var headline = GloveboxFormat.DueHeadline(due, unit);
                title = item.Stage == NotificationStage.HeadsUp
                    ? $"Heads-up: {item.Reminder.Description} {LowerFirst(headline)}"
                    : $"{item.Reminder.Description}: {LowerFirst(headline)}";
                body.AppendLine(vehicleLabel);
                var target = GloveboxFormat.DueTarget(due, unit);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    body.Append($"Due {target}");
                }
                if (due.HasOdometer && currentMileage > 0)
                {
                    body.Append($". Odometer is at {GloveboxFormat.Distance(currentMileage, unit)}");
                }
                body.Append('.');
            }
            else
            {
                title = $"{vehicleName}: {pending.Count} reminders need attention";
                body.AppendLine(vehicleLabel);
                foreach (var item in pending.OrderByDescending(x => x.Stage))
                {
                    var due = ToDueItem(item.Reminder, item.ViewModel, currentMileage, now);
                    body.AppendLine($"• {item.Reminder.Description}: {LowerFirst(GloveboxFormat.DueHeadline(due, unit))}");
                }
            }
            return await PublishAsync(config, title, body.ToString().Trim(), StagePriority(topStage), new List<string> { StageTag(topStage) }, GetClickUrl(config, vehicle.Id));
        }
        private static string LowerFirst(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return char.ToLowerInvariant(input[0]) + input.Substring(1);
        }

        private class NtfyMessage
        {
            public string Topic { get; set; } = string.Empty;
            public string? Title { get; set; }
            public string? Message { get; set; }
            public int? Priority { get; set; }
            public List<string>? Tags { get; set; }
            public string? Click { get; set; }
            public string? Icon { get; set; }
        }
        private async Task<bool> PublishAsync(NtfyConfig config, string title, string message, int priority, List<string> tags, string clickUrl)
        {
            var result = await PublishWithResultAsync(config, title, message, priority, tags, clickUrl);
            return result.Success;
        }
        private async Task<OperationResponse> PublishWithResultAsync(NtfyConfig config, string title, string message, int priority, List<string> tags, string clickUrl)
        {
            try
            {
                var serverUrl = string.IsNullOrWhiteSpace(config.ServerUrl) ? "https://ntfy.sh" : config.ServerUrl.Trim().TrimEnd('/');
                if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    serverUrl = $"https://{serverUrl}";
                }
                var payload = new NtfyMessage
                {
                    Topic = config.Topic.Trim(),
                    Title = title,
                    Message = message,
                    Priority = priority,
                    Tags = tags,
                    Click = string.IsNullOrWhiteSpace(clickUrl) ? null : clickUrl
                };
                var appUrl = !string.IsNullOrWhiteSpace(config.AppUrl) ? config.AppUrl : _config.GetServerDomain();
                if (!string.IsNullOrWhiteSpace(appUrl))
                {
                    payload.Icon = $"{appUrl.TrimEnd('/')}/defaults/glovebox_icon_192.png";
                }
                var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(config.AccessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken.Trim());
                }
                else if (!string.IsNullOrWhiteSpace(config.Username))
                {
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                }
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(20);
                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return OperationResponse.Succeed("Notification sent");
                }
                var responseText = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"ntfy: publish failed with {(int)response.StatusCode}: {responseText}");
                var reason = (int)response.StatusCode switch
                {
                    401 or 403 => "ntfy rejected the credentials. Check the access token or username and password.",
                    404 => "ntfy couldn't find that server URL. Check the server address.",
                    429 => "ntfy is rate limiting this server. Try again in a minute.",
                    _ => $"ntfy returned {(int)response.StatusCode}. Check the server URL and topic."
                };
                return OperationResponse.Failed(reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"ntfy: publish failed: {ex.Message}");
                return OperationResponse.Failed($"Couldn't reach the ntfy server: {ex.Message}");
            }
        }
        public async Task<OperationResponse> SendTestAsync(NtfyConfig config, string vehicleName)
        {
            if (string.IsNullOrWhiteSpace(config.Topic))
            {
                return OperationResponse.Failed("Add a topic first.");
            }
            var title = "Glovebox is connected";
            var message = string.IsNullOrWhiteSpace(vehicleName)
                ? "Reminder notifications will arrive here."
                : $"Reminder notifications for {vehicleName} will arrive here.";
            var baseUrl = !string.IsNullOrWhiteSpace(config.AppUrl) ? config.AppUrl : _config.GetServerDomain();
            return await PublishWithResultAsync(config, title, message, 3, new List<string> { "white_check_mark" }, string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/'));
        }
    }

    /// <summary>
    /// Evaluates reminders every 15 minutes, and shortly after any record changes.
    /// </summary>
    public class NtfyNotificationService : BackgroundService
    {
        private readonly INtfyNotificationLogic _ntfyLogic;
        private readonly ILogger<NtfyNotificationService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);
        public NtfyNotificationService(INtfyNotificationLogic ntfyLogic, ILogger<NtfyNotificationService> logger)
        {
            _ntfyLogic = ntfyLogic;
            _logger = logger;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                //let the app finish starting before the first check
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _ntfyLogic.RunCheckAsync(stoppingToken);
                    await _ntfyLogic.WaitForRequestAsync(Interval, stoppingToken);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    //give bursts of edits (e.g. a CSV import or a few quick records) time to settle
                    await Task.Delay(Debounce, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError($"ntfy: background worker stopped: {ex.Message}");
            }
        }
    }
}
