using BattleNetPrefill.Api;

namespace BattleNetPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ParseHiddenFlags();

                Console.WriteLine($"BattleNetPrefill daemon v{ThisAssembly.Info.InformationalVersion}");

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                if (!useTcp)
                {
                    Console.WriteLine("Using Unix domain socket transport.");
                }

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutdown signal received...");
                    cts.Cancel();
                };

                using var maxLifetimeTimer = StartMaxLifetimeTimer(cts);

                if (useTcp)
                {
                    await DaemonMode.RunTcpAsync(tcpPort, cts.Token);
                }
                else
                {
                    await DaemonMode.RunAsync(socketPath, cts.Token);
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.VerboseLogs)
                {
                    Console.WriteLine(e.StackTrace);
                }
                return 1;
            }
        }

        private static System.Threading.Timer? StartMaxLifetimeTimer(CancellationTokenSource cts)
        {
            var maxLifetimeEnv = Environment.GetEnvironmentVariable("PREFILL_MAX_LIFETIME_SECONDS");
            if (!int.TryParse(maxLifetimeEnv, out var maxLifetimeSeconds) || maxLifetimeSeconds <= 0)
            {
                return null;
            }

            Console.WriteLine($"Max lifetime set to {maxLifetimeSeconds} seconds. Daemon will self-shutdown after this period.");

            return new System.Threading.Timer(
                _ =>
                {
                    Console.WriteLine($"\nMax lifetime of {maxLifetimeSeconds} seconds reached. Shutting down daemon...");
                    cts.Cancel();
                },
                state: null,
                dueTime: TimeSpan.FromSeconds(maxLifetimeSeconds),
                period: Timeout.InfiniteTimeSpan);
        }

        private static void ParseHiddenFlags()
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")) || args.Any(e => e.Contains("--verbose")))
            {
                Console.WriteLine($"Using verbose logging flag. Displaying verbose logging...");
                AppConfig.VerboseLogs = true;
            }

            if (args.Any(e => e.Contains("--no-download")))
            {
                Console.WriteLine($"Using --no-download flag. Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
            }

            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                Console.WriteLine($"Using --nocache flag. Will always re-download indexes...");
                AppConfig.NoLocalCache = true;
            }

            if (AppConfig.VerboseLogs || AppConfig.SkipDownloads || AppConfig.NoLocalCache)
            {
                Console.WriteLine();
                Console.WriteLine(new string('─', 60));
            }
        }
    }
}
