using System.Net;
using System.Text;
using BattleNetPrefill.Api;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Structs.Enums;
using BattleNetPrefill.Web;

namespace BattleNetPrefill.Test;

public sealed class CacheCommitTests
{
    [Fact]
    public async Task FailedMarkerReplacementKeepsPreviousVersionAndFailsRun()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        var path = Path.Combine(fixture.Directory, "prefilledVersion-d3.txt");
        await File.WriteAllTextAsync(path, "previous-version");
        var settings = fixture.Settings with
        {
            CommitFile = (temporary, target) =>
            {
                if (Path.GetFullPath(target) == Path.GetFullPath(path)) { throw new IOException("Injected replacement failure."); }
                File.Move(temporary, target, overwrite: true);
            }
        };
        fixture.Body("d3").Release.TrySetResult();
        using var api = new BattleNetPrefillApi(NullProgress.Instance, settings);
        await api.InitializeAsync();
        var run = PrefillRunTests.CreateRun(fixture, "d3");
        await run.ExecuteAsync(api, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", run.Progress.Snapshot.State);
        Assert.Equal(1, run.Progress.Snapshot.FailedApps);
        Assert.Equal(0, run.Progress.Snapshot.CompletedApps);
        Assert.Equal("failed", Assert.Single(run.Progress.GetPage(0, 100).Items).Result);
        Assert.Equal("previous-version", await File.ReadAllTextAsync(path));
        Assert.Equal(8, run.Progress.Snapshot.BytesTransferred);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SameUrlReadersShareOneCompleteCacheTransaction()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var settings = fixture.Settings with
        {
            CreateClient = () => new HttpClient(new Reply(async token =>
            {
                Interlocked.Increment(ref requests);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("complete-cache")) };
            }))
        };
        using var first = new CdnRequestManager(new ApiConsoleAdapter(NullProgress.Instance), settings, NullProgress.Instance);
        using var second = new CdnRequestManager(new ApiConsoleAdapter(NullProgress.Instance),
            settings with { OperationId = Guid.NewGuid().ToString("D") }, NullProgress.Instance);
        var hash = new MD5Hash(1, 2);
        var a = first.GetRequestAsBytesAsync(RootFolder.config, hash);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = second.GetRequestAsBytesAsync(RootFolder.config, hash);
        Assert.Equal(1, Volatile.Read(ref requests));
        release.TrySetResult();
        Assert.Equal(Encoding.UTF8.GetBytes("complete-cache"), await a.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(await a, await b.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task FailedCacheReplacePreservesOldBytesAndDisjointCommitsRemain()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        var a = Path.Combine(fixture.Directory, "a");
        var b = Path.Combine(fixture.Directory, "b");
        await File.WriteAllTextAsync(a, "old");
        await Assert.ThrowsAsync<IOException>(() => CdnRequestManager.CommitAsync(a, Encoding.UTF8.GetBytes("new"),
            (_, _) => throw new IOException("Injected replacement failure."), CancellationToken.None));
        Assert.Equal("old", await File.ReadAllTextAsync(a));
        await Task.WhenAll(
            CdnRequestManager.CommitAsync(a, Encoding.UTF8.GetBytes("first"), null, CancellationToken.None),
            CdnRequestManager.CommitAsync(b, Encoding.UTF8.GetBytes("second"), null, CancellationToken.None));
        Assert.Equal("first", await File.ReadAllTextAsync(a));
        Assert.Equal("second", await File.ReadAllTextAsync(b));
    }

    private sealed class Reply(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(cancellationToken);
    }
}
