using System.Net;
using BattleNetPrefill.Api;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Structs.Enums;
using BattleNetPrefill.Web;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class CdnRequestManagerBatchAbandonTests
{
    /// <summary>
    /// A queue far longer than the abandon threshold, against a cache that answers nothing.  Without the check
    /// every request in the queue is attempted, so the cost grows with the queue rather than staying flat.
    /// </summary>
    [Fact]
    public async Task DownloadQueuedRequests_SourceReturningNothing_AbandonsQueueWithoutAttemptingEveryRequest()
    {
        var handler = new FailingHandler();
        using var httpClient = new HttpClient(handler);
        using var manager = CreateManager(httpClient);

        QueueRequests(manager, 400);

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => manager.DownloadQueuedRequestsAsync(new PrefillSummaryResult()));

        Assert.Contains("not one byte arrived", failure.Message);
        Assert.Contains("127.0.0.1", failure.Message);

        // Three attempts walk three CDN hosts, and each abandons after its own two waves, so the work done is a
        // small multiple of the threshold rather than the whole 400 request queue three times over.
        Assert.InRange(handler.RequestCount, 1, 400);
    }

    /// <summary>
    /// The over-fire guard.  A cache whose first wave fails and which then serves everything else must still
    /// finish.  This passes with and without the abandon check, because code that never abandons cannot fail an
    /// assertion that it did not abandon; it exists to catch a threshold set low enough to break real downloads.
    /// </summary>
    [Fact]
    public async Task DownloadQueuedRequests_SourceRecoveringAfterFirstWave_StillCompletes()
    {
        var handler = new FailsFirstRequestsHandler(failFirst: 20);
        using var httpClient = new HttpClient(handler);
        using var manager = CreateManager(httpClient);

        QueueRequests(manager, 200);

        var succeeded = await manager.DownloadQueuedRequestsAsync(new PrefillSummaryResult());

        Assert.True(succeeded);
    }

    private static void QueueRequests(CdnRequestManager manager, int count)
    {
        for (var i = 0; i < count; i++)
        {
            manager.QueueRequest(
                RootFolder.data,
                new MD5Hash((ulong)i + 1, (ulong)i + 2),
                startBytes: 0,
                endBytes: 4095);
        }
    }

    private static CdnRequestManager CreateManager(HttpClient httpClient)
        => new(
            new ApiConsoleAdapter(NullProgress.Instance),
            httpClient,
            "127.0.0.1",
            "tpr/test",
            NullProgress.Instance,
            "test",
            "Test Product");

    private sealed class FailingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
        }
    }

    private sealed class FailsFirstRequestsHandler(int failFirst) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestCount);
            if (index <= failFirst)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4096])
            });
        }
    }
}
