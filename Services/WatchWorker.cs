using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using WatchTowerService.Models;

namespace WatchTowerService.Services
{
    public sealed class WatchWorker : IDisposable
    {
        private readonly string _configPath;
        private readonly FileSystemWatcher _fsw;
        private readonly Timer _timer;
        private readonly object _lock = new object();

        private WatchConfig _cfg = new WatchConfig();
        private TimeSpan _interval = TimeSpan.FromMinutes(1);
        private TimeSpan _opTimeout = TimeSpan.FromSeconds(30);

        private int _maxAttempts = 3;
        private TimeSpan _failureWindow = TimeSpan.FromMinutes(10);
        private TimeSpan _quarantine = TimeSpan.FromMinutes(30);
        private int _backoffMin = 60, _backoffMax = 600;
        private string _eventLogSource = "WatchTowerService";
        private string _webhookUrl = null;
        private bool _ignoreMissingDefault = false;
        private int _successResetThreshold = 3;

        private sealed class FailState
        {
            public int Count;
            public DateTime FirstTsUtc = DateTime.MinValue;
            public DateTime QuarantineUntilUtc = DateTime.MinValue;
            public DateTime NextAllowedUtc = DateTime.MinValue;
            public int ConsecutiveSuccess = 0;
        }

        private readonly Dictionary<string, FailState> _fails = new Dictionary<string, FailState>(StringComparer.OrdinalIgnoreCase);
        private string Key(ServiceItem s) { return string.IsNullOrWhiteSpace(s.Machine) ? s.Name : (s.Name + "@" + s.Machine); }

        public WatchWorker(string configPath)
        {
            _configPath = configPath;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory);

            _timer = new Timer(OnTick, null, _interval, _interval);
            LoadConfig();

            _fsw = new FileSystemWatcher(Path.GetDirectoryName(_configPath) ?? ".", Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _fsw.Changed += (_, __) => DebouncedReload();
            _fsw.Created += (_, __) => DebouncedReload();
            _fsw.Renamed += (_, __) => DebouncedReload();
            _fsw.EnableRaisingEvents = true;
        }

        private void DebouncedReload()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(500);
                try { LoadConfig(); }
                catch (Exception ex) { Log.Error(ex, "Failed to hot-reload configuration"); }
            });
        }

        private void LoadConfig()
        {
            lock (_lock)
            {
                if (!File.Exists(_configPath))
                    throw new FileNotFoundException("Configuration file not found", _configPath);

                var json = File.ReadAllText(_configPath);
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("Configuration file is empty: " + _configPath);

                var root = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                WatchConfig cfg = new WatchConfig();
                if (root != null && root.ContainsKey("WatchTower"))
                {
                    var wt = (root["WatchTower"] as Newtonsoft.Json.Linq.JObject)?.ToObject<WatchConfig>();
                    if (wt != null) cfg = wt;
                }

                _cfg = cfg;
                _interval = TimeSpan.FromSeconds(Math.Max(5, _cfg.IntervalSeconds));
                _opTimeout = TimeSpan.FromSeconds(Math.Max(5, _cfg.ServiceOpTimeoutSeconds));

                Func<string,int,int> getInt = (name, defVal) => {
                    var jt = (root["WatchTower"] as Newtonsoft.Json.Linq.JObject)?[name];
                    return jt != null ? jt.Value<int>() : defVal;
                };

                _maxAttempts = Math.Max(1, getInt("MaxRestartAttempts", 3));
                _failureWindow = TimeSpan.FromMinutes(Math.Max(1, getInt("FailureWindowMinutes", 10)));
                _quarantine = TimeSpan.FromMinutes(Math.Max(1, getInt("QuarantineMinutes", 30)));
                _backoffMin = Math.Max(10, getInt("BackoffSecondsMin", 60));
                _backoffMax = Math.Max(_backoffMin, getInt("BackoffSecondsMax", 600));

                var notify = (root["WatchTower"] as Newtonsoft.Json.Linq.JObject)?["Notify"] as Newtonsoft.Json.Linq.JObject;
                _eventLogSource = notify?["EventLogSource"]?.Value<string>() ?? "WatchTowerService";
                _webhookUrl = notify?["WebhookUrl"]?.Value<string>();

                _ignoreMissingDefault = ((root["WatchTower"] as Newtonsoft.Json.Linq.JObject)?["IgnoreMissingDefault"])?.Value<bool?>() ?? false;
                _successResetThreshold = Math.Max(1, ((root["WatchTower"] as Newtonsoft.Json.Linq.JObject)?["SuccessResetThreshold"])?.Value<int?>() ?? 3);

                _timer.Change(TimeSpan.Zero, _interval);
                Log.Information("Config loaded. Interval={Interval}s, OpTimeout={OpTimeout}s, Services={SvcCount}, RestartAllInGroupIfAnyDown={RestartGroup}",
                    _cfg.IntervalSeconds, _cfg.ServiceOpTimeoutSeconds, _cfg.Services.Count, _cfg.RestartAllInGroupIfAnyDown);
            }
        }

        private void OnTick(object state)
        {
            lock (_lock)
            {
                try { RunOnce(); }
                catch (Exception ex) { Log.Error(ex, "RunOnce failed"); }
            }
        }

        public void RunOnce()
        {
            var ct = CancellationToken.None;
            var groups = _cfg.Services
                .GroupBy(s => string.IsNullOrWhiteSpace(s.Group) ? "__ungrouped__" : s.Group)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var kv in groups)
            {
                var groupName = kv.Key;
                var services = kv.Value;
                var downList = new List<ServiceItem>();

                foreach (var svc in services)
                {
                    var tup = ServiceOps.SafeGetStatus(svc);
                    var status = tup.Item1; var ok = tup.Item2; var exists = tup.Item3;

                    if (!exists)
                    {
                        bool ignoreMissing = svc.IgnoreMissing ?? _ignoreMissingDefault;
                        if (!ignoreMissing)
                            Log.Warning("Service {Service} not found on machine {Machine}.", svc.Name, string.IsNullOrWhiteSpace(svc.Machine) ? "." : svc.Machine);
                        continue;
                    }

                    Log.Debug("Checked {Service} (group={Group}, status={Status}, ok={Ok})", svc.Name, groupName, status, ok);

                    if (ok && ServiceOps.IsDown(status))
                    {
                        downList.Add(svc);
                    }
                    else if (ok && status == System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        var keyOk = Key(svc);
                        FailState fOk;
                        if (!_fails.TryGetValue(keyOk, out fOk)) _fails[keyOk] = fOk = new FailState();
                        fOk.ConsecutiveSuccess++;
                        if (fOk.ConsecutiveSuccess >= _successResetThreshold)
                        {
                            fOk.Count = 0;
                            fOk.FirstTsUtc = DateTime.MinValue;
                            fOk.QuarantineUntilUtc = DateTime.MinValue;
                            fOk.NextAllowedUtc = DateTime.MinValue;
                            fOk.ConsecutiveSuccess = 0;
                            Log.Information("Auto-recovery reset for {Service} after {N} healthy checks.", keyOk, _successResetThreshold);
                        }
                    }
                }

                if (downList.Count == 0) continue;

                if (_cfg.RestartAllInGroupIfAnyDown)
                {
                    Log.Warning("Group '{Group}' has {Count} down; restarting ALL services in group...", groupName, downList.Count);
                    foreach (var svc in services.Where(s => s.AutoRestart))
                    {
                        AttemptRestart(ct, svc);
                    }
                }
                else
                {
                    foreach (var svc in downList.Where(s => s.AutoRestart))
                    {
                        AttemptRestart(ct, svc);
                    }
                }
            }
        }

        private void AttemptRestart(CancellationToken ct, ServiceItem svc)
        {
            var key = Key(svc);
            FailState f;
            if (!_fails.TryGetValue(key, out f))
                _fails[key] = f = new FailState();

            var now = DateTime.UtcNow;

            if (f.QuarantineUntilUtc > now)
            {
                Log.Warning("Service {Service} in quarantine until {Until}, skipping restarts.", key, f.QuarantineUntilUtc);
                return;
            }

            if (f.NextAllowedUtc > now)
            {
                Log.Information("Backoff active for {Service} until {Until}, skipping.", key, f.NextAllowedUtc);
                return;
            }

            if (f.FirstTsUtc == DateTime.MinValue || (now - f.FirstTsUtc) > _failureWindow)
            {
                f.FirstTsUtc = now;
                f.Count = 0;
            }

            Log.Warning("Service {Service} is down; restarting...", svc.Name);
            var okRestart = ServiceOps.Restart(svc, _opTimeout, msg => Log.Information(msg), (m, ex) => Log.Error(ex, m), ct);

            if (!okRestart)
            {
                f.Count++;
                var backoff = TimeSpan.FromSeconds(Math.Min(_backoffMax, _backoffMin * (int)Math.Pow(2, Math.Max(0, f.Count - 1))));
                f.NextAllowedUtc = now.Add(backoff);

                if (f.Count >= _maxAttempts && (now - f.FirstTsUtc) <= _failureWindow)
                {
                    f.QuarantineUntilUtc = now.Add(_quarantine);
                    WriteEventAndNotify(key, $"Entered quarantine for {_quarantine.TotalMinutes} minutes after {f.Count} failed restarts in {_failureWindow.TotalMinutes} minutes.", true);
                    Log.Error("Service {Service} entered quarantine for {Minutes} minutes.", key, _quarantine.TotalMinutes);
                }
                else
                {
                    Log.Warning("Restart failed for {Service}. Failures={Count}/{Max}. Next retry after {Backoff}s.",
                        key, f.Count, _maxAttempts, backoff.TotalSeconds);
                }
            }
            else
            {
                f.Count = 0;
                f.FirstTsUtc = DateTime.MinValue;
                f.QuarantineUntilUtc = DateTime.MinValue;
                f.NextAllowedUtc = DateTime.MinValue;
                f.ConsecutiveSuccess = 0;
            }
        }

        private void WriteEventAndNotify(string serviceKey, string message, bool error = true)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_eventLogSource))
                {
                    if (!System.Diagnostics.EventLog.SourceExists(_eventLogSource))
                    {
                        System.Diagnostics.EventLog.CreateEventSource(_eventLogSource, "Application");
                    }
                    var type = error ? System.Diagnostics.EventLogEntryType.Error : System.Diagnostics.EventLogEntryType.Warning;
                    System.Diagnostics.EventLog.WriteEntry(_eventLogSource, serviceKey + ": " + message, type);
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(_webhookUrl))
                {
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                        var payload = "{\"text\":\"" + serviceKey.Replace("\"","'") + ": " + message.Replace("\"","'") + "\"}";
                        wc.UploadString(_webhookUrl, payload);
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _fsw.Dispose();
            _timer.Dispose();
        }
    }
}
