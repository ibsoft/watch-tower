using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using WatchTowerService.Models;

namespace WatchTowerService.Services
{
    public static class ServiceOps
    {
        // (status, ok, exists)
        public static Tuple<ServiceControllerStatus, bool, bool> SafeGetStatus(ServiceItem svc)
        {
            try
            {
                var machine = string.IsNullOrWhiteSpace(svc.Machine) ? "." : svc.Machine;
                ServiceController[] all = ServiceController.GetServices(machine);
                bool exists = all.Any(s => string.Equals(s.ServiceName, svc.Name, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    return Tuple.Create(ServiceControllerStatus.Stopped, false, false);

                using (var sc = CreateController(svc))
                {
                    return Tuple.Create(sc.Status, true, true);
                }
            }
            catch
            {
                return Tuple.Create(ServiceControllerStatus.Stopped, false, true);
            }
        }

        public static bool Restart(ServiceItem svc, TimeSpan timeout, Action<string> logInfo, Action<string, Exception> logError, CancellationToken ct)
        {
            try
            {
                using (var sc = CreateController(svc))
                {
                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                    {
                        logInfo($"Stopping {SvcName(svc)}...");
                        try { sc.Stop(); } catch { }
                        WaitStatus(sc, ServiceControllerStatus.Stopped, timeout, ct);
                    }

                    logInfo($"Starting {SvcName(svc)}...");
                    sc.Start();
                    WaitStatus(sc, ServiceControllerStatus.Running, timeout, ct);

                    logInfo($"Service {SvcName(svc)} restarted successfully.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                logError($"Failed to restart service {SvcName(svc)}", ex);
                return false;
            }
        }

        public static bool IsDown(ServiceControllerStatus status)
        {
            return status == ServiceControllerStatus.Stopped
                || status == ServiceControllerStatus.Paused
                || status == ServiceControllerStatus.StopPending
                || status == ServiceControllerStatus.PausePending;
        }

        private static ServiceController CreateController(ServiceItem svc)
        {
            return string.IsNullOrWhiteSpace(svc.Machine)
                ? new ServiceController(svc.Name)
                : new ServiceController(svc.Name, svc.Machine);
        }

        private static void WaitStatus(ServiceController sc, ServiceControllerStatus desired, TimeSpan timeout, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < timeout && !ct.IsCancellationRequested)
            {
                sc.Refresh();
                if (sc.Status == desired) return;
                Thread.Sleep(1000);
            }

            sc.Refresh();
            if (sc.Status != desired)
                throw new System.TimeoutException($"Timed out waiting for {sc.ServiceName} -> {desired} (last={sc.Status})");
        }

        private static string SvcName(ServiceItem svc)
            => string.IsNullOrWhiteSpace(svc.Machine) ? svc.Name : $"{svc.Name}@{svc.Machine}";
    }
}
