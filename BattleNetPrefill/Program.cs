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

                Console.WriteLine($"""
                    ╔═══════════════════════════════════════════════════════════╗
                    ║              BattleNetPrefill Daemon                      ║
                    ║                  v{ThisAssembly.Info.InformationalVersion,-20}             ║
                    ╚═══════════════════════════════════════════════════════════╝

                    Battle.net content is anonymous - no account login required.

                    """);

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                if (!useTcp)
                {
                    Console.WriteLine("Using Unix Domain Socket for reliable, low-latency IPC.");
                    Console.WriteLine();
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
