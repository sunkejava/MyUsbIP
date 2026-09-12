using System.Collections.Concurrent;
using MyUsbIP.Abstractions;

namespace MyUsbIP.Runtime;

/// <summary>
/// 客户端连接管理器。
/// 统一记录连接状态，并在远端设备重新出现后自动重新 Attach。
/// </summary>
public sealed class UsbIpConnectionManager(
    IMyUsbIpClient client,
    IUsbIpEventSink? eventSink = null) : IUsbIpConnectionManager
{
    private readonly ConcurrentDictionary<string, UsbIpManagedConnection> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUsbIpEventSink sink = eventSink ?? NullUsbIpEventSink.Instance;

    public IReadOnlyList<UsbIpManagedConnection> GetConnections() =>
        connections.Values.OrderBy(x => x.CreatedAt).ToArray();

    public async Task<UsbIpManagedConnection> ConnectAsync(
        string host,
        string busId,
        int port = 3240,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(busId);

        var id = BuildId(host, port, busId);
        var state = new UsbIpManagedConnection
        {
            ConnectionId = id,
            Host = host,
            BusId = busId,
            ServerPort = port,
            State = UsbIpConnectionState.Connecting,
            CreatedAt = DateTimeOffset.Now,
        };
        connections[id] = state;

        try
        {
            var result = await client.AttachAsync(host, busId, port, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(result.Message ?? "USB/IP Attach 失败。 ");

            state = state with
            {
                State = UsbIpConnectionState.Connected,
                LocalPort = result.Port,
                LastConnectedAt = DateTimeOffset.Now,
                LastError = null,
            };
            connections[id] = state;
            await WriteAsync("connection.connected", "Information", state, "远程 USB 已挂载", cancellationToken);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state = state with { State = UsbIpConnectionState.Faulted, LastError = ex.Message };
            connections[id] = state;
            await WriteAsync("connection.failed", "Error", state, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (!connections.TryRemove(connectionId, out var state)) return;

        if (state.LocalPort is int localPort)
        {
            await client.DetachAsync(localPort, cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync("connection.disconnected", "Information", state, "远程 USB 已断开", cancellationToken);
    }

    public async Task RunRecoveryLoopAsync(UsbIpReconnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new UsbIpReconnectOptions();
        if (!options.Enabled) return;

        var failures = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var pair in connections.ToArray())
            {
                var connection = pair.Value;
                try
                {
                    var remote = await client.GetRemoteDevicesAsync(connection.Host, connection.ServerPort, cancellationToken).ConfigureAwait(false);
                    var exists = remote.Any(x => string.Equals(x.BusId, connection.BusId, StringComparison.OrdinalIgnoreCase));

                    if (!exists)
                    {
                        if (connection.State != UsbIpConnectionState.Reconnecting)
                        {
                            connection = connection with { State = UsbIpConnectionState.Reconnecting, LastError = "远端设备暂不可见" };
                            connections[pair.Key] = connection;
                            await WriteAsync("connection.remote.missing", "Warning", connection, "远端设备暂不可见，等待恢复", cancellationToken);
                        }
                        continue;
                    }

                    if (connection.State is UsbIpConnectionState.Faulted or UsbIpConnectionState.Reconnecting)
                    {
                        await Task.Delay(options.RetryDelay, cancellationToken).ConfigureAwait(false);
                        var result = await client.AttachAsync(connection.Host, connection.BusId, connection.ServerPort, cancellationToken).ConfigureAwait(false);
                        if (!result.Success) throw new InvalidOperationException(result.Message ?? "重新挂载失败");

                        connection = connection with
                        {
                            State = UsbIpConnectionState.Connected,
                            LocalPort = result.Port ?? connection.LocalPort,
                            LastConnectedAt = DateTimeOffset.Now,
                            LastError = null,
                        };
                        connections[pair.Key] = connection;
                        failures[pair.Key] = 0;
                        await WriteAsync("connection.recovered", "Information", connection, "远程 USB 连接已恢复", cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var count = failures.AddOrUpdate(pair.Key, 1, static (_, value) => value + 1);
                    var latest = connection with { State = UsbIpConnectionState.Faulted, LastError = ex.Message };
                    connections[pair.Key] = latest;
                    await WriteAsync("connection.health.failed", "Warning", latest, $"连接检查失败，第 {count} 次: {ex.Message}", cancellationToken);

                    if (options.MaxConsecutiveFailures > 0 && count >= options.MaxConsecutiveFailures)
                    {
                        await WriteAsync("connection.recovery.stopped", "Error", latest, "已达到最大连续失败次数，停止自动恢复该连接", cancellationToken);
                        connections.TryRemove(pair.Key, out _);
                    }
                }
            }

            await Task.Delay(options.CheckInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask WriteAsync(string name, string level, UsbIpManagedConnection state, string message, CancellationToken cancellationToken) =>
        sink.WriteAsync(new(DateTimeOffset.Now, name, level, null, state.BusId, state.Host, message), cancellationToken);

    private static string BuildId(string host, int port, string busId) => $"{host}:{port}/{busId}";
}
