#nullable enable

using System.Text.Json;

namespace BattleNetPrefill.Api;

/// <summary>
/// Command interface that uses Unix Domain Socket or TCP for IPC.
/// Handles all socket commands for the Battle.net prefill daemon.
///
/// Battle.net is anonymous (no account login). The daemon reports itself as ready/logged-in
/// immediately on connect; there is no login/logout/credential flow. The HMAC socket handshake
/// (PREFILL_SOCKET_SECRET) is handled by <see cref="SocketServer"/> and is unrelated to any
/// Battle.net account.
/// </summary>
public sealed class SocketCommandInterface : IDisposable
{
    private readonly SocketServer _socketServer;
    private readonly SocketProgress _progress;
    private readonly CancellationTokenSource _cts = new();
    private readonly OwnedOperationCoordinator _prefillOperation = new();
    private readonly BattleNetPrefillApi _api;
    private readonly Func<PrefillOptions, CancellationToken, Task<PrefillResult>> _prefillAsync;
    private bool _disposed;

    public SocketCommandInterface(string socketPath)
    {
        _progress = new SocketProgress();
        _socketServer = new SocketServer(socketPath, _progress);
        _api = new BattleNetPrefillApi(_progress);
        _prefillAsync = _api.PrefillAsync;
        _socketServer.OnCommand = HandleCommandAsync;

        _progress.SocketServer = _socketServer;
    }

    public SocketCommandInterface(int tcpPort)
    {
        _progress = new SocketProgress();
        _socketServer = new SocketServer(tcpPort, _progress);
        _api = new BattleNetPrefillApi(_progress);
        _prefillAsync = _api.PrefillAsync;
        _socketServer.OnCommand = HandleCommandAsync;

        _progress.SocketServer = _socketServer;
    }

    internal SocketCommandInterface(
        int tcpPort,
        Func<PrefillOptions, CancellationToken, Task<PrefillResult>> prefillAsync)
        : this(tcpPort)
    {
        _prefillAsync = prefillAsync ?? throw new ArgumentNullException(nameof(prefillAsync));
    }

    internal int BoundTcpPort => _socketServer.BoundTcpPort;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _progress.OnLog(LogLevel.Info, "Starting socket command interface...");

        await _socketServer.StartAsync(cancellationToken);

        // Battle.net is anonymous - the daemon is ready immediately, no login required.
        await _api.InitializeAsync(cancellationToken);
        await BroadcastStatusAsync("logged-in", "Battle.net prefill ready (anonymous - no login required)", _api.DisplayName);

        _progress.OnLog(LogLevel.Info, "Socket command interface started - ready for commands");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        await _prefillOperation.CancelAndWaitAsync();
        await _socketServer.StopAsync();
        _progress.OnLog(LogLevel.Info, "Socket command interface stopped");
    }

    private async Task<CommandResponse> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        _progress.OnLog(LogLevel.Info, $"Processing command: {request.Type} (ID: {request.Id})");

        try
        {
            return request.Type.ToLowerInvariant() switch
            {
                "cancel-prefill" => await HandleCancelPrefillAsync(request, cancellationToken),
                "status" => HandleStatus(request),
                "get-owned-games" => await HandleGetOwnedGamesAsync(request, cancellationToken),
                "get-selected-apps" => HandleGetSelectedApps(request),
                "set-selected-apps" => HandleSetSelectedApps(request),
                "get-selected-apps-status" => await HandleGetSelectedAppsStatusAsync(request, cancellationToken),
                "prefill" => await HandlePrefillAsync(request, cancellationToken),
                "clear-cache" => HandleClearCache(request),
                "get-cache-info" => HandleGetCacheInfo(request),
                "check-cache-status" => await HandleCheckCacheStatusAsync(request, cancellationToken),
                "shutdown" => HandleShutdown(request),
                _ => new CommandResponse
                {
                    Id = request.Id,
                    Success = false,
                    Error = $"Unknown command type: {request.Type}",
                    CompletedAt = DateTime.UtcNow
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Error, $"Error handling command {request.Type}: {ex.Message}");
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = ex.Message,
                CompletedAt = DateTime.UtcNow
            };
        }
    }

    private async Task<CommandResponse> HandleCancelPrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!_prefillOperation.IsRunning)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = true, Message = "No prefill in progress", CompletedAt = DateTime.UtcNow
            };
        }

        _progress.OnLog(LogLevel.Info, "Cancelling prefill...");
        var result = await _prefillOperation.CancelAndWaitAsync(cancellationToken);
        if (result.Status == OwnedOperationStatus.Failed)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = result.Exception?.Message ?? "Prefill failed while cancellation was in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        if (_prefillOperation.IsRunning || _api.IsPrefilling)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Prefill did not stop cleanly",
                CompletedAt = DateTime.UtcNow
            };
        }

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = result.Status == OwnedOperationStatus.Idle ? "No prefill in progress" : "Prefill cancelled",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleStatus(CommandRequest request)
    {
        // Anonymous - always ready.
        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = new StatusData
            {
                IsLoggedIn = true,
                IsInitialized = _api.IsInitialized
            },
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetOwnedGamesAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var games = await _api.GetOwnedGamesAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id, Success = true, Data = games, CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetSelectedApps(CommandRequest request)
    {
        var selected = _api.GetSelectedApps();

        return new CommandResponse
        {
            Id = request.Id, Success = true, Data = selected, CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleSetSelectedApps(CommandRequest request)
    {
        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (string.IsNullOrEmpty(appIdsJson))
        {
            return new CommandResponse
            {
                Id = request.Id, Success = false, Error = "appIds parameter required", CompletedAt = DateTime.UtcNow
            };
        }

        var appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        if (appIds == null)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = false, Error = "appIds must be a JSON array", CompletedAt = DateTime.UtcNow
            };
        }

        // An empty array is a valid request: it clears the current selection.
        _api.SetSelectedApps(appIds);
        _progress.OnLog(LogLevel.Info, $"Set {appIds.Count} selected apps");

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Apps selected", CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetSelectedAppsStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var status = await _api.GetSelectedAppsStatusAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandlePrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (_prefillOperation.IsRunning || _api.IsPrefilling)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = false, Error = "A prefill is already in progress", CompletedAt = DateTime.UtcNow
            };
        }

        var options = new PrefillOptions();

        if (request.Parameters != null)
        {
            if (bool.TryParse(request.Parameters.GetValueOrDefault("all"), out var all))
                options.DownloadAllOwnedGames = all;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("force"), out var force))
                options.Force = force;

            var productsJson = request.Parameters.GetValueOrDefault("products");
            if (!string.IsNullOrEmpty(productsJson))
            {
                options.Products = JsonSerializer.Deserialize(productsJson, DaemonSerializationContext.Default.ListString);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _prefillOperation.StartAsync(async operationToken =>
            {
                try
                {
                    var result = await _prefillAsync(options, operationToken);

                    if (result.Success)
                        _progress.OnLog(LogLevel.Info, "Prefill completed successfully");
                    else
                        _progress.OnLog(LogLevel.Warning, $"Prefill completed with errors: {result.ErrorMessage}");
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    _progress.OnPrefillCancelled("Prefill cancelled by user");
                }
                catch (Exception ex)
                {
                    _progress.OnError($"Prefill failed: {ex.Message}", ex);
                    throw;
                }
            }, _cts.Token);
        }
        catch (InvalidOperationException)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "A prefill is already in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Prefill started", CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleClearCache(CommandRequest request)
    {
        var result = BattleNetPrefillApi.ClearCache();

        return new CommandResponse
        {
            Id = request.Id, Success = result.Success, Data = result, Message = result.Message, CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetCacheInfo(CommandRequest request)
    {
        var info = BattleNetPrefillApi.GetCacheInfo();

        return new CommandResponse
        {
            Id = request.Id, Success = info.Success, Data = info, Message = info.Message, CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCheckCacheStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        List<string> appIds;
        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (!string.IsNullOrEmpty(appIdsJson))
        {
            appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString) ?? new List<string>();
        }
        else
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Data = new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No app IDs provided" },
                Message = "No app IDs provided",
                CompletedAt = DateTime.UtcNow
            };
        }

        var status = await _api.CheckCacheStatusAsync(appIds, cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleShutdown(CommandRequest request)
    {
        _api.Shutdown();

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Shutdown complete", CompletedAt = DateTime.UtcNow
        };
    }

    private async Task BroadcastStatusAsync(string status, string message, string? displayName = null)
    {
        var statusEvent = new AuthStateEvent(status, message, displayName);
        await _socketServer.BroadcastAuthStateAsync(statusEvent);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cts.Cancel();
        _prefillOperation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cts.Dispose();
        _api.Dispose();
        _socketServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Progress implementation that broadcasts updates via socket.
    /// </summary>
    internal sealed class SocketProgress : IPrefillProgress
    {
        private readonly DaemonLogSink _logSink;
        public SocketServer? SocketServer { get; set; }
        private DateTime _lastProgressBroadcast = DateTime.MinValue;
        private static readonly TimeSpan BroadcastThrottle = TimeSpan.FromMilliseconds(250);

        internal SocketProgress(
            Action<string>? writeLine = null,
            DaemonLogLevel? minimumLevel = null)
        {
            _logSink = new DaemonLogSink(
                writeLine ?? (static message => Console.WriteLine(message)),
                minimumLevel ?? (AppConfig.VerboseLogs ? DaemonLogLevel.Debug : DaemonLogLevel.Info));
        }

        public void OnLog(LogLevel level, string message)
        {
            var daemonLevel = level switch
            {
                LogLevel.Debug => DaemonLogLevel.Debug,
                LogLevel.Info => DaemonLogLevel.Info,
                LogLevel.Warning => DaemonLogLevel.Warning,
                LogLevel.Error => DaemonLogLevel.Error,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
            var prefix = level == LogLevel.Warning ? "[WARN]" : $"[{level.ToString().ToUpperInvariant()}]";
            _logSink.Write(daemonLevel, $"{DateTime.UtcNow:HH:mm:ss} {prefix} {message}");
        }

        public void OnOperationStarted(string operationName)
            => OnLog(LogLevel.Info, $"Starting: {operationName}");

        public void OnOperationCompleted(string operationName, TimeSpan elapsed)
            => OnLog(LogLevel.Info, $"Completed: {operationName} ({elapsed.TotalSeconds:F2}s)");

        public void OnAppStarted(AppDownloadInfo app)
        {
            OnLog(LogLevel.Info, $"Downloading: {app.Name} ({app.AppId})");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "downloading",
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = 0,
                PercentComplete = 0,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnDownloadProgress(DownloadProgressInfo progress)
        {
            var now = DateTime.UtcNow;

            // "preparing" is a single, low-frequency state-transition emit (one per product, before the
            // transfer loop) carrying the up-front total — never throttle it away, or the UI would miss the
            // "0 B / <total>" hand-off. Only the high-frequency "downloading" byte stream is throttled.
            var isDownloading = progress.State == "downloading";
            if (isDownloading)
            {
                if (now - _lastProgressBroadcast < BroadcastThrottle)
                    return;
                _lastProgressBroadcast = now;
            }

            var downloadedStr = FormatBytes(progress.BytesDownloaded);
            var totalStr = FormatBytes(progress.TotalBytes);
            var speedStr = FormatBytes((long)progress.BytesPerSecond) + "/s";
            OnLog(LogLevel.Info, $"{progress.AppName}: {progress.State} - {progress.PercentComplete:F1}% - {speedStr} - {downloadedStr} / {totalStr}");

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = progress.State,
                CurrentAppId = progress.AppId,
                CurrentAppName = progress.AppName,
                TotalBytes = progress.TotalBytes,
                BytesDownloaded = progress.BytesDownloaded,
                PercentComplete = progress.PercentComplete,
                BytesPerSecond = progress.BytesPerSecond,
                Elapsed = progress.Elapsed,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:F2} {sizes[order]}";
        }

        public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
        {
            OnLog(LogLevel.Info, $"Completed: {app.Name} - {result}");
            var bytesDownloaded = result == AppDownloadResult.Success ? app.TotalBytes : 0;
            var state = result == AppDownloadResult.AlreadyUpToDate ? "already_cached" : "app_completed";

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = state,
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = bytesDownloaded,
                Result = result.ToString(),
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnPrefillCompleted(PrefillSummary summary)
        {
            OnLog(LogLevel.Info, $"Prefill complete: {summary.UpdatedApps} updated, {summary.AlreadyUpToDate} up-to-date, {summary.FailedApps} failed");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "completed",
                TotalApps = summary.TotalApps,
                UpdatedApps = summary.UpdatedApps,
                AlreadyUpToDate = summary.AlreadyUpToDate,
                FailedApps = summary.FailedApps,
                TotalBytesTransferred = summary.TotalBytesTransferred,
                TotalTime = summary.TotalTime,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnPrefillCancelled(string message)
        {
            OnLog(LogLevel.Info, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "cancelled",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnError(string message, Exception? exception = null)
        {
            OnLog(LogLevel.Error, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "error",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private void BroadcastProgress(PrefillProgressUpdate update)
        {
            if (SocketServer == null) return;

            var progressEvent = new ProgressEvent(update);
            _ = SocketServer.BroadcastProgressAsync(progressEvent);
        }
    }
}
