#nullable enable

using BattleNetPrefill.Handlers;
using BattleNetPrefill.Web;
using LancachePrefill.Common.SelectAppsTui;
using Spectre.Console;
using System.Diagnostics;

namespace BattleNetPrefill.Api;

/// <summary>
/// High-level programmatic API for Battle.net Prefill operations.
///
/// Battle.net content is served anonymously from Blizzard's public CDN — there is NO
/// account login, no credentials, and no concept of an "owned" library. "Owned games"
/// is therefore the fixed catalog of TACT products the upstream tool knows how to prefill.
///
/// This wraps the upstream <see cref="TactProductHandler"/> / <see cref="CdnRequestManager"/>
/// in-process and routes their Spectre console output to <see cref="IPrefillProgress"/> via
/// <see cref="ApiConsoleAdapter"/>.
/// </summary>
public sealed class BattleNetPrefillApi : IDisposable
{
    private readonly IPrefillProgress _progress;
    private readonly IAnsiConsole _console;

    private List<string>? _selectedAppsCache;
    private bool _isInitialized;
    private bool _isDisposed;

    public BattleNetPrefillApi(IPrefillProgress? progress = null)
    {
        _progress = progress ?? NullProgress.Instance;
        _console = new ApiConsoleAdapter(_progress);
    }

    public bool IsInitialized => _isInitialized;

    public string? DisplayName => "Battle.net";

    /// <summary>
    /// Initializes the API. Battle.net is anonymous, so there is no login step — this only
    /// marks the API as ready. Kept async to mirror the daemon contract used by the manager.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return Task.CompletedTask;

        _progress.OnOperationStarted("Initializing Battle.net prefill");
        _isInitialized = true;
        _progress.OnOperationCompleted("Initializing Battle.net prefill", TimeSpan.Zero);
        _progress.OnLog(LogLevel.Info, "Battle.net prefill ready (anonymous - no login required)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the fixed TACT product catalog. Battle.net has no per-account library, so this
    /// is every product the prefill tool supports: { AppId = TACT product code, Name = display name }.
    /// </summary>
    public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var result = TactProduct.AllEnumValues
            .Select(p => new OwnedGame { AppId = p.ProductCode, Name = p.DisplayName })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _progress.OnLog(LogLevel.Info, $"Returning {result.Count} Battle.net products");
        return Task.FromResult(result);
    }

    public List<string> GetSelectedApps()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (_selectedAppsCache != null && _selectedAppsCache.Count > 0)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache;
        }

        var fileApps = TactProductHandler.LoadPreviouslySelectedApps()
            .Select(p => p.ProductCode)
            .ToList();
        _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Loaded {fileApps.Count} apps from file");
        return fileApps;
    }

    public void SetSelectedApps(IEnumerable<string> appIds)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIdList = appIds.ToList();
        _selectedAppsCache = appIdList;

        var tuiApps = appIdList.Select(id =>
        {
            var product = ResolveProduct(id);
            return new TuiAppInfo(id, product?.DisplayName ?? id) { IsSelected = true };
        }).ToList();

        var handler = new TactProductHandler(_console);
        handler.SetAppsAsSelected(tuiApps);
        _progress.OnLog(LogLevel.Info, $"Set {tuiApps.Count} apps for prefill");
    }

    /// <summary>
    /// Reports cache status by checking the per-product prefill marker files written after a
    /// successful prefill. A product is considered "up to date" when a prefilledVersion marker
    /// exists for it (i.e. it has been prefilled before). A live CDN version comparison is
    /// performed by the actual prefill run; this status is a lightweight, network-free check.
    /// </summary>
    public Task<CacheStatusResult> CheckCacheStatusAsync(List<string> appIds, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (appIds.Count == 0)
        {
            return Task.FromResult(new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No app IDs provided"
            });
        }

        var apps = new List<AppCacheStatus>();
        foreach (var appId in appIds.Distinct())
        {
            var product = ResolveProduct(appId);
            apps.Add(new AppCacheStatus
            {
                AppId = appId,
                Name = product?.DisplayName ?? appId,
                IsUpToDate = HasPrefillMarker(appId)
            });
        }

        return Task.FromResult(new CacheStatusResult
        {
            Apps = apps,
            Message = $"Checked {apps.Count} apps"
        });
    }

    /// <summary>
    /// Status of the currently selected apps, including whether each has been prefilled before.
    /// </summary>
    public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var selectedAppIds = GetSelectedApps();
        if (selectedAppIds.Count == 0)
        {
            return Task.FromResult(new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            });
        }

        var apps = selectedAppIds.Select(appId =>
        {
            var product = ResolveProduct(appId);
            return new AppStatus
            {
                AppId = appId,
                Name = product?.DisplayName ?? appId,
                DownloadSize = 0,
                IsUpToDate = HasPrefillMarker(appId)
            };
        }).ToList();

        return Task.FromResult(new SelectedAppsStatus
        {
            Apps = apps,
            TotalDownloadSize = 0
        });
    }

    /// <summary>
    /// Runs the prefill operation, emitting structured progress events per product.
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        options ??= new PrefillOptions();

        _progress.OnOperationStarted("Prefill operation");
        var timer = Stopwatch.StartNew();

        // Resolve the set of products to prefill.
        List<TactProduct> products;
        if (options.Products is { Count: > 0 })
        {
            products = options.Products
                .Select(ResolveProduct)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
        }
        else if (options.DownloadAllOwnedGames)
        {
            products = TactProduct.AllEnumValues.ToList();
        }
        else
        {
            products = TactProductHandler.LoadPreviouslySelectedApps();
        }

        products = products.Distinct().ToList();

        if (products.Count == 0)
        {
            _progress.OnError("No apps selected for prefill. Select apps first or pass 'all'.");
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = "No apps selected for prefill",
                TotalTime = timer.Elapsed
            };
        }

        var handler = new TactProductHandler(_console, forcePrefill: options.Force);

        var updated = 0;
        var alreadyUpToDate = 0;
        var failed = 0;

        try
        {
            foreach (var product in products)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var appInfo = new AppDownloadInfo { AppId = product.ProductCode, Name = product.DisplayName };
                _progress.OnAppStarted(appInfo);

                var versionBefore = ReadPrefillMarker(product.ProductCode);

                try
                {
                    await handler.ProcessProductAsync(product);

                    var versionAfter = ReadPrefillMarker(product.ProductCode);

                    // If the marker is unchanged and existed before, the product was already up to date.
                    if (!options.Force && versionBefore != null && versionBefore == versionAfter)
                    {
                        alreadyUpToDate++;
                        _progress.OnAppCompleted(appInfo, AppDownloadResult.AlreadyUpToDate);
                    }
                    else
                    {
                        updated++;
                        _progress.OnAppCompleted(appInfo, AppDownloadResult.Success);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _progress.OnLog(LogLevel.Warning, $"Prefill failed for {product.DisplayName}: {ex.Message}");
                    _progress.OnAppCompleted(appInfo, AppDownloadResult.Failed);
                }
            }

            timer.Stop();

            _progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = products.Count,
                UpdatedApps = updated,
                AlreadyUpToDate = alreadyUpToDate,
                FailedApps = failed,
                TotalBytesTransferred = 0,
                TotalTime = timer.Elapsed
            });

            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            return new PrefillResult
            {
                Success = failed == 0,
                ErrorMessage = failed == 0 ? null : $"{failed} product(s) failed to prefill",
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = "Prefill cancelled",
                TotalTime = timer.Elapsed
            };
        }
        catch (Exception ex)
        {
            _progress.OnError("Prefill operation failed", ex);
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TotalTime = timer.Elapsed
            };
        }
    }

    private static TactProduct? ResolveProduct(string appId)
    {
        return TactProduct.AllEnumValues
            .FirstOrDefault(p => string.Equals(p.ProductCode, appId, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrefillMarkerPath(string productCode)
        => Path.Combine(AppConfig.CacheDir, $"prefilledVersion-{productCode}.txt");

    private static bool HasPrefillMarker(string productCode)
        => File.Exists(PrefillMarkerPath(productCode));

    private static string? ReadPrefillMarker(string productCode)
    {
        var path = PrefillMarkerPath(productCode);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static (int FileCount, long TotalBytes)? GetCacheStats()
    {
        var cacheDir = new DirectoryInfo(AppConfig.CacheDir);
        if (!cacheDir.Exists)
            return null;

        var files = cacheDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(e => e.Length));
    }

    public static ClearCacheResult ClearCache()
    {
        var stats = GetCacheStats();
        if (stats is not { FileCount: > 0 })
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        var (fileCount, totalBytes) = stats.Value;

        try
        {
            Directory.Delete(AppConfig.CacheDir, true);
            Directory.CreateDirectory(AppConfig.CacheDir);
            var clearedSize = ByteSize.FromBytes(totalBytes);
            return new ClearCacheResult
            {
                Success = true,
                FileCount = fileCount,
                BytesCleared = totalBytes,
                Message = $"Cleared {fileCount} files ({clearedSize.ToDecimalString()})"
            };
        }
        catch (Exception ex)
        {
            return new ClearCacheResult { Success = false, FileCount = 0, BytesCleared = 0, Message = $"Failed to clear cache: {ex.Message}" };
        }
    }

    public static ClearCacheResult GetCacheInfo()
    {
        var stats = GetCacheStats();
        if (stats == null)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is empty" };
        }

        var (fileCount, totalBytes) = stats.Value;
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = fileCount,
            BytesCleared = totalBytes,
            Message = $"Cache contains {fileCount} files ({cacheSize.ToDecimalString()})"
        };
    }

    public void Shutdown()
    {
        _isInitialized = false;
        _progress.OnLog(LogLevel.Info, "Battle.net prefill shut down");
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        Shutdown();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("BattleNetPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(BattleNetPrefillApi));
    }
}

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }

    /// <summary>
    /// Optional explicit list of TACT product codes to prefill. When empty, falls back to
    /// the selected-apps file (or the full catalog when <see cref="DownloadAllOwnedGames"/> is set).
    /// </summary>
    public List<string>? Products { get; set; }
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
