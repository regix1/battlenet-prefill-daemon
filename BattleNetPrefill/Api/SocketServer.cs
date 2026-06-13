#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BattleNetPrefill.Api;

public enum SocketServerMode
{
    UnixSocket,
    Tcp
}

public sealed class SocketServer : IAsyncDisposable
{
    private static readonly HashSet<string> RedactedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth"
    };

    private readonly SocketServerMode _mode;
    private readonly string? _socketPath;
    private readonly int _tcpPort;
    private readonly IPAddress _tcpBindAddress;
    private readonly IPrefillProgress _progress;
    private readonly string? _sharedSecret;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private Task? _acceptTask;
    private bool _disposed;

    public Func<CommandRequest, CancellationToken, Task<CommandResponse>>? OnCommand { get; set; }

    public SocketServer(string socketPath, IPrefillProgress? progress = null)
    {
        _mode = SocketServerMode.UnixSocket;
        _socketPath = socketPath;
        _tcpPort = 0;
        _tcpBindAddress = IPAddress.Any;
        _progress = progress ?? NullProgress.Instance;

        _sharedSecret = Environment.GetEnvironmentVariable("PREFILL_SOCKET_SECRET");
        if (!string.IsNullOrEmpty(_sharedSecret))
        {
            _progress.OnLog(LogLevel.Info, "Socket authentication enabled via PREFILL_SOCKET_SECRET");
        }
    }

    public SocketServer(int tcpPort, IPrefillProgress? progress = null, IPAddress? bindAddress = null)
    {
        _mode = SocketServerMode.Tcp;
        _socketPath = null;
        _tcpPort = tcpPort;
        _tcpBindAddress = bindAddress ?? IPAddress.Any;
        _progress = progress ?? NullProgress.Instance;

        _sharedSecret = Environment.GetEnvironmentVariable("PREFILL_SOCKET_SECRET");
        if (!string.IsNullOrEmpty(_sharedSecret))
        {
            _progress.OnLog(LogLevel.Info, "Socket authentication enabled via PREFILL_SOCKET_SECRET");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_mode == SocketServerMode.UnixSocket)
        {
            if (_socketPath != null && File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                    _progress.OnLog(LogLevel.Debug, $"Removed stale socket file: {_socketPath}");
                }
                catch (Exception ex)
                {
                    _progress.OnLog(LogLevel.Warning, $"Could not remove stale socket file: {ex.Message}");
                }
            }

            var socketPath = _socketPath ?? throw new InvalidOperationException("Socket path is required for Unix socket mode.");

            var dir = Path.GetDirectoryName(socketPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                TrySetUnixPermissions(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(5);

            TrySetUnixPermissions(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

            _progress.OnLog(LogLevel.Info, $"Socket server listening on: {socketPath}");
        }
        else
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(_tcpBindAddress, _tcpPort));
            _listener.Listen(5);
            _progress.OnLog(LogLevel.Info, $"TCP socket server listening on: {_tcpBindAddress}:{_tcpPort}");
        }

        _acceptTask = AcceptConnectionsAsync(_cts.Token);

        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listener!.AcceptAsync(cancellationToken);
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var client = new ConnectedClient(clientId, clientSocket);

                _clients[clientId] = client;
                _progress.OnLog(LogLevel.Info, $"Client connected: {clientId}");

                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Warning, $"Error accepting connection: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(ConnectedClient client, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, client.CancellationToken);
        var token = linkedCts.Token;

        try
        {
            var stream = client.Stream;

            while (!token.IsCancellationRequested)
            {
                var lengthBytes = new byte[4];
                var bytesRead = await ReadExactlyAsync(stream, lengthBytes, token);
                if (bytesRead == 0) break;

                var length = BitConverter.ToInt32(lengthBytes, 0);
                if (length <= 0 || length > 10 * 1024 * 1024)
                {
                    _progress.OnLog(LogLevel.Warning, $"Invalid message length from client {client.Id}: {length}");
                    break;
                }

                var messageBytes = new byte[length];
                bytesRead = await ReadExactlyAsync(stream, messageBytes, token);
                if (bytesRead == 0) break;

                var json = Encoding.UTF8.GetString(messageBytes);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "unknown";
                    var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : "unknown";

                    _progress.OnLog(LogLevel.Debug,
                        RedactedTypes.Contains(type ?? string.Empty)
                            ? $"Received from {client.Id}: {type} (id: {id}) [redacted]"
                            : $"Received from {client.Id}: {type} (id: {id})");
                }
                catch
                {
                    _progress.OnLog(LogLevel.Debug, $"Received from {client.Id}: <unparseable message>");
                }

                CommandResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize(json, DaemonSerializationContext.Default.CommandRequest);
                    if (request == null)
                    {
                        response = new CommandResponse { Id = "unknown", Success = false, Error = "Failed to parse command request" };
                    }
                    else if (OnCommand == null)
                    {
                        response = new CommandResponse { Id = request.Id, Success = false, Error = "No command handler registered" };
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_sharedSecret) && !client.IsAuthenticated)
                        {
                            if (request.Type == "auth" && request.Parameters?.TryGetValue("secret", out var providedSecret) == true)
                            {
                                if (providedSecret == _sharedSecret)
                                {
                                    client.IsAuthenticated = true;
                                    _progress.OnLog(LogLevel.Info, $"Client {client.Id} authenticated successfully");
                                    response = new CommandResponse { Id = request.Id, Success = true, Message = "Authenticated" };
                                }
                                else
                                {
                                    _progress.OnLog(LogLevel.Warning, $"Client {client.Id} failed authentication - invalid secret");
                                    response = new CommandResponse { Id = request.Id, Success = false, Error = "Authentication failed: invalid secret" };
                                    break;
                                }
                            }
                            else
                            {
                                _progress.OnLog(LogLevel.Warning, $"Client {client.Id} sent command without authenticating first");
                                response = new CommandResponse { Id = request.Id, Success = false, Error = "Authentication required. Send 'auth' command with secret first." };
                                break;
                            }
                        }
                        else
                        {
                            response = await OnCommand(request, token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _progress.OnLog(LogLevel.Error, $"Error handling command: {ex.Message}");
                    response = new CommandResponse { Id = "error", Success = false, Error = ex.Message };
                }

                await SendMessageAsync(client, response, DaemonSerializationContext.Default.CommandResponse, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Warning, $"Client {client.Id} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
            _progress.OnLog(LogLevel.Info, $"Client disconnected: {client.Id}");
        }
    }

    public async Task BroadcastProgressAsync(SocketEvent<PrefillProgressUpdate> eventData, CancellationToken cancellationToken = default)
    {
        var tasks = _clients.Values.Select(client =>
            SendEventToClientInternalAsync(client, eventData, DaemonSerializationContext.Default.SocketEventPrefillProgressUpdate, cancellationToken));
        await Task.WhenAll(tasks);
    }

    public async Task BroadcastAuthStateAsync(SocketEvent<AuthStateData> eventData, CancellationToken cancellationToken = default)
    {
        var tasks = _clients.Values.Select(client =>
            SendEventToClientInternalAsync(client, eventData, DaemonSerializationContext.Default.SocketEventAuthStateData, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task SendEventToClientInternalAsync<T>(ConnectedClient client, T eventData, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            await client.SendLock.WaitAsync(cancellationToken);
            try
            {
                var json = JsonSerializer.Serialize(eventData, typeInfo);
                var bytes = Encoding.UTF8.GetBytes(json);

                var stream = client.Stream;
                await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                client.SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Warning, $"Failed to send event to {client.Id}: {ex.Message}");
        }
    }

    private async Task SendMessageAsync<T>(ConnectedClient client, T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, typeInfo);
        await SendRawJsonAsync(client, json, cancellationToken);
    }

    private async Task SendRawJsonAsync(ConnectedClient client, string json, CancellationToken cancellationToken)
    {
        await client.SendLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);

            var stream = client.Stream;
            await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            _progress.OnLog(LogLevel.Debug, $"Sent response to {client.Id}: {json[..Math.Min(200, json.Length)]}...");
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0) return 0;
            totalRead += read;
        }
        return totalRead;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        _listener?.Close();
        _listener?.Dispose();
        _listener = null;

        if (_acceptTask != null)
        {
            try { await _acceptTask; }
            catch (OperationCanceledException) { }
        }

        if (_mode == SocketServerMode.UnixSocket && _socketPath != null && File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); }
            catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Failed to delete socket file {_socketPath}: {ex.Message}"); }
        }

        _progress.OnLog(LogLevel.Info, "Socket server stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _cts.Dispose();
        _disposed = true;
    }

    private void TrySetUnixPermissions(string path, UnixFileMode mode)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) ||
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                File.SetUnixFileMode(path, mode);
                _progress.OnLog(LogLevel.Debug, $"Set permissions on {path}: {mode}");
            }
        }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Warning, $"Could not set permissions on {path}: {ex.Message}");
        }
    }

    private class ConnectedClient : IDisposable
    {
        public string Id { get; }
        public Socket Socket { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource CancellationTokenSource { get; } = new();
        public CancellationToken CancellationToken => CancellationTokenSource.Token;
        public bool IsAuthenticated { get; set; }

        public ConnectedClient(string id, Socket socket)
        {
            Id = id;
            Socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: false);
        }

        public void Dispose()
        {
            CancellationTokenSource.Cancel();
            CancellationTokenSource.Dispose();
            SendLock.Dispose();
            Stream.Dispose();
            try { Socket.Shutdown(SocketShutdown.Both); }
            catch (SocketException) { /* Socket already disconnected */ }
            Socket.Dispose();
        }
    }
}

public class SocketEvent<T>
{
    public string Type { get; init; } = string.Empty;
    public T? Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class ProgressEvent : SocketEvent<PrefillProgressUpdate>
{
    public ProgressEvent(PrefillProgressUpdate progress)
    {
        Type = "progress";
        Data = progress;
    }
}

public class AuthStateEvent : SocketEvent<AuthStateData>
{
    public AuthStateEvent(string state, string? message = null, string? displayName = null)
    {
        Type = "auth-state";
        Data = new AuthStateData { State = state, Message = message, DisplayName = displayName };
    }
}

public class AuthStateData
{
    public string State { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? DisplayName { get; init; }
}
