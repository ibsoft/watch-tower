# watch-tower
Watches a windows service or a group of services and restart them if down - sends webhooks

Serilog (logging)

MinimumLevel: "Information"
The lowest level that will be logged. Levels (low→high): Verbose, Debug, Information, Warning, Error, Fatal.
With Information, you’ll see normal ops, warnings, and errors—but not very chatty debug traces.

WriteTo
Where to write logs.

{ "Name": "Console" }: writes to stdout (handy in --console mode).

{ "Name": "File", "Args": { ... } }

path: "logs\\watch-tower-.log": file path relative to the EXE folder. The dash before .log is used for rolling filenames like watch-tower-2025-09-27.log.

rollingInterval: "Day": a new file per day.

retainedFileCountLimit: 14: keep 14 daily files; older ones are deleted.

shared: true: allows multiple processes to write to the same file (prevents lock errors).

WatchTower (core behavior)

IntervalSeconds: 60
Polling interval. Every 60s it checks services and takes action. Minimum enforced in code: 5s.

ServiceOpTimeoutSeconds: 30
How long to wait for a service to stop/start before treating it as a timeout.

RestartAllInGroupIfAnyDown: true
Services are grouped by Group.

true: if one service in a group is down, restart all services in that group (useful when they depend on each other).

false: only restart the specific service(s) that are down.

Failure handling (when restarts keep failing)

MaxRestartAttempts: 3
Max failed restart attempts within the failure window (below) before we “quarantine” the service.

FailureWindowMinutes: 10
Rolling window to count failed attempts. If 3 failures happen within 10 minutes, quarantine kicks in.

QuarantineMinutes: 30
During quarantine the service is not retried at all (prevents infinite loops). After 30 minutes, retries can resume.

BackoffSecondsMin: 60 / BackoffSecondsMax: 600
Exponential backoff between retries after each failure. Roughly:
nextDelay = min(BackoffSecondsMax, BackoffSecondsMin * 2^(failCount-1))
e.g., with 60s min: 60s → 120s → 240s … capped at 600s.

IgnoreMissingDefault: false
Global default for how to handle nonexistent services (wrong name or not installed):

false: log a warning when a service is not found.

true: silently skip missing services unless overridden per-service.

SuccessResetThreshold: 3
After a service runs healthy for N consecutive checks, we clear its failure counters/backoff/quarantine (auto-recovery reset). This prevents one-off flukes from lingering.

Notifications

Notify.EventLogSource: "WatchTowerService"
Source name for writing to the Windows Application Event Log.
⚠️ On the first run, creating a new event source may require Administrator rights.

Notify.WebhookUrl: null
Optional URL to POST a simple JSON message when we enter quarantine (or other notable events). Put a Teams/Slack/Discord incoming webhook URL here to receive alerts.

Services (the targets)

Each entry describes a Windows service to monitor:

Name
ServiceName, not Display Name. (Find it via Get-Service | Select Name,DisplayName or Services.msc → Properties → Service name.)

Group
Logical grouping. Used by RestartAllInGroupIfAnyDown.

AutoRestart: true
If true, the watcher will attempt to restart it when down. If false, it will only log.

IgnoreMissing (per-service override)
If true, suppress “not found” warnings for that service (useful for optional/role-based services). Falls back to IgnoreMissingDefault if omitted.

(Optional) Machine
The remote computer name (e.g., "SRV-REMOTE01"). Requires network/RPC access and appropriate service-control privileges.
