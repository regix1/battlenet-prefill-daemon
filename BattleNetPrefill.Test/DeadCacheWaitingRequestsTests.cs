using System.Net;
using BattleNetPrefill.Api;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Structs.Enums;
using BattleNetPrefill.Web;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class DeadCacheWaitingRequestsTests
{
    private const int ProcessCeiling = 128;
    private const int QueueLength = 400;
    // CdnRequestManager.cs judges the cache down after 50 failed requests in one attempt.
    private const int FailuresThatJudgeTheCacheDown = 50;
    // The first attempt and two retries, each against the next CDN host and each judging the cache down again.
    private const int Attempts = 3;

    /// <summary>
    /// A run whose request limit (1) is far below the process ceiling (128) meets a cache that refuses every
    /// request. Once an attempt has judged the cache down, the requests already waiting for the run's permit must
    /// not go out one after another, each costing its own request timeout on a real dead cache.
    /// </summary>
    [Fact]
    public async Task ARunBelowTheCeilingSendsNoWaitingRequestsOnceTheCacheIsJudgedDown()
    {
        var cache = new RefusingCache();
        using var budget = new RequestBudget(ProcessCeiling);
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "battlenet-dead-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        try
        {
            using var manager = new CdnRequestManager(
                new ApiConsoleAdapter(NullProgress.Instance),
                new PrefillSettings
                {
                    Budget = budget,
                    OperationId = "dead-cache",
                    MaxConcurrency = 1,
                    CacheDirectory = cacheDirectory,
                    NoLocalCache = true,
                    LancacheAddress = "127.0.0.1",
                    CreateClient = () => new HttpClient(cache)
                },
                NullProgress.Instance);
            for (var i = 0; i < QueueLength; i++)
            {
                manager.QueueRequest(RootFolder.data, new MD5Hash((ulong)i + 1, (ulong)i + 2), startBytes: 0, endBytes: 4095);
            }

            await Assert.ThrowsAsync<TimeoutException>(() => manager.DownloadQueuedRequestsAsync(new PrefillSummaryResult()));

            var judgedDownAfter = Attempts * FailuresThatJudgeTheCacheDown;
            var sentAfterTheVerdict = cache.Refused - judgedDownAfter;
            Assert.True(sentAfterTheVerdict <= Attempts * 8,
                $"{sentAfterTheVerdict} requests went out one at a time after the run judged the cache down " +
                $"({cache.Refused} in all over {Attempts} attempts, each attempt's verdict came after {FailuresThatJudgeTheCacheDown})");
        }
        finally
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    /// <summary>A cache that refuses every request, and counts them.</summary>
    private sealed class RefusingCache : HttpMessageHandler
    {
        private int _refused;

        public int Refused => Volatile.Read(ref _refused);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refused);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
        }
    }
}
