using System.ServiceProcess;
using Serilog;
using WatchTowerService.Services;
using System;
using System.Configuration;
using System.IO;

namespace WatchTowerService
{
    public partial class WatchTowerService : ServiceBase
    {
        private WatchWorker _worker;

        public WatchTowerService()
        {
            ServiceName = "watch-tower";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            Log.Information("watch-tower service starting...");
            var configPath = ResolveConfigPath();
            _worker = new WatchWorker(configPath);
            Log.Information("watch-tower service started.");
        }

        protected override void OnStop()
        {
            Log.Information("watch-tower service stopping...");
            _worker?.Dispose();
            Log.Information("watch-tower service stopped.");
        }

        private static string ResolveConfigPath()
        {
            var fromConfig = ConfigurationManager.AppSettings["WatchTower:ConfigPath"];
            if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "appsettings.json");
        }
    }
}
