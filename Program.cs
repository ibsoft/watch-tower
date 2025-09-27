using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using Serilog;
using Serilog.Events;
using WatchTowerService.Services;

namespace WatchTowerService
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            BootstrapLogging();

            var isConsole = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
            if (isConsole)
            {
                Console.Title = "watch-tower (console)";
                Log.Information("Starting in console mode...");

                var configPath = ResolveConfigPath();
                using (var worker = new WatchWorker(configPath))
                {
                    Log.Information("Press Ctrl+C to exit. Running periodic checks...");
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; Environment.Exit(0); };
                    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                }
            }
            else
            {
                ServiceBase.Run(new ServiceBase[] { new WatchTowerService() });
            }
        }

        private static string ResolveConfigPath()
        {
            var fromConfig = ConfigurationManager.AppSettings["WatchTower:ConfigPath"];
            if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "appsettings.json");
        }

        private static void BootstrapLogging()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = ConfigurationManager.AppSettings["WatchTower:LogDir"] ?? "logs";
                var path = Path.Combine(baseDir, logDir, "watch-tower-.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("System", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.File(path, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
                    .CreateLogger();

                Log.Information("Logging initialized at {Path}", path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to bootstrap Serilog: " + ex);
            }
        }
    }
}
