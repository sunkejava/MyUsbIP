namespace MyUsbIP.Abstractions;

/// <summary>日志文件维护工具。</summary>
public static class LogFileMaintenance
{
    /// <summary>
    /// 删除指定目录中超过保留天数的日志文件。维护失败不会影响 USB 主链路。
    /// </summary>
    public static void Cleanup(string directory, string searchPattern, int retentionDays)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var threshold = DateTime.UtcNow.AddDays(-retentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold) File.Delete(file);
                }
                catch
                {
                    // 单个文件被占用或无权限时跳过，不能影响 USB 业务。
                }
            }
        }
        catch
        {
            // 日志维护异常不影响主流程。
        }
    }
}
