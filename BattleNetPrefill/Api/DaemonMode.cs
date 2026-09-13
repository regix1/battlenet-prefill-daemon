#nullable enable

namespace BattleNetPrefill.Api;

/// <summary>
/// Runs BattleNetPrefill in daemon mode using Unix Domain Socket or TCP for IPC.
/// </summary>
public static class DaemonMode
{
    public static async Task RunAsync(
        string socketPath = "/responses/daemon.sock",
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Starting BattleNetPrefill daemon on Unix socket {socketPath}");

        using var socketInterface = new SocketCommandInterface(socketPath);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
    }

    public static async Task RunTcpAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Starting BattleNetPrefill daemon on TCP port {port}");

        using var socketInterface = new SocketCommandInterface(port);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
    }
}
