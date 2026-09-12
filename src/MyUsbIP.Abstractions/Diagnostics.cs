using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MyUsbIP.Abstractions;

/// <summary>USB/IP 结构化诊断事件。</summary>
public sealed record UsbIpEvent(
    DateTimeOffset Timestamp,
    string EventName,
    string Level,
    string? TraceId,
    string? BusId,
    string? RemoteHost,
    string? Message,
    IReadOnlyDictionary<string, object?>? Properties = null,
    Exception? Exception = null);

/// <summary>
/// MyUsbIP 全局诊断入口。
/// 使用 BCL 原生 Activity/Meter，不强制依赖某个日志框架；调用方可直接接 OpenTelemetry。
/// </summary>
public static class UsbIpDiagnostics
{
    public const string ActivitySourceName = "MyUsbIP";
    public const string MeterName = "MyUsbIP";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName, "0.1.0");

    public static readonly Counter<long> Operations = Meter.CreateCounter<long>("myusbip.operations");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("myusbip.failures");
    public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("myusbip.network.bytes.sent");
    public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("myusbip.network.bytes.received");
    public static readonly Histogram<double> OperationDurationMs = Meter.CreateHistogram<double>("myusbip.operation.duration", "ms");
    public static readonly UpDownCounter<long> ActiveConnections = Meter.CreateUpDownCounter<long>("myusbip.connections.active");

    /// <summary>启动一个带统一标签的链路跟踪 Activity。</summary>
    public static Activity? StartActivity(string operation, string? busId = null, string? remoteHost = null)
    {
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("usbip.operation", operation);
        if (!string.IsNullOrWhiteSpace(busId)) activity?.SetTag("usbip.bus_id", busId);
        if (!string.IsNullOrWhiteSpace(remoteHost)) activity?.SetTag("server.address", remoteHost);
        return activity;
    }
}

/// <summary>默认空事件接收器，避免业务层到处判断 null。</summary>
public sealed class NullUsbIpEventSink : IUsbIpEventSink
{
    public static NullUsbIpEventSink Instance { get; } = new();
    private NullUsbIpEventSink() { }
    public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
