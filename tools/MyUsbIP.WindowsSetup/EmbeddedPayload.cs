using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

internal static class EmbeddedPayload
{
    private const string ResourcePrefix = "MyUsbIP.Setup.";

    /// <summary>
    /// 角色专用安装包只内嵌本角色的 payload/dependency。优先从资源判断角色，
    /// 避免用户重命名 Setup.exe 后仅依赖文件名导致选择错误。
    /// dual 开发包同时包含两种资源时返回 null，继续使用交互/文件名兼容逻辑。
    /// </summary>
    public static string? DetectEmbeddedRole()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var hasServer = assembly.GetManifestResourceInfo(ResourcePrefix + "server-payload.zip") is not null
                        && assembly.GetManifestResourceInfo(ResourcePrefix + "server-dependency.bin") is not null;
        var hasClient = assembly.GetManifestResourceInfo(ResourcePrefix + "client-payload.zip") is not null
                        && assembly.GetManifestResourceInfo(ResourcePrefix + "client-dependency.bin") is not null;

        if (hasServer && !hasClient) return "server";
        if (hasClient && !hasServer) return "client";
        return null;
    }

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

    private static void ExtractResource(Assembly assembly, string resourceName, string destination)
    {
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"安装器缺少内置资源: {resourceName}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }
}
