#nullable enable

namespace BattleNetPrefill.Api;

/// <summary>
/// Interface for receiving progress updates during prefill operations.
/// </summary>
public interface IPrefillProgress
{
    void OnLog(LogLevel level, string message);
    void OnOperationStarted(string operationName);
    void OnOperationCompleted(string operationName, TimeSpan elapsed);
    void OnAppStarted(AppDownloadInfo app);
    void OnDownloadProgress(DownloadProgressInfo progress);
    void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result);
    void OnPrefillCompleted(PrefillSummary summary);
    void OnError(string message, Exception? exception = null);
}


public class NullProgress : IPrefillProgress
{
    public static readonly NullProgress Instance = new();

    public void OnLog(LogLevel level, string message) { }
    public void OnOperationStarted(string operationName) { }
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) { }
    public void OnAppStarted(AppDownloadInfo app) { }
    public void OnDownloadProgress(DownloadProgressInfo progress) { }
    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result) { }
    public void OnPrefillCompleted(PrefillSummary summary) { }
    public void OnError(string message, Exception? exception = null) { }
}
