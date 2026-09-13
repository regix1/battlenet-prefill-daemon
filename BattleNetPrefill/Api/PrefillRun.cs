#nullable enable

namespace BattleNetPrefill.Api;

internal sealed class PrefillRun : IPrefillProgress
{
    private readonly IPrefillProgress _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, RunItemSnapshot> _items = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _claims = new();

    internal PrefillRun(string operationId, PrefillProtocol protocol, RunOptions options,
        IPrefillProgress log, Func<RunSnapshot, CancellationToken, Task>? publish = null)
    {
        Options = options;
        _log = log;
        Progress = new RunProgress(operationId, protocol.DaemonInstanceId, options, publish);
    }

    internal RunOptions Options { get; }
    internal RunProgress Progress { get; }

    internal void Hold(IDisposable claim) => _claims.Add(claim);

    internal void Commit(AppDownloadInfo app, Action commit, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var item = _items[app.AppId] with { State = "completed", Result = "success" };
            if (!Progress.TryCommitItem(item, () =>
            {
                commit();
                _items[app.AppId] = item;
            }))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    internal async Task ExecuteAsync(BattleNetPrefillApi api, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(() => Progress.TryChooseTerminal("cancelled"));
        try
        {
            var result = await api.PrefillAsync(this, cancellationToken);
            Progress.TryChooseTerminal(result.Success ? "completed" : "failed", result.Success ? null : "download-failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Progress.Terminal?.State == "cancelled")
        {
            Progress.TryChooseTerminal("cancelled");
            throw;
        }
        catch (Exception)
        {
            Progress.TryChooseTerminal("failed", "download-failed");
            throw;
        }
        finally
        {
            Dictionary<string, long> bytes;
            lock (_sync)
            {
                bytes = _items.ToDictionary(item => item.Key, item => item.Value.BytesTransferred, StringComparer.Ordinal);
            }
            try
            {
                await Progress.CompleteAsync(bytes.Values.Sum(), bytes);
            }
            finally
            {
                foreach (var claim in _claims) { claim.Dispose(); }
            }
        }
    }

    public void OnLog(LogLevel level, string message) => _log.OnLog(level, message);
    public void OnOperationStarted(string operationName) => _log.OnOperationStarted(operationName);
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) => _log.OnOperationCompleted(operationName, elapsed);

    public void OnAppStarted(AppDownloadInfo app)
    {
        lock (_sync)
        {
            var item = new RunItemSnapshot { AppId = app.AppId, Name = app.Name, State = "preparing", TotalBytes = app.TotalBytes };
            _items[app.AppId] = item;
            Progress.UpdateItem(item);
        }
    }

    public void OnDownloadProgress(DownloadProgressInfo progress)
    {
        lock (_sync)
        {
            var item = _items[progress.AppId] with
            {
                State = progress.State,
                TotalBytes = progress.TotalBytes,
                BytesTransferred = Math.Max(_items[progress.AppId].BytesTransferred, progress.BytesDownloaded)
            };
            _items[progress.AppId] = item;
            Progress.UpdateItem(item);
        }
    }

    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
    {
        lock (_sync)
        {
            var outcome = result switch
            {
                AppDownloadResult.Success => "success",
                AppDownloadResult.AlreadyUpToDate => "already_cached",
                AppDownloadResult.Failed => "failed",
                AppDownloadResult.Skipped => "skipped",
                _ => "skipped"
            };
            var item = _items[app.AppId] with
            {
                State = outcome == "success" ? "completed" : outcome,
                Result = outcome,
                Reason = result == AppDownloadResult.Skipped ? "skippedOverlap" : null
            };
            _items[app.AppId] = item;
            Progress.UpdateItem(item);
        }
    }

    public void OnPrefillCompleted(PrefillSummary summary) { }
    public void OnError(string message, Exception? exception = null)
    {
        _log.OnLog(LogLevel.Error, message);
        Progress.TryChooseTerminal("failed", "download-failed");
    }
}
