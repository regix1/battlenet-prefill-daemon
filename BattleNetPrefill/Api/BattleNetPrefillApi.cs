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

    private readonly ConcurrentDictionary<string, byte> _activeRuns = new(StringComparer.Ordinal);
    private readonly ItemClaims _claims = new();
    private readonly PrefillSettings _settings;

    // Size estimates share their own result cache.
    private readonly SemaphoreSlim _sizePassLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Per-product cached download-size estimate, keyed by product code + the CDN/marker version it was
    /// computed for. Lets repeated status polls return the byte total without re-running the CDN metadata
    /// round-trip. Invalidated implicitly: a different version produces a different key, so a stale entry is
    /// simply never read.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _downloadSizeCache = new ConcurrentDictionary<string, long>();

    public bool IsPrefilling => !_activeRuns.IsEmpty;

    public BattleNetPrefillApi(IPrefillProgress? progress = null)
        : this(progress ?? NullProgress.Instance, AppConfig.Capture())
    {
    }

    internal BattleNetPrefillApi(IPrefillProgress progress, PrefillSettings settings)
    {
        _progress = progress ?? NullProgress.Instance;
        _console = new ApiConsoleAdapter(_progress);
        _settings = settings;
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

        if (_selectedAppsCache != null)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache.ToList();
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
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var selectedAppIds = GetSelectedApps();
        if (selectedAppIds.Count == 0)
        {
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            };
        }

        var apps = new List<AppStatus>();
        long totalDownloadSize = 0;

        foreach (var appId in selectedAppIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var product = ResolveProduct(appId);
            var isUpToDate = HasPrefillMarker(appId);
            long downloadSize = 0;

            // Only run the (network-touching) size pass for products that still need downloading.
            if (product != null && !isUpToDate)
            {
                downloadSize = await GetCachedDownloadSizeAsync(product, cancellationToken);
            }

            totalDownloadSize += downloadSize;
            apps.Add(new AppStatus
            {
                AppId = appId,
                Name = product?.DisplayName ?? appId,
                DownloadSize = downloadSize,
                IsUpToDate = isUpToDate
            });
        }

        return new SelectedAppsStatus
        {
            Apps = apps,
            TotalDownloadSize = totalDownloadSize
        };
    }

    // Size-only handlers capture their own options and cannot disable another run.
    private async Task<long> GetCachedDownloadSizeAsync(TactProduct product, CancellationToken cancellationToken)
    {
        var cacheKey = BuildSizeCacheKey(product.ProductCode);
        if (_downloadSizeCache.TryGetValue(cacheKey, out var cachedSize))
        {
            return cachedSize;
        }

        await _sizePassLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring: another poll may have computed it, or a prefill may have started
            // while we were waiting on the semaphore.
            if (_downloadSizeCache.TryGetValue(cacheKey, out cachedSize))
            {
                return cachedSize;
            }

            try
            {
                var handler = new TactProductHandler(_console, false, NullProgress.Instance,
                    _settings with { OperationId = Guid.NewGuid().ToString("D"), SkipDownloads = true });
                var downloadSize = await handler.GetProductDownloadSizeAsync(product, cancellationToken);
                _downloadSizeCache[cacheKey] = downloadSize;
                return downloadSize;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Surface the lancache target so a wrong-IP / lancache-misconfig is obvious from the
                // daemon log. The build-config read (and thus the size estimate) is routed through
                // LANCACHE_IP; if that points at something that is not a lancache cache (e.g. the
                // lancache-manager app), this fails with "Error reading build config!".
                var lancacheIp = Environment.GetEnvironmentVariable("LANCACHE_IP");
                var lancacheInfo = string.IsNullOrWhiteSpace(lancacheIp)
                    ? "LANCACHE_IP not set (using DNS auto-detect)"
                    : $"LANCACHE_IP={lancacheIp}";
                var inner = ex.InnerException != null ? $" | Inner: {ex.InnerException.Message}" : string.Empty;
                _progress.OnLog(LogLevel.Warning,
                    $"Failed to get size for {product.DisplayName} [{lancacheInfo}]: {ex.Message}{inner}");
                return 0;
            }
        }
        finally
        {
            _sizePassLock.Release();
        }
    }

    /// <summary>
    /// Builds the size-cache key from the product code and its current prefill-marker version. When a prefill
    /// updates the product, the marker version changes, so the next poll computes a fresh key and the stale
    /// entry is naturally never read again.
    /// </summary>
    private string BuildSizeCacheKey(string productCode)
    {
        var version = ReadPrefillMarker(productCode) ?? "none";
        return $"{productCode}@{version}";
    }

    /// <summary>
    /// Runs the prefill operation, emitting structured progress events per product.
    /// </summary>
    public Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PrefillOptions();
        var ids = options.Products is { Count: > 0 }
            ? options.Products.ToArray()
            : options.DownloadAllOwnedGames
                ? TactProduct.AllEnumValues.Select(product => product.ProductCode).ToArray()
                : GetSelectedApps().ToArray();
        return PrefillAsync(ids, options.Force, _progress,
            _settings with { OperationId = Guid.NewGuid().ToString("D") }, null, cancellationToken);
    }

    internal Task<PrefillResult> PrefillAsync(PrefillRun run, CancellationToken cancellationToken)
    {
        var ids = run.Options.AppIds
            ?? TactProduct.AllEnumValues.Select(product => product.ProductCode).ToArray();
        if (!run.Progress.Snapshot.SelectionResolved)
        {
            run.Progress.ResolveSelection(ids);
        }
        return PrefillAsync(ids, run.Options.Force, run,
            _settings with
            {
                OperationId = run.Progress.Snapshot.OperationId,
                MaxConcurrency = run.Options.MaxConcurrency,
                Run = run
            }, run, cancellationToken);
    }

    private async Task<PrefillResult> PrefillAsync(IReadOnlyList<string> ids, bool force,
        IPrefillProgress progress, PrefillSettings settings, PrefillRun? run,
        CancellationToken cancellationToken)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();
        var products = ids.Select(id => ResolveProduct(id)
            ?? throw new ArgumentException("Unknown Battle.net product.", nameof(ids))).Distinct().ToArray();
        if (products.Length == 0)
        {
            return new PrefillResult { Success = false, ErrorMessage = "No apps selected for prefill" };
        }

        _activeRuns.TryAdd(settings.OperationId, 0);
        var timer = Stopwatch.StartNew();
        var handler = new TactProductHandler(new ApiConsoleAdapter(progress), force, progress, settings);
        var updated = 0;
        var cached = 0;
        var failed = 0;
        try
        {
            foreach (var product in products)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var app = new AppDownloadInfo { AppId = product.ProductCode, Name = product.DisplayName };
                var claim = _claims.TryClaim(settings.OperationId, new[] { product.ProductCode });
                progress.OnAppStarted(app);
                if (claim == null)
                {
                    progress.OnAppCompleted(app, AppDownloadResult.Skipped);
                    continue;
                }
                run?.Hold(claim);
                try
                {
                    var failuresBefore = handler.Summary.FailedApps;
                    var cachedBefore = handler.Summary.AlreadyUpToDate;
                    await handler.ProcessProductAsync(product, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handler.Summary.FailedApps != failuresBefore)
                    {
                        failed++;
                        progress.OnAppCompleted(app, AppDownloadResult.Failed);
                    }
                    else if (handler.Summary.AlreadyUpToDate != cachedBefore)
                    {
                        cached++;
                        progress.OnAppCompleted(app, AppDownloadResult.AlreadyUpToDate);
                    }
                    else if (!settings.SkipDownloads)
                    {
                        updated++;
                        if (run == null) { progress.OnAppCompleted(app, AppDownloadResult.Success); }
                        foreach (var key in _downloadSizeCache.Keys.Where(key =>
                            key.StartsWith(product.ProductCode + "@", StringComparison.Ordinal)))
                        {
                            _downloadSizeCache.TryRemove(key, out _);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                    || run?.Progress.Terminal?.State == "cancelled")
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    progress.OnLog(LogLevel.Warning, $"Prefill failed for {product.DisplayName}: {exception.Message}");
                    progress.OnAppCompleted(app, AppDownloadResult.Failed);
                }
                finally
                {
                    if (run == null) { claim.Dispose(); }
                }
            }
            progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = products.Length,
                UpdatedApps = updated,
                AlreadyUpToDate = cached,
                FailedApps = failed,
                TotalBytesTransferred = (long)handler.Summary.TotalBytesTransferred.Bytes,
                TotalTime = timer.Elapsed
            });
            return new PrefillResult
            {
                Success = failed == 0,
                ErrorMessage = failed == 0 ? null : $"{failed} product(s) failed to prefill",
                TotalTime = timer.Elapsed
            };
        }
        finally
        {
            _activeRuns.TryRemove(settings.OperationId, out _);
        }
    }

    internal static TactProduct? ResolveProduct(string appId)
    {
        return TactProduct.AllEnumValues
            .FirstOrDefault(p => string.Equals(p.ProductCode, appId, StringComparison.OrdinalIgnoreCase));
    }

    private string PrefillMarkerPath(string productCode)
        => Path.Combine(_settings.CacheDirectory, $"prefilledVersion-{productCode}.txt");

    private bool HasPrefillMarker(string productCode)
        => File.Exists(PrefillMarkerPath(productCode));

    private string? ReadPrefillMarker(string productCode)
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
        _sizePassLock.Dispose();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("BattleNetPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
