using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class EmbeddedPayload
{
    private const string ManifestResource = "MyUsbIP.Setup.manifest.json";
    private const string BundleResource = "MyUsbIP.Setup.bundle.zip";
    internal const long MaxBundleBytes = 1024L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static SetupManifest ReadManifest()
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException("缺少内置安装清单，请使用 Build-WindowsSetups.ps1 生成安装器。");
        var manifest = JsonSerializer.Deserialize(input, SetupJsonContext.Default.SetupManifest)
            ?? throw new InvalidOperationException("内置安装清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.Role is not ("server" or "client") ||
            manifest.RuntimeIdentifier != "win-x64" || string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Size is <= 0 or > MaxBundleBytes)
            throw new InvalidOperationException("内置安装清单版本、角色、架构或大小无效。");
        ValidateFileName(manifest.FileName);
        ValidateHash(manifest.Sha256);
        ValidateHttpsUrl(manifest.DownloadUrl);
        return manifest;
    }

    public static string CreateWorkingDirectory()
    {
        // 每次安装使用随机私有工作目录，避免多个安装器互相删除载荷。
        var root = Path.Combine(Path.GetTempPath(), "MyUsbIP-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static async Task PrepareAsync(string workRoot, string executableDirectory, SetupManifest manifest,
        SetupOptions options, Action<string> write, CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var local = options.PackagePath ?? Path.Combine(executableDirectory, manifest.FileName);
        using var embedded = options.PackagePath is null && options.Mode != "online"
            ? assembly.GetManifestResourceStream(BundleResource) : null;
        var useLocal = options.Mode != "online" && (options.PackagePath is not null || File.Exists(local));
        var archivePath = Path.Combine(workRoot, "bundle.zip");
        var partialPath = archivePath + ".partial";
        if (useLocal)
        {
            write($"离线模式：{local}");
            using var input = File.OpenRead(local);
            await CopyBoundedAsync(input, partialPath, manifest.Size, cancellationToken);
        }
        else if (embedded is not null)
        {
            write("离线模式：使用内置安装包。");
            await CopyBoundedAsync(embedded, partialPath, manifest.Size, cancellationToken);
        }
        else
        {
            if (options.Mode == "offline")
                throw new FileNotFoundException("离线模式缺少安装包。请把对应 ZIP 放在安装器旁，或使用 --package 指定。", local);
            write($"在线模式：下载 {manifest.Version} {manifest.Role} 安装包...");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var url = ValidateHttpsUrl(manifest.DownloadUrl);
            // 手动限制 HTTPS 重定向，覆盖 GitHub Release 的 CDN 跳转。
            for (var redirects = 0; ; redirects++)
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 5 || response.Headers.Location is null)
                        throw new HttpRequestException("下载重定向过多或缺少地址。");
                    url = ValidateHttpsUrl(new Uri(url, response.Headers.Location).AbsoluteUri);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != manifest.Size)
                    throw new InvalidDataException("服务器返回的安装包大小与内置清单不一致。");
                using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                await CopyBoundedAsync(input, partialPath, manifest.Size, deadline.Token);
                break;
            }
        }
        VerifyFile(partialPath, manifest.Sha256, manifest.Size);
        File.Move(partialPath, archivePath);
        write("安装包 SHA256 校验通过，开始解包。");
        ExtractArchive(archivePath, workRoot, cancellationToken);
        File.Delete(archivePath);
    }

    internal static async Task CopyBoundedAsync(Stream input, string destination, long expectedSize, CancellationToken token)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > expectedSize || total > MaxBundleBytes) throw new InvalidDataException("安装包超过清单允许的大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != expectedSize) throw new InvalidDataException($"安装包下载/读取不完整：{total}/{expectedSize} bytes。");
    }

    internal static void VerifyFile(string path, string hash, long? expectedSize = null)
    {
        ValidateHash(hash);
        using var stream = File.OpenRead(path);
        if (expectedSize is long size && stream.Length != size) throw new InvalidDataException("安装文件大小校验失败。");
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA256 校验失败: {Path.GetFileName(path)}，期望={hash}，实际={actual}");
    }

    internal static void ExtractArchive(string archivePath, string destination, CancellationToken token = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("安装包文件数过多。");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"安装包包含不安全路径: {name}");
            if (name.Split('/').Any(x => IsReservedName(x)))
                throw new InvalidDataException($"安装包包含 Windows 保留名称: {name}");
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(target))
                throw new InvalidDataException($"安装包路径越界或重复: {name}");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("安装包解压后过大。");
            if (name.EndsWith('/')) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    copied += read;
                    if (copied > entry.Length) throw new InvalidDataException("ZIP 实际解压大小超过声明大小。");
                    output.Write(buffer, 0, read);
                }
                if (copied != entry.Length) throw new InvalidDataException("ZIP 文件解压不完整。");
            }
        }
    }

    private static bool IsReservedName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }

    internal static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || IsReservedName(name))
            throw new InvalidDataException($"清单包含无效文件名: {name}");
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("清单必须提供 64 位 SHA256。");
    }

    private static Uri ValidateHttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("安装包下载地址必须是无凭据的绝对 HTTPS 地址。");
        return uri;
    }

    public static void TryCleanupWorkingDirectory(string directory)
    {
        try
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent || !Path.GetFileName(directory).StartsWith("MyUsbIP-Setup-")) return;
            Directory.Delete(directory, recursive: true);
        }
        catch { /* 清理失败不覆盖安装结果。 */ }
    }
}
