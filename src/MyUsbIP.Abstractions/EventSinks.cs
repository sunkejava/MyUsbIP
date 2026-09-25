using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyUsbIP.Abstractions;

/// <summary>
/// 将 USB/IP 结构化事件按 JSON Lines 方式写入文件。
/// 每行一个完整 JSON，方便后期使用 PowerShell、jq、ELK、Loki 等工具分析。
/// </summary>
public sealed class JsonLinesUsbIpEventSink : IUsbIpEventSink, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly StreamWriter writer;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 日志主要用于人工排查。默认 JSON Encoder 会把中文写成 \uXXXX，严重影响直接阅读。
        // UnsafeRelaxedJsonEscaping 仍会保持 JSON 必需的引号/控制字符转义，但中文直接以 UTF-8 原文落盘。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonLinesUsbIpEventSink(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public async ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        // 不直接序列化 Exception。Exception 内部包含 MethodBase 等复杂成员，
        // 在 NativeAOT/裁剪或部分运行时下可能导致整个错误事件无法落盘。
        var payload = new
        {
            evt.Timestamp,
            evt.EventName,
            evt.Level,
            evt.TraceId,
            evt.BusId,
            evt.RemoteHost,
            evt.Message,
            evt.Properties,
            Exception = evt.Exception is null ? null : new
            {
                Type = evt.Exception.GetType().FullName,
                evt.Exception.Message,
                evt.Exception.StackTrace,
                evt.Exception.HResult,
                NativeErrorCode = evt.Exception is Win32Exception win32
                    ? win32.NativeErrorCode
                    : evt.Exception.InnerException is Win32Exception innerWin32
                        ? innerWin32.NativeErrorCode
                        : (int?)null,
                InnerType = evt.Exception.InnerException?.GetType().FullName,
                InnerMessage = evt.Exception.InnerException?.Message,
            },
        };
        var json = JsonSerializer.Serialize(payload, jsonOptions);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}

/// <summary>同时把同一事件发送给多个接收器。</summary>
public sealed class CompositeUsbIpEventSink(params IUsbIpEventSink[] sinks) : IUsbIpEventSink
{
    private readonly IReadOnlyList<IUsbIpEventSink> sinks = sinks ?? [];

    public async ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        foreach (var sink in sinks)
        {
            try
            {
                await sink.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 日志接收器故障不能反向阻塞 USB 主链路。
            }
        }
    }
}

/// <summary>保留最近若干条事件，适合管理页面实时查看。</summary>
public sealed class MemoryUsbIpEventSink(int capacity = 1000) : IUsbIpEventSink
{
    private readonly ConcurrentQueue<UsbIpEvent> events = new();
    private readonly int capacity = Math.Max(1, capacity);

    public IReadOnlyList<UsbIpEvent> Snapshot() => events.ToArray();

    public ValueTask WriteAsync(UsbIpEvent evt, CancellationToken cancellationToken = default)
    {
        events.Enqueue(evt);
        while (events.Count > capacity) events.TryDequeue(out _);
        return ValueTask.CompletedTask;
    }
}
