using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BattleNetPrefill.Api;
using LancachePrefill.Common;

namespace BattleNetPrefill.Test;

public sealed class SocketAdapterTests
{
    [Fact]
    public async Task APrefillWithoutProtocolVersionIsRefusedAsOutdated()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        await using var commands = new SocketCommandInterface(0, fixture.Protocol, fixture.Settings);
        await commands.StartAsync();
        await using var client = await FramedClient.ConnectAsync(commands.BoundTcpPort);
        await client.SendAsync(new CommandRequest { Id = "outdated", Type = "prefill" });
        var response = await ConcurrentPrefillTests.ReadResponseAsync(client, "outdated");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("daemon-outdated", response.GetProperty("errorCode").GetString());
        await commands.StopAsync();
    }

    [Fact]
    public async Task UnknownOperationAnswersWithTheTypedNotFoundCode()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        await using var commands = new SocketCommandInterface(0, fixture.Protocol, fixture.Settings);
        await commands.StartAsync();
        await using var client = await FramedClient.ConnectAsync(commands.BoundTcpPort);
        foreach (var type in new[] { "cancel-prefill", "get-operation" })
        {
            await client.SendAsync(fixture.Control(type, type, Guid.NewGuid().ToString("D")));
            var response = await ConcurrentPrefillTests.ReadResponseAsync(client, type);
            Assert.False(response.GetProperty("success").GetBoolean());
            Assert.Equal("operation-not-found",
                response.TryGetProperty("errorCode", out var code) ? code.GetString() : null);
        }
        await commands.StopAsync();
    }

    [Fact]
    public async Task AcceptedRunSurvivesLostAcknowledgementAndReconnect()
    {
        using var fixture = new ConcurrentPrefillTests.TactFixture();
        await using var commands = new SocketCommandInterface(0, fixture.Protocol, fixture.Settings);
        await commands.StartAsync();
        var id = Guid.NewGuid().ToString("D");
        var first = await FramedClient.ConnectAsync(commands.BoundTcpPort);
        await first.SendAsync(fixture.Start(id, "d3"));
        await fixture.Body("d3").Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.DisposeAsync();
        await using var second = await FramedClient.ConnectAsync(commands.BoundTcpPort);
        await second.SendAsync(new CommandRequest { Id = "reconnect", Type = "status" });
        var status = (await ConcurrentPrefillTests.ReadResponseAsync(second, "reconnect")).GetProperty("data");
        Assert.Equal(fixture.Protocol.DaemonInstanceId, status.GetProperty("daemonInstanceId").GetString());
        Assert.Equal(id, status.GetProperty("activeOperations")[0].GetProperty("operationId").GetString());
        Assert.True(status.GetProperty("isLoggedIn").GetBoolean());
        Assert.Equal(2, status.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(6, status.GetProperty("features").GetArrayLength());
        Assert.Contains(status.GetProperty("features").EnumerateArray(),
            feature => feature.GetString() == "cacheStatusV2");
        await second.SendAsync(new CommandRequest { Id = "single-cancel", Type = "cancel-prefill" });
        Assert.True((await ConcurrentPrefillTests.ReadResponseAsync(second, "single-cancel")).GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.ContentRequests);
        await second.SendAsync(fixture.Control("repeat", "cancel-prefill", id));
        Assert.Equal("cancelled", (await ConcurrentPrefillTests.ReadResponseAsync(second, "repeat")).GetProperty("data").GetProperty("state").GetString());
        await second.SendAsync(fixture.Control("missing", "get-operation", Guid.NewGuid().ToString("D")));
        Assert.Equal("operation-not-found", (await ConcurrentPrefillTests.ReadResponseAsync(second, "missing")).GetProperty("error").GetString());
        await commands.StopAsync();
    }

    [Fact]
    public async Task SocketReader_HandlesControlRequestWhileConcurrentHandlerIsBlocked()
    {
        var slowStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new SocketServer(0, NullProgress.Instance);
        server.OnCommand = async (request, cancellationToken) =>
        {
            if (request.Type == "slow")
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
            }

            return new CommandResponse { Id = request.Id, Success = true };
        };

        await server.StartAsync();
        await using var client = await FramedClient.ConnectAsync(server.BoundTcpPort);
        await client.SendAsync(new CommandRequest { Id = "slow-1", Type = "slow" });
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.SendAsync(new CommandRequest { Id = "status-1", Type = "status" });

        var status = await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("status-1", status.GetProperty("id").GetString());

        releaseSlow.TrySetResult();
        var slow = await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("slow-1", slow.GetProperty("id").GetString());

        await server.StopAsync();
    }

    [Fact]
    public async Task ClientDisconnect_CancelsAndDrainsDispatchedHandler()
    {
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new SocketServer(0, NullProgress.Instance);
        server.OnCommand = async (request, cancellationToken) =>
        {
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CommandResponse { Id = request.Id, Success = true };
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        };

        await server.StartAsync();
        var client = await FramedClient.ConnectAsync(server.BoundTcpPort);
        await client.SendAsync(new CommandRequest { Id = "slow-1", Type = "slow" });
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.DisposeAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.StopAsync();
    }

    [Fact]
    public void SocketProgress_UsesSharedDefaultAndDebugLogPolicy()
    {
        var normalLines = new List<string>();
        var normal = new SocketCommandInterface.SocketProgress(
            normalLines.Add,
            DaemonLogLevel.Info);

        normal.OnLog(LogLevel.Debug, "hidden");
        normal.OnLog(LogLevel.Info, "information");
        normal.OnLog(LogLevel.Warning, "warning");
        normal.OnLog(LogLevel.Error, "error");

        Assert.DoesNotContain(normalLines, line => line.Contains("hidden", StringComparison.Ordinal));
        Assert.Contains(normalLines, line => line.Contains("information", StringComparison.Ordinal));
        Assert.Contains(normalLines, line => line.Contains("warning", StringComparison.Ordinal));
        Assert.Contains(normalLines, line => line.Contains("error", StringComparison.Ordinal));

        var debugLines = new List<string>();
        var debug = new SocketCommandInterface.SocketProgress(
            debugLines.Add,
            DaemonLogLevel.Debug);
        debug.OnLog(LogLevel.Debug, "visible");

        Assert.Contains(debugLines, line => line.Contains("visible", StringComparison.Ordinal));
    }

    internal sealed class FramedClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private FramedClient(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<FramedClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new FramedClient(client);
        }

        public async Task SendAsync(CommandRequest request)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                request,
                DaemonSerializationContext.Default.CommandRequest);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            await _stream.WriteAsync(length);
            await _stream.WriteAsync(payload);
            await _stream.FlushAsync();
        }

        public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken = default)
        {
            var length = new byte[sizeof(int)];
            await _stream.ReadExactlyAsync(length, cancellationToken);
            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
            await _stream.ReadExactlyAsync(payload, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
