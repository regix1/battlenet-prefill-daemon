using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BattleNetPrefill.Api;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Structs.Enums;
using BattleNetPrefill.Web;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class ConcurrentPrefillTests
{
    [Fact]
    public async Task SmallAndLargeQueuesShareTheDefaultTwentyFiveBodyBudget()
    {
        using var fixture = new TactFixture(25);
        var products = new[] { TactProduct.Diablo3, TactProduct.Diablo4, TactProduct.Starcraft1 };
        var managers = products.Select(product => new CdnRequestManager(new ApiConsoleAdapter(NullProgress.Instance),
            fixture.Settings with { OperationId = product.ProductCode, MaxConcurrency = 10 }, NullProgress.Instance)).ToArray();
        try
        {
            for (var index = 0; index < managers.Length; index++)
            {
                await managers[index].InitializeAsync(products[index]);
                for (var i = 20; i < 60; i++)
                    managers[index].QueueRequest(RootFolder.data, new MD5Hash((ulong)i, (ulong)i + 1),
                        0, i % 2 == 0 ? 4095 : 1048575);
            }
            var tasks = managers.Select(manager => manager.DownloadQueuedRequestsAsync(new PrefillSummaryResult())).ToArray();
            await fixture.BudgetFilled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(25, fixture.PeakBodies);
            foreach (var product in products)
            {
                Assert.True(fixture.Body(product.ProductCode).Entered.Task.IsCompleted);
                Assert.InRange(fixture.Body(product.ProductCode).Peak, 1, 10);
                fixture.Body(product.ProductCode).Release.TrySetResult();
            }
            Assert.All(await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)), Assert.True);
            Assert.Equal(25, fixture.PeakBodies);
        }
        finally
        {
            foreach (var product in products) { fixture.Body(product.ProductCode).Release.TrySetResult(); }
            foreach (var manager in managers) { manager.Dispose(); }
        }
    }

    [Fact]
    public async Task ThreeProductBodiesOverlapAndTargetedCancelPreservesSiblings()
    {
        using var fixture = new TactFixture();
        await using var commands = new SocketCommandInterface(0, fixture.Protocol, fixture.Settings);
        await commands.StartAsync();
        await using var client = await SocketAdapterTests.FramedClient.ConnectAsync(commands.BoundTcpPort);
        var products = new[] { "d3", "fenris", "s1" };
        var ids = products.Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            await client.SendAsync(fixture.Start(ids[i], products[i]));
            Assert.True((await ReadResponseAsync(client, ids[i])).GetProperty("success").GetBoolean());
        }
        await Task.WhenAll(products.Select(product => fixture.Body(product).Entered.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, fixture.PeakBodies);

        var excess = Guid.NewGuid().ToString("D");
        await client.SendAsync(fixture.Start(excess, "s2"));
        Assert.Equal("run-limit", (await ReadResponseAsync(client, excess)).GetProperty("error").GetString());

        await client.SendAsync(new CommandRequest { Id = "ambiguous", Type = "cancel-prefill" });
        Assert.Equal("ambiguous-operation", (await ReadResponseAsync(client, "ambiguous")).GetProperty("error").GetString());
        Assert.All(products, product => Assert.False(fixture.Body(product).Cancelled.Task.IsCompleted));

        await client.SendAsync(fixture.Control("wrong", "cancel-prefill", ids[0], "wrong-instance"));
        Assert.Equal("instance-changed", (await ReadResponseAsync(client, "wrong")).GetProperty("error").GetString());
        await client.SendAsync(fixture.Control("cancel", "cancel-prefill", ids[0]));
        Assert.True((await ReadResponseAsync(client, "cancel")).GetProperty("success").GetBoolean());
        await fixture.Body(products[0]).Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var size = new TactProductHandler(new ApiConsoleAdapter(NullProgress.Instance), false, NullProgress.Instance,
            fixture.Settings with { OperationId = Guid.NewGuid().ToString("D") });
        Assert.Equal(8, await size.GetProductDownloadSizeAsync(TactProduct.Starcraft2).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "prefilledVersion-s2.txt")));
        Assert.False(fixture.Body(products[1]).Cancelled.Task.IsCompleted);
        Assert.False(fixture.Body(products[2]).Cancelled.Task.IsCompleted);
        fixture.Body(products[1]).Release.TrySetResult();
        fixture.Body(products[2]).Release.TrySetResult();

        var terminal = new Dictionary<string, JsonElement>();
        using var terminalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (terminal.Count != 3)
        {
            terminalDeadline.Token.ThrowIfCancellationRequested();
            await client.SendAsync(new CommandRequest { Id = "status", Type = "status" });
            var status = (await ReadResponseAsync(client, "status")).GetProperty("data");
            foreach (var operation in status.GetProperty("recentOperations").EnumerateArray())
                terminal[operation.GetProperty("operationId").GetString()!] = operation.Clone();
            if (terminal.Count != 3) { await Task.Yield(); }
        }
        Assert.Equal("cancelled", terminal[ids[0]].GetProperty("state").GetString());
        Assert.Equal(2, terminal[ids[0]].GetProperty("bytesTransferred").GetInt64());
        foreach (var id in ids.Skip(1))
        {
            Assert.Equal("completed", terminal[id].GetProperty("state").GetString());
            Assert.Equal(8, terminal[id].GetProperty("bytesTransferred").GetInt64());
        }
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "prefilledVersion-d3.txt")));
        Assert.Equal("fixture-version", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "prefilledVersion-fenris.txt")));

        await client.SendAsync(fixture.Control("page", "get-operation", ids[1]));
        var page = (await ReadResponseAsync(client, "page")).GetProperty("data");
        Assert.Equal("success", page.GetProperty("items")[0].GetProperty("result").GetString());
        Assert.Equal(1, page.GetProperty("totalItems").GetInt32());
        var before = fixture.ContentRequests;
        await client.SendAsync(fixture.Start(ids[1], products[1]));
        Assert.True((await ReadResponseAsync(client, ids[1])).GetProperty("success").GetBoolean());
        Assert.Equal(before, fixture.ContentRequests);
        await client.SendAsync(fixture.Start(ids[1], products[2]));
        Assert.Equal("operation-conflict", (await ReadResponseAsync(client, ids[1])).GetProperty("error").GetString());
        await commands.StopAsync();
    }

    internal static async Task<JsonElement> ReadResponseAsync(SocketAdapterTests.FramedClient client, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await client.ReadAsync(deadline.Token);
            if (message.TryGetProperty("id", out var received) && received.GetString() == id) { return message; }
        }
    }

    internal sealed class TactFixture : IDisposable
    {
        private readonly ConcurrentDictionary<string, ProductBody> _bodies = new(StringComparer.Ordinal);
        private int _activeBodies;
        private int _peakBodies;
        private int _contentRequests;
        internal TactFixture(int requests = 3)
        {
            Directory = Path.Combine(Path.GetTempPath(), "battlenet-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Protocol = new PrefillProtocol(25, "3", requests.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Budget = new RequestBudget(requests);
            Settings = new PrefillSettings
            {
                Budget = Budget,
                OperationId = Guid.NewGuid().ToString("D"),
                MaxConcurrency = 1,
                CacheDirectory = Directory,
                NoLocalCache = false,
                LancacheAddress = "127.0.0.1",
                CreateClient = () => new HttpClient(new TactHttp(this))
            };
        }
        internal string Directory { get; }
        internal PrefillProtocol Protocol { get; }
        internal RequestBudget Budget { get; }
        internal PrefillSettings Settings { get; }
        internal int PeakBodies => Volatile.Read(ref _peakBodies);
        internal int ContentRequests => Volatile.Read(ref _contentRequests);
        internal TaskCompletionSource BudgetFilled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProductBody Body(string product) => _bodies.GetOrAdd(product, _ => new ProductBody());

        internal CommandRequest Start(string id, string product) => new()
        {
            Id = id,
            Type = "prefill",
            Parameters = new Dictionary<string, string>
            {
                ["protocolVersion"] = "2",
                ["daemonInstanceId"] = Protocol.DaemonInstanceId,
                ["appIds"] = JsonSerializer.Serialize(new[] { product }),
                ["maxConcurrency"] = "1"
            }
        };
        internal CommandRequest Control(string id, string type, string operationId, string? instance = null) => new()
        {
            Id = id,
            Type = type,
            Parameters = new Dictionary<string, string>
            {
                ["daemonInstanceId"] = instance ?? Protocol.DaemonInstanceId,
                ["operationId"] = operationId
            }
        };

        public void Dispose()
        {
            foreach (var body in _bodies.Values) { body.Release.TrySetResult(); }
            Budget.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        private sealed class TactHttp(TactFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var last = segments[^1];
                var product = segments[0] == "tpr" ? segments[1] : segments[0];
                HttpContent content;
                if (last == "cdns")
                    content = new StringContent($"Path!STRING:0|Hosts!STRING:0\ntpr/{product}|cdn.blizzard.com\n");
                else if (last == "versions")
                    content = new StringContent($"BuildConfig!HEX:16|CDNConfig!HEX:16|VersionsName!STRING:0\n{Hash(1)}|{Hash(2)}|fixture-version\n");
                else if (last == Hash(1))
                    content = new StringContent($"# Build\ndownload = {Hash(0)} {Hash(3)}\ninstall = {Hash(0)} {Hash(4)}\ninstall-size = 4096 4096\nencoding = {Hash(0)} {Hash(5)}\nencoding-size = 4 4\nsize = {Hash(0)} {Hash(6)}\nsize-size = 4 4\n");
                else if (last == Hash(2))
                    content = new StringContent($"archives = {Hash(7)}\nfile-index = {Hash(8)}\n");
                else if (last == Hash(7) + ".index")
                {
                    var footer = new byte[20];
                    new byte[] { 1, 0, 0, 4, 4, 4, 16, 8 }.CopyTo(footer, 0);
                    content = new ByteArrayContent(footer);
                }
                else if (last == Hash(8) + ".index")
                {
                    var footer = new byte[28];
                    new byte[] { 1, 0, 0, 4, 4, 4, 16, 8 }.CopyTo(footer, 8);
                    content = new ByteArrayContent(footer);
                }
                else if (last == Hash(3))
                {
                    var manifest = new byte[] { 68, 76, 1, 16, 0, 0, 0, 0, 0, 0, 1 }
                        .Concat(Encoding.UTF8.GetBytes("enUS\0")).Concat(new byte[] { 0, 0 }).ToArray();
                    content = new ByteArrayContent(Blte(manifest));
                }
                else if (last == Hash(4))
                    content = new ByteArrayContent(Blte(new byte[] { 73, 78, 1, 16, 0, 0, 0, 0, 0, 0 }));
                else if (last == Hash(5) || last == Hash(6) || request.Headers.Range != null)
                {
                    Interlocked.Increment(ref fixture._contentRequests);
                    var range = request.Headers.Range!.Ranges.Single();
                    content = new StreamContent(new ContentStream(fixture, fixture.Body(product), range.To!.Value - range.From!.Value + 1));
                }
                else { throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        }

        private static string Hash(int value) => new((char)('0' + value), 32);
        private static byte[] Blte(byte[] bytes)
        {
            var result = new byte[37 + bytes.Length];
            Encoding.ASCII.GetBytes("BLTE").CopyTo(result, 0);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), 36);
            result[11] = 1;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(12), bytes.Length + 1);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16), bytes.Length);
            result[36] = 78;
            bytes.CopyTo(result, 37);
            return result;
        }

        private sealed class ContentStream(TactFixture fixture, ProductBody body, long length) : Stream
        {
            private int _position;
            private bool _entered;
            private bool _disposed;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_entered)
                {
                    _entered = true;
                    var active = Interlocked.Increment(ref fixture._activeBodies);
                    int previous;
                    do { previous = Volatile.Read(ref fixture._peakBodies); }
                    while (active > previous && Interlocked.CompareExchange(ref fixture._peakBodies, active, previous) != previous);
                    if (active == 25) { fixture.BudgetFilled.TrySetResult(); }
                    var productActive = Interlocked.Increment(ref body.Active);
                    do { previous = Volatile.Read(ref body.Peak); }
                    while (productActive > previous && Interlocked.CompareExchange(ref body.Peak, productActive, previous) != previous);
                    body.Entered.TrySetResult();
                }
                if (_position == length) { return 0; }
                if (_position != 0)
                {
                    try { await body.Release.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { body.Cancelled.TrySetResult(); throw; }
                }
                var count = _position == 0 ? Math.Min(2, buffer.Length) : (int)Math.Min(length - _position, buffer.Length);
                buffer.Span[..count].Clear();
                _position += count;
                return count;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            protected override void Dispose(bool disposing)
            {
                if (!_disposed && _entered)
                {
                    Interlocked.Decrement(ref fixture._activeBodies);
                    Interlocked.Decrement(ref body.Active);
                }
                _disposed = true;
                base.Dispose(disposing);
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
