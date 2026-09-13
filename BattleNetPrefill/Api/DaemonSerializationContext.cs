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
[JsonSerializable(typeof(PrefillStart))]
[JsonSerializable(typeof(RunSnapshot))]
[JsonSerializable(typeof(OperationPage))]
internal sealed partial class DaemonSerializationContext : JsonSerializerContext
{
}
