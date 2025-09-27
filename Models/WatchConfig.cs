using System.Collections.Generic;

namespace WatchTowerService.Models
{
    public sealed class WatchConfig
    {
        public int IntervalSeconds { get; set; } = 60;
        public bool RestartAllInGroupIfAnyDown { get; set; } = false;
        public int ServiceOpTimeoutSeconds { get; set; } = 30;

        public int MaxRestartAttempts { get; set; } = 3;
        public int FailureWindowMinutes { get; set; } = 10;
        public int QuarantineMinutes { get; set; } = 30;
        public int BackoffSecondsMin { get; set; } = 60;
        public int BackoffSecondsMax { get; set; } = 600;

        public bool IgnoreMissingDefault { get; set; } = false;
        public int SuccessResetThreshold { get; set; } = 3;

        public NotifyConfig Notify { get; set; } = new NotifyConfig();
        public List<ServiceItem> Services { get; set; } = new List<ServiceItem>();
    }

    public sealed class NotifyConfig
    {
        public string EventLogSource { get; set; } = "WatchTowerService";
        public string WebhookUrl { get; set; } = null;
    }

    public sealed class ServiceItem
    {
        public string Name { get; set; } = string.Empty;
        public string Machine { get; set; }
        public string Group { get; set; }
        public bool AutoRestart { get; set; } = true;
        public bool? IgnoreMissing { get; set; }
    }
}
