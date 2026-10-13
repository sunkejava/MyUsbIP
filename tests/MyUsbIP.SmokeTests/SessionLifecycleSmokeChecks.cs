using System.Net;
using System.Net.Sockets;
using MyUsbIP.Abstractions;
using MyUsbIP.NativeServer;
using MyUsbIP.Protocol;

internal static class SessionLifecycleSmokeChecks
{
    public static async Task RunAsync(List<string> failures)
    {
        await RunCase("同步完成不会留下幽灵 URB", SyncCompletionAsync);
        await RunCase("迟缓取消期间禁止重连，排空后允许复用序号", SlowCancellationAsync);

        async Task RunCase(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"Session lifecycle: PASS ({name})"); }
            catch (Exception ex) { failures.Add($"Session lifecycle: {name}: {ex.Message}"); }
        }
    }

    private static async Task SyncCompletionAsync()
    {
        var fake = new FakeTransport { Synchronous = true };
        var sink = new Sink();
        var port = GetPort();
        await using var server = new UsbIpNativeServer(fake, IPAddress.Loopback, port, sink);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        using var client = await ImportAsync(port);
        for (var i = 0; i < 2; i++)
        {
            await UsbIpRemoteProtocol.WriteSubmitAsync(client.GetStream(), Request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await UsbIpRemoteProtocol.ReadSubmitCompletionAsync(client.GetStream(), timeout.Token);
            Require(response.Sequence == 1 && response.Status == 0, "同步请求完成错误");
        }
        client.Dispose();
        await sink.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(fake.CancelCount == 0, "已完成请求仍在断开时被取消");
        stop.Cancel();
        await running;
    }

    private static async Task SlowCancellationAsync()
    {
        var fake = new FakeTransport();
        var sink = new Sink();
        var port = GetPort();
        var server = new UsbIpNativeServer(fake, IPAddress.Loopback, port, sink);
        var running = server.RunAsync();
        try
        {
            using var client = await ImportAsync(port);
            await UsbIpRemoteProtocol.WriteSubmitAsync(client.GetStream(), Request);
            await fake.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            client.Dispose();
            await sink.DrainWaiting.Task.WaitAsync(TimeSpan.FromSeconds(9));
            Require(!fake.Ended.Task.IsCompleted, "未完成 URB 被提前释放");
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            await UsbIpRemoteProtocol.RequestImportAsync(second.GetStream(), "1-1");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var rejected = await UsbIpCodec.ReadOperationHeaderAsync(second.GetStream(), timeout.Token);
            Require(rejected.Status != 0 && fake.BeginCount == 1, "清理期间再次 IMPORT 未被隔离");

            fake.Completion.TrySetResult();
            await sink.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fake.Synchronous = true;
            using var third = await ImportAsync(port);
            await UsbIpRemoteProtocol.WriteSubmitAsync(third.GetStream(), Request);
            var completion = await UsbIpRemoteProtocol.ReadSubmitCompletionAsync(third.GetStream(), timeout.Token);
            Require(completion.Status == 0 && fake.BeginCount == 2, "排空后再次连接失败");
        }
        finally
        {
            fake.Completion.TrySetResult();
            await server.DisposeAsync();
            await running;
        }
    }

    private static readonly UsbIpSubmitRequest Request = new(1, 1, 1, 1, 0, 0, 0, 0, 0, new byte[8], []);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static int GetPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
    private static async Task<TcpClient> ImportAsync(int port)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await UsbIpRemoteProtocol.RequestImportAsync(client.GetStream(), "1-1", timeout.Token);
            await UsbIpRemoteProtocol.ReadImportReplyAsync(client.GetStream(), timeout.Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private sealed class Sink : IUsbIpEventSink
    {
        public TaskCompletionSource Released { get; } = Signal();
        public TaskCompletionSource DrainWaiting { get; } = Signal();
        public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt.EventName == "native.session.released") Released.TrySetResult();
            if (evt.EventName == "native.session.drain.waiting") DrainWaiting.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class FakeTransport : IUsbIpExportTransport
    {
        private readonly UsbIpDeviceInfo device = new() { BusId = "1-1", Product = "test", State = UsbIpDeviceState.Available };
        public bool Synchronous;
        public int BeginCount;
        public int CancelCount;
        public TaskCompletionSource Submitted { get; } = Signal();
        public TaskCompletionSource Completion { get; } = Signal();
        public TaskCompletionSource Ended { get; } = Signal();
        public Task<IReadOnlyList<UsbIpDeviceInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UsbIpDeviceInfo>>([device]);
        public Task<UsbIpDeviceInfo?> FindAsync(string busId, CancellationToken cancellationToken = default) => Task.FromResult<UsbIpDeviceInfo?>(device);
        public Task BeginSessionAsync(string busId, CancellationToken cancellationToken = default) { Interlocked.Increment(ref BeginCount); return Task.CompletedTask; }
        public Task EndSessionAsync(string busId, CancellationToken cancellationToken = default) { Ended.TrySetResult(); return Task.CompletedTask; }
        public Task CancelAsync(string busId, uint sequence, CancellationToken cancellationToken = default) { Interlocked.Increment(ref CancelCount); return Task.CompletedTask; }
        public async Task<UsbIpSubmitCompletion> SubmitAsync(string busId, UsbIpSubmitRequest request, CancellationToken cancellationToken = default)
        {
            Submitted.TrySetResult();
            // 模拟驱动已收到取消、但内核请求迟迟未完成。
            if (!Synchronous) await Completion.Task;
            return new(request.Sequence, request.DeviceId, request.Direction, request.Endpoint, 0, 0, 0, 0, 0, []);
        }
    }
}
