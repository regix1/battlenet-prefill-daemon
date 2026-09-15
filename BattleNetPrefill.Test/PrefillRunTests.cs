using BattleNetPrefill.Api;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class PrefillRunTests
{
    [Theory]
    [InlineData(true, "targeted")]
    [InlineData(false, "targeted")]
    [InlineData(true, "unkeyed")]
    [InlineData(false, "unkeyed")]
    [InlineData(true, "all")]
    [InlineData(false, "all")]
    [InlineData(true, "process")]
    [InlineData(false, "process")]
    public async Task MarkerCommitAndCancellationHaveOneWinningOrder(bool cancelFirst, string mode)
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        fixture.Body("d3").Release.TrySetResult();
        var path = Path.GetFullPath(Path.Combine(fixture.Directory, "prefilledVersion-d3.txt"));
        await File.WriteAllTextAsync(path, "previous-version");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var settings = fixture.Settings with
        {
            BeforeCommit = cancelFirst ? async _ =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            : null,
            CommitFile = (temporary, target) =>
            {
                if (Path.GetFullPath(target) == path)
                {
                    entered.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    File.Move(temporary, target, overwrite: true);
                    Interlocked.Increment(ref writes);
                }
                else { File.Move(temporary, target, overwrite: true); }
            }
        };
        using var api = new BattleNetPrefillApi(NullProgress.Instance, settings);
        await api.InitializeAsync();
        using var lifetime = new CancellationTokenSource();
        await using var owner = new OwnedOperationCoordinator();
        var run = CreateRun(fixture, "d3");
        var id = run.Progress.Snapshot.OperationId;
        await owner.StartAsync(id, PrefillProtocol.Fingerprint(run.Options), run.Progress, async token =>
        {
            using var observer = token.Register(() => cancellationObserved.TrySetResult());
            await run.ExecuteAsync(api, token);
        }, lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancel = Task.Run(async () =>
            {
                cancellationStarted.TrySetResult();
                switch (mode)
                {
                    case "targeted":
                        owner.Cancel(id, fixture.Protocol.DaemonInstanceId);
                        break;
                    case "unkeyed":
                        await owner.CancelAndWaitAsync();
                        break;
                    case "all":
                        await owner.CancelAllAndWaitAsync();
                        break;
                    case "process":
                        await lifetime.CancelAsync();
                        break;
                    default:
                        throw new InvalidOperationException("Unknown cancellation path.");
                }
            });
            await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelFirst) { await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            release.TrySetResult();
            await Task.WhenAll(cancel, owner.WaitAsync(id)).WaitAsync(TimeSpan.FromSeconds(10));
            var item = Assert.Single(run.Progress.GetPage(0, 100).Items);
            Assert.Equal(cancelFirst ? 0 : 1, writes);
            Assert.Equal(cancelFirst ? "cancelled" : "success", item.Result);
            Assert.Equal(cancelFirst ? null : "fixture-version", item.CacheRevision);
            Assert.Equal(cancelFirst ? "previous-version" : "fixture-version", await File.ReadAllTextAsync(path));
            Assert.Equal(8, item.BytesTransferred);
            Assert.Equal(cancelFirst ? 0 : 1, run.Progress.Snapshot.CompletedApps);
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task OverlappingProductIsSkippedAndDifferentProductContinues()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var api = new BattleNetPrefillApi(NullProgress.Instance, fixture.Settings);
        await api.InitializeAsync();
        var first = CreateRun(fixture, "d3");
        var firstTask = first.ExecuteAsync(api, CancellationToken.None);
        await fixture.Body("d3").Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var skipped = CreateRun(fixture, "d3");
        await skipped.ExecuteAsync(api, CancellationToken.None);
        Assert.Equal("completed", skipped.Progress.Snapshot.State);
        Assert.Equal("skippedOverlap", skipped.Progress.Snapshot.Reason);
        Assert.Equal(0, skipped.Progress.Snapshot.BytesTransferred);
        var second = CreateRun(fixture, "d3", "s1");
        var secondTask = second.ExecuteAsync(api, CancellationToken.None);
        await fixture.Body("s1").Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Body("s1").Release.TrySetResult();
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        var items = second.Progress.GetPage(0, 100).Items;
        Assert.Equal("skipped", items[0].Result);
        Assert.Equal("skippedOverlap", items[0].Reason);
        Assert.Equal("success", items[1].Result);
        Assert.Equal(1, second.Progress.Snapshot.SkippedApps);
        Assert.Equal(1, second.Progress.Snapshot.CompletedApps);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "prefilledVersion-d3.txt")));
        fixture.Body("d3").Release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExplicitSelectionIsCopiedAndEmptySelectionDoesNotUseSavedDefaults()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        var ids = new List<string> { "d3" };
        var captured = fixture.Protocol.Capture(new RunOptions { AppIds = ids, MaxConcurrency = 1, Force = true });
        ids[0] = "s1";
        Assert.Equal("d3", Assert.Single(captured.AppIds!));
        Assert.Throws<ArgumentException>(() => fixture.Protocol.Capture(new RunOptions { AppIds = Array.Empty<string>(), MaxConcurrency = 1 }));
        await using var commands = new SocketCommandInterface(0, fixture.Protocol, fixture.Settings);
        await commands.StartAsync();
        await using var client = await SocketAdapterTests.FramedClient.ConnectAsync(commands.BoundTcpPort);
        var request = fixture.Start(Guid.NewGuid().ToString("D"), "d3");
        request.Parameters!["appIds"] = "[]";
        await client.SendAsync(request);
        Assert.False((await ConcurrentPrefillTests.ReadResponseAsync(client, request.Id)).GetProperty("success").GetBoolean());
        Assert.Equal(0, fixture.ContentRequests);
        await commands.StopAsync();
    }

    [Fact]
    public async Task MissingRevisionUsesStoredMarkerForCacheStatus()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, "prefilledVersion-d3.txt"), "fixture-version");
        using var api = new BattleNetPrefillApi(NullProgress.Instance, fixture.Settings);
        await api.InitializeAsync();

        var status = await api.CheckCacheStatusAsync([
            new CachedAppInput { AppId = "d3" },
            new CachedAppInput { AppId = "fenris" }
        ]);

        var app = Assert.Single(status.Apps);
        Assert.Equal("d3", app.AppId);
        Assert.True(app.IsUpToDate);
    }

    [Fact]
    public async Task MissingManagerCacheRecordForcesDownloadDespiteLocalMarker()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        fixture.Body("d3").Release.TrySetResult();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, "prefilledVersion-d3.txt"), "fixture-version");
        using var api = new BattleNetPrefillApi(NullProgress.Instance, fixture.Settings);
        await api.InitializeAsync();
        var first = new PrefillRun(Guid.NewGuid().ToString("D"), fixture.Protocol,
            fixture.Protocol.Capture(new RunOptions
            {
                AppIds = ["d3"],
                CachedApps = [],
                MaxConcurrency = 1
            }), NullProgress.Instance);
        await first.ExecuteAsync(api, CancellationToken.None);
        Assert.Equal("success", Assert.Single(first.Progress.GetPage(0, 10).Items).Result);
        var requestsAfterDownload = fixture.ContentRequests;
        Assert.True(requestsAfterDownload > 0);

        var second = new PrefillRun(Guid.NewGuid().ToString("D"), fixture.Protocol,
            fixture.Protocol.Capture(new RunOptions
            {
                AppIds = ["d3"],
                CachedApps = [new CachedAppInput { AppId = "d3", Revision = "fixture-version" }],
                MaxConcurrency = 1
            }), NullProgress.Instance);
        await second.ExecuteAsync(api, CancellationToken.None);
        Assert.Equal("already_cached", Assert.Single(second.Progress.GetPage(0, 10).Items).Result);
        Assert.Equal(requestsAfterDownload, fixture.ContentRequests);
    }

    internal static PrefillRun CreateRun(ConcurrentPrefillTests.TactFixture fixture, params string[] products)
        => new(Guid.NewGuid().ToString("D"), fixture.Protocol,
            fixture.Protocol.Capture(new RunOptions { AppIds = products, MaxConcurrency = 1, Force = true }), NullProgress.Instance);
}
