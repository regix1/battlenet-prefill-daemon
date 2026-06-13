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
        Console.WriteLine("Starting BattleNetPrefill daemon...");
        Console.WriteLine($"Socket path: {socketPath}");
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ UNIX SOCKET IPC                                              │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Reliable bidirectional communication                       │");
        Console.WriteLine("│ • Low latency (<1ms)                                         │");
        Console.WriteLine("│ • Works in both host and bridge Docker network modes         │");
        Console.WriteLine("│ • Real-time progress streaming                               │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ SECURITY                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Battle.net content is anonymous - NO account login         │");
        Console.WriteLine("│ • Socket transport secured by PREFILL_SOCKET_SECRET (HMAC)   │");
        Console.WriteLine("│ • Commands available immediately on connect                  │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

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
        Console.WriteLine("Starting BattleNetPrefill daemon (TCP mode)...");
        Console.WriteLine($"TCP port: {port}");
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ TCP IPC                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Reliable bidirectional communication                       │");
        Console.WriteLine("│ • Useful for Windows Docker Desktop bind mounts              │");
        Console.WriteLine("│ • Real-time progress streaming                               │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ SECURITY                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Battle.net content is anonymous - NO account login         │");
        Console.WriteLine("│ • Socket transport secured by PREFILL_SOCKET_SECRET (HMAC)   │");
        Console.WriteLine("│ • Commands available immediately on connect                  │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

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

public class PrefillProgressUpdate
{
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppId")]
    public string? CurrentAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds => Elapsed.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public string? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds => TotalTime.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
