using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

internal static class EmbeddedPayload
{
    private const string ResourcePrefix = "MyUsbIP.Setup.";

    public static string PrepareWorkingDirectory(string fallbackBaseDirectory, string role)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var manifestName = ResourcePrefix + "dependencies.windows.json";
        if (assembly.GetManifestResourceInfo(manifestName) is null)
            return fallbackBaseDirectory;

        var workRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MyUsbIP",
            "SetupCache",
            role);

        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
        Directory.CreateDirectory(workRoot);

        var configDir = Path.Combine(workRoot, "config");
        var cacheDir = Path.Combine(workRoot, "dependencies", "cache");
        var payloadDir = Path.Combine(workRoot, "payload", role);
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(payloadDir);

        var manifestPath = Path.Combine(configDir, "dependencies.windows.json");
        ExtractResource(assembly, manifestName, manifestPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var package = doc.RootElement.GetProperty("packages")
            .EnumerateArray()
            .FirstOrDefault(x =>
                x.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean() &&
                x.TryGetProperty("role", out var packageRole) &&
                string.Equals(packageRole.GetString(), role, StringComparison.OrdinalIgnoreCase));

        if (package.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"内置依赖清单中未找到 {role} 包。");

        var fileName = package.GetProperty("fileName").GetString();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException($"内置依赖清单中的 {role} fileName 为空。");

        ExtractResource(
            assembly,
            ResourcePrefix + role + "-dependency.bin",
            Path.Combine(cacheDir, fileName));

        var payloadZip = Path.Combine(workRoot, role + "-payload.zip");
        ExtractResource(assembly, ResourcePrefix + role + "-payload.zip", payloadZip);
        ZipFile.ExtractToDirectory(payloadZip, payloadDir, overwriteFiles: true);
        File.Delete(payloadZip);

        return workRoot;
    }

    public static void TryCleanupWorkingDirectory(string baseDirectory, string role)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MyUsbIP",
                "SetupCache",
                role));
            var actual = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            expected = expected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 只允许删除安装器自己创建的 ProgramData SetupCache 目录。
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
        }
        catch
        {
            // 安装已经成功时，缓存清理失败不应反向把安装判定为失败。
        }
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string destination)
    {
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"安装器缺少内置资源: {resourceName}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }
}
