using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BattleNetPrefill.Api;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class CacheStatusTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyAndV2UseCompatibleWireShapes()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp();
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();
        var cachedApps = new List<CachedAppInput>
        {
            new() { AppId = "d3", Revision = "fixture-version" }
        };

        var legacy = await api.CheckCacheStatusAsync(cachedApps);
        var v2 = await api.CheckCacheStatusAsync(
            cachedApps,
            CancellationToken.None,
            Start.AddMinutes(1),
            2);

        using var legacyJson = JsonDocument.Parse(JsonSerializer.Serialize(
            legacy,
            DaemonSerializationContext.Default.CacheStatusResult));
        var legacyRoot = legacyJson.RootElement;
        var legacyApp = legacyRoot.GetProperty("apps")[0];
        Assert.Equal("Checked 1 apps", legacy.Message);
        Assert.Equal("Checked 1 apps", legacyRoot.GetProperty("message").GetString());
        Assert.False(legacyRoot.TryGetProperty("version", out _));
        Assert.False(legacyApp.TryGetProperty("outcome", out _));
        Assert.False(legacyApp.TryGetProperty("reason", out _));
        Assert.True(legacyApp.GetProperty("isUpToDate").GetBoolean());

        using var v2Json = JsonDocument.Parse(JsonSerializer.Serialize(
            v2,
            DaemonSerializationContext.Default.CacheStatusResult));
        var v2Root = v2Json.RootElement;
        var v2App = v2Root.GetProperty("apps")[0];
        Assert.Null(v2.Message);
        Assert.False(v2Root.TryGetProperty("message", out _));
        Assert.Equal(2, v2Root.GetProperty("version").GetInt32());
        Assert.Equal("Current", v2App.GetProperty("outcome").GetString());
        Assert.False(v2App.TryGetProperty("reason", out _));
        Assert.True(v2App.GetProperty("isUpToDate").GetBoolean());

        var requestsBeforeExpiredLegacy = revisions.VersionsRequests;
        var expiredLegacy = await api.CheckCacheStatusAsync(
            cachedApps,
            CancellationToken.None,
            Start.AddSeconds(-1));
        Assert.Empty(expiredLegacy.Apps);
        Assert.Null(expiredLegacy.Version);
        Assert.Equal(requestsBeforeExpiredLegacy, revisions.VersionsRequests);
    }

    [Fact]
    public async Task ExpiredV2ReturnsOrderedDeadlineRowsWithoutRevisionRequests()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp();
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "d3", Revision = "fixture-version" },
                new CachedAppInput { AppId = "missing", Revision = "fixture-version" }
            ],
            CancellationToken.None,
            Start.AddSeconds(-1),
            2);

        Assert.Equal(2, result.Version);
        Assert.Null(result.Message);
        Assert.Equal(new[] { "d3", "missing" }, result.Apps.Select(app => app.AppId));
        Assert.All(result.Apps, app =>
        {
            Assert.False(app.IsUpToDate);
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.DeadlineReached, app.Reason);
        });
        Assert.Equal(0, revisions.VersionsRequests);
        Assert.Equal(0, revisions.CdnRequests);
    }

    [Fact]
    public async Task DeadlineKeepsCompletedRowsAndCancelsHeldVersionsBody()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp(new Dictionary<string, RevisionReply>
        {
            ["fenris"] = RevisionReply.Hold
        });
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();

        var pending = api.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "d3", Revision = "fixture-version" },
                new CachedAppInput { AppId = "fenris", Revision = "fixture-version" },
                new CachedAppInput { AppId = "s1", Revision = "fixture-version" }
            ],
            CancellationToken.None,
            Start.AddSeconds(10),
            2);
        await revisions.HeldEntered.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CacheOutcome.Current, result.Apps[0].Outcome);
        Assert.Equal(CacheReason.DeadlineReached, result.Apps[1].Reason);
        Assert.Equal(CacheReason.DeadlineReached, result.Apps[2].Reason);
        Assert.Equal(2, revisions.VersionsRequests);
        await revisions.HeldCancelled.WaitAsync(TimeSpan.FromSeconds(5));
        await revisions.HeldDisposed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationWinsDeadlineRace()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp(new Dictionary<string, RevisionReply>
        {
            ["d3"] = RevisionReply.Hold
        });
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();
        using var caller = new CancellationTokenSource();
        var pending = api.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "d3", Revision = "fixture-version" }],
            caller.Token,
            Start.AddSeconds(10),
            2);
        await revisions.HeldEntered.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(8));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await revisions.HeldCancelled.WaitAsync(TimeSpan.FromSeconds(5));
        await revisions.HeldDisposed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PerItemFailuresDoNotPreventLaterResults()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp(new Dictionary<string, RevisionReply>
        {
            ["d3"] = RevisionReply.Blank,
            ["s1"] = RevisionReply.Malformed
        });
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "missing", Revision = "fixture-version" },
                new CachedAppInput { AppId = "d3", Revision = "fixture-version" },
                new CachedAppInput { AppId = "fenris" },
                new CachedAppInput { AppId = "s1", Revision = "fixture-version" },
                new CachedAppInput { AppId = "s2", Revision = "fixture-version" }
            ],
            CancellationToken.None,
            Start.AddMinutes(1),
            2);

        Assert.Equal(CacheReason.MissingApp, result.Apps[0].Reason);
        Assert.Equal(CacheReason.ManifestUnavailable, result.Apps[1].Reason);
        Assert.Equal(CacheReason.NoCacheEvidence, result.Apps[2].Reason);
        Assert.Equal(CacheReason.InspectionFailed, result.Apps[3].Reason);
        Assert.Equal(CacheOutcome.Current, result.Apps[4].Outcome);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task RevisionOnlyStatusSkipsCdnInitialization()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp();
        var clock = new CacheStatusClock(Start);
        using var api = new BattleNetPrefillApi(
            NullProgress.Instance,
            WithRevisions(fixture, revisions),
            clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "d3", Revision = "fixture-version" }],
            CancellationToken.None,
            Start.AddMinutes(1),
            2);

        Assert.Equal(CacheOutcome.Current, Assert.Single(result.Apps).Outcome);
        Assert.Equal(1, revisions.VersionsRequests);
        Assert.Equal(0, revisions.CdnRequests);
    }

    [Fact]
    public async Task SocketReturnsPartialV2ResponseAndKeepsTransportOpen()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp(new Dictionary<string, RevisionReply>
        {
            ["fenris"] = RevisionReply.Hold
        });
        var clock = new CacheStatusClock(Start);
        await using var commands = new SocketCommandInterface(
            0,
            fixture.Protocol,
            WithRevisions(fixture, revisions),
            clock);
        await commands.StartAsync();
        await using var client = await SocketAdapterTests.FramedClient.ConnectAsync(commands.BoundTcpPort);
        await client.SendAsync(CheckRequest(
            "partial",
            [
                new CachedAppInput { AppId = "d3", Revision = "fixture-version" },
                new CachedAppInput { AppId = "fenris", Revision = "fixture-version" }
            ],
            Start.AddSeconds(10)));
        await revisions.HeldEntered.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(8));
        var response = await ConcurrentPrefillTests.ReadResponseAsync(client, "partial");
        var apps = response.GetProperty("data").GetProperty("apps");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.False(response.TryGetProperty("message", out _));
        Assert.False(response.GetProperty("data").TryGetProperty("message", out _));
        Assert.Equal("Current", apps[0].GetProperty("outcome").GetString());
        Assert.Equal("DeadlineReached", apps[1].GetProperty("reason").GetString());
        await revisions.HeldCancelled.WaitAsync(TimeSpan.FromSeconds(5));
        await revisions.HeldDisposed.WaitAsync(TimeSpan.FromSeconds(5));

        await client.SendAsync(new CommandRequest { Id = "still-open", Type = "status" });
        Assert.True((await ConcurrentPrefillTests.ReadResponseAsync(client, "still-open"))
            .GetProperty("success").GetBoolean());
        await commands.StopAsync();
    }

    [Fact]
    public async Task SocketRejectsMalformedV2Requests()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        using var revisions = new RevisionHttp();
        var clock = new CacheStatusClock(Start);
        await using var commands = new SocketCommandInterface(
            0,
            fixture.Protocol,
            WithRevisions(fixture, revisions),
            clock);
        await commands.StartAsync();
        await using var client = await SocketAdapterTests.FramedClient.ConnectAsync(commands.BoundTcpPort);
        var expiry = Start.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture);
        var requests = new[]
        {
            new CommandRequest
            {
                Id = "missing-expiry",
                Type = "check-cache-status",
                Parameters = new Dictionary<string, string>
                {
                    ["cacheStatusVersion"] = "2",
                    ["cachedApps"] = "[]"
                }
            },
            new CommandRequest
            {
                Id = "bad-version",
                Type = "check-cache-status",
                Parameters = new Dictionary<string, string>
                {
                    ["cacheStatusVersion"] = "3",
                    ["expiresAtUtc"] = expiry,
                    ["cachedApps"] = "[]"
                }
            },
            new CommandRequest
            {
                Id = "bad-shape",
                Type = "check-cache-status",
                Parameters = new Dictionary<string, string>
                {
                    ["cacheStatusVersion"] = "2",
                    ["expiresAtUtc"] = expiry,
                    ["cachedApps"] = "{}"
                }
            },
            new CommandRequest
            {
                Id = "missing-apps",
                Type = "check-cache-status",
                Parameters = new Dictionary<string, string>
                {
                    ["cacheStatusVersion"] = "2",
                    ["expiresAtUtc"] = expiry
                }
            },
            CheckRequest("blank-id", [new CachedAppInput { AppId = " " }], Start.AddMinutes(1)),
            CheckRequest(
                "duplicate-id",
                [
                    new CachedAppInput { AppId = "d3" },
                    new CachedAppInput { AppId = "D3" }
                ],
                Start.AddMinutes(1))
        };

        foreach (var request in requests)
        {
            await client.SendAsync(request);
            var response = await ConcurrentPrefillTests.ReadResponseAsync(client, request.Id);
            Assert.False(response.GetProperty("success").GetBoolean());
        }

        Assert.Equal(0, revisions.VersionsRequests);
        await commands.StopAsync();
    }

    private static PrefillSettings WithRevisions(
        ConcurrentPrefillTests.TactFixture fixture,
        RevisionHttp revisions)
        => fixture.Settings with
        {
            CreateClient = () => new HttpClient(revisions, disposeHandler: false)
        };

    private static CommandRequest CheckRequest(
        string id,
        List<CachedAppInput> cachedApps,
        DateTimeOffset expiresAtUtc)
        => new()
        {
            Id = id,
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cacheStatusVersion"] = "2",
                ["expiresAtUtc"] = expiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["cachedApps"] = JsonSerializer.Serialize(
                    cachedApps,
                    DaemonSerializationContext.Default.ListCachedAppInput)
            }
        };

    private enum RevisionReply
    {
        Success,
        Hold,
        Blank,
        Malformed
    }

    private sealed class RevisionHttp : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, RevisionReply> _replies;
        private int _versionsRequests;
        private int _cdnRequests;

        internal RevisionHttp(IReadOnlyDictionary<string, RevisionReply>? replies = null)
        {
            _replies = replies ?? new Dictionary<string, RevisionReply>();
        }

        internal int VersionsRequests => Volatile.Read(ref _versionsRequests);
        internal int CdnRequests => Volatile.Read(ref _cdnRequests);
        internal Task HeldEntered => HeldStream.Entered.Task;
        internal Task HeldCancelled => HeldStream.Cancelled.Task;
        internal Task HeldDisposed => HeldStream.Disposed.Task;
        private HeldBody HeldStream { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var product = segments[0] == "tpr" ? segments[1] : segments[0];
            var last = segments[^1];
            if (last == "cdns")
            {
                Interlocked.Increment(ref _cdnRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"Path!STRING:0|Hosts!STRING:0\ntpr/{product}|cdn.blizzard.com\n")
                });
            }
            if (last != "versions")
            {
                throw new InvalidOperationException("Unexpected revision request: " + request.RequestUri);
            }

            Interlocked.Increment(ref _versionsRequests);
            var reply = _replies.GetValueOrDefault(product, RevisionReply.Success);
            HttpContent content = reply switch
            {
                RevisionReply.Hold => new StreamContent(HeldStream),
                RevisionReply.Blank => new StringContent(Versions(string.Empty)),
                RevisionReply.Malformed => new StringContent("not-a-versions-table"),
                _ => new StringContent(Versions("fixture-version"))
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private static string Versions(string revision)
            => $"BuildConfig!HEX:16|CDNConfig!HEX:16|VersionsName!STRING:0\n{new string('1', 32)}|{new string('2', 32)}|{revision}\n";
    }

    private sealed class HeldBody : Stream
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
