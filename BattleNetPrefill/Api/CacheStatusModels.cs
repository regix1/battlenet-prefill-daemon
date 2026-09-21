#nullable enable

using System.Text.Json.Serialization;

namespace BattleNetPrefill.Api;

public sealed class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
    public int? Version { get; init; }
}

public sealed class AppCacheStatus
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsUpToDate { get; init; }
    public CacheOutcome? Outcome { get; init; }
    public CacheReason? Reason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheOutcome>))]
public enum CacheOutcome
{
    Current,
    Outdated,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheReason>))]
public enum CacheReason
{
    MissingApp,
    ManifestUnavailable,
    NoCacheEvidence,
    InspectionFailed,
    DeadlineReached
}
