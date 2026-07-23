using System.Net;
using BattleNetPrefill.Api;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Structs.Enums;
using BattleNetPrefill.Web;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class CdnRequestManagerCancellationTests
{
    [Fact]
    public async Task MetadataRequest_PropagatesCallerCancellationToSendAsync()
    {
        var handler = new BlockingSendHandler();
        using var httpClient = new HttpClient(handler);
        using var manager = CreateManager(httpClient);
        using var cancellation = new CancellationTokenSource();
        var previousNoLocalCache = AppConfig.NoLocalCache;
        AppConfig.NoLocalCache = true;

        try
        {
            var request = manager.GetRequestAsBytesAsync(
                RootFolder.data,
                new MD5Hash(1, 2),
                cancellationToken: cancellation.Token);

            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(2)));
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            AppConfig.NoLocalCache = previousNoLocalCache;
        }
    }

    [Fact]
    public async Task DownloadQueuedRequests_PropagatesCallerCancellationToResponseRead()
    {
        var responseStream = new BlockingReadStream();
        using var httpClient = new HttpClient(new StreamingResponseHandler(responseStream));
        using var manager = CreateManager(httpClient);
        using var cancellation = new CancellationTokenSource();

        manager.QueueRequest(
            RootFolder.data,
            new MD5Hash(3, 4),
            startBytes: 0,
            endBytes: 4095);

        var download = manager.DownloadQueuedRequestsAsync(
            new PrefillSummaryResult(),
            cancellation.Token);

        await responseStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => download.WaitAsync(TimeSpan.FromSeconds(2)));
        await responseStream.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class BlockingSendHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking request unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class StreamingResponseHandler(BlockingReadStream responseStream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            });
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
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

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadCoreAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(ReadCoreAsync(cancellationToken));

        private async Task<int> ReadCoreAsync(CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
