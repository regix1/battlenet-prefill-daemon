#nullable enable

using LancachePrefill.Common;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BattleNetPrefill.Api;

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }

    /// <summary>
    /// Optional explicit list of TACT product codes to prefill. When empty, falls back to
    /// the selected-apps file (or the full catalog when <see cref="DownloadAllOwnedGames"/> is set).
    /// </summary>
#pragma warning disable CA2227 // The socket request serializer replaces this collection from JSON.
    public List<string>? Products { get; set; }
#pragma warning restore CA2227
}

public class PrefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class ClearCacheResult
{
    public bool Success { get; init; }
    public int FileCount { get; init; }
    public long BytesCleared { get; init; }
    public string? Message { get; init; }
}

public class AppStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long DownloadSize { get; init; }
    public bool IsUpToDate { get; init; }
}

public class SelectedAppsStatus
{
    public List<AppStatus> Apps { get; init; } = new();
    public long TotalDownloadSize { get; init; }
    public string? Message { get; init; }
}

public class OwnedGame
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public class AppCacheStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsUpToDate { get; init; }
}

public class StatusData
{
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }
    public int ProtocolVersion { get; init; } = PrefillProtocol.Version;
    public IReadOnlyList<string> Features { get; init; } = PrefillProtocol.Features;
    public string DaemonInstanceId { get; init; } = string.Empty;
    public int MaxConcurrentRuns { get; init; }
    public int MaxConcurrentRequests { get; init; }
    public int RetentionHours { get; init; } = PrefillProtocol.RetentionHours;
    public int RetentionOperations { get; init; } = PrefillProtocol.RetentionOperations;
    public int RetentionItems { get; init; } = PrefillProtocol.RetentionItems;
    public IReadOnlyList<RunSnapshot> ActiveOperations { get; init; } = Array.Empty<RunSnapshot>();
    public IReadOnlyList<RunSnapshot> RecentOperations { get; init; } = Array.Empty<RunSnapshot>();
}

public class CommandRequest
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
#pragma warning disable CA2227 // The socket request serializer replaces this collection from JSON.
    public Dictionary<string, string>? Parameters { get; set; }
#pragma warning restore CA2227
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CommandResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }
    public bool RequiresLogin { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public class PrefillProgressUpdate
{
    public string? OperationId { get; init; }
    public string? DaemonInstanceId { get; init; }
    public long Sequence { get; init; }
    public string? Reason { get; init; }
    public int SkippedApps { get; init; }
    public int CancelledApps { get; init; }
    public RunSnapshot? Operation { get; init; }
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

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public class AppDownloadInfo
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public int ChunkCount { get; init; }
}

public class DownloadProgressInfo
{
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Socket state to broadcast for this update. Defaults to "downloading" (live byte transfer).
    /// The pre-transfer phase (metadata / building archive indexes / determining files) emits
    /// "preparing" so the UI shows activity (and the known total) before the first byte flows.
    /// </summary>
    public string State { get; init; } = "downloading";
}

public enum AppDownloadResult
{
    Success,
    AlreadyUpToDate,
    Failed,
    Skipped,
    NoDepotsToDownload
}

public class PrefillSummary
{
    public int TotalApps { get; init; }
    public int UpdatedApps { get; init; }
    public int AlreadyUpToDate { get; init; }
    public int FailedApps { get; init; }
    public long TotalBytesTransferred { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public enum SocketServerMode
{
    UnixSocket,
    Tcp
}

internal sealed record PrefillSettings
{
    public required RequestBudget Budget { get; init; }
    public required string OperationId { get; init; }
    public int MaxConcurrency { get; init; } = 25;
    public bool SkipDownloads { get; init; }
    public bool NoLocalCache { get; init; }
    public string CacheDirectory { get; init; } = AppConfig.CacheDir;
    public Func<HttpClient>? CreateClient { get; init; }
    public string? LancacheAddress { get; init; }
    public Action<string, string>? CommitFile { get; init; }
    public Func<CancellationToken, Task>? BeforeCommit { get; init; }
    public PrefillRun? Run { get; init; }
}

public sealed record PrefillStart(bool Success, string RunId, string DaemonInstanceId, string State);

public class SocketEvent<T>
{
    public string Type { get; init; } = string.Empty;
    public T? Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class AuthStateData
{
    public string State { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? DisplayName { get; init; }
}
