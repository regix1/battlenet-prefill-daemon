using System.Text.Json.Serialization;

namespace BattleNetPrefill.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(List<OwnedGame>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PrefillResult))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(PrefillProgressUpdate))]
[JsonSerializable(typeof(ClearCacheResult))]
[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(SelectedAppsStatus))]
[JsonSerializable(typeof(CacheStatusResult))]
[JsonSerializable(typeof(AppCacheStatus))]
// Socket event types
[JsonSerializable(typeof(SocketEvent<PrefillProgressUpdate>))]
[JsonSerializable(typeof(SocketEvent<AuthStateData>))]
[JsonSerializable(typeof(AuthStateData))]
[JsonSerializable(typeof(object))]
internal sealed partial class DaemonSerializationContext : JsonSerializerContext
{
}

public class StatusData
{
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }
}

public class CommandRequest
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string>? Parameters { get; set; }
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
