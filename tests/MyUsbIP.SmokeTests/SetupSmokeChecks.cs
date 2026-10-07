using System.IO.Compression;
using System.Security.Cryptography;

internal static class SetupSmokeChecks
{
    public static async Task RunAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "myusbip-setup-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = SetupOptions.Parse(["--mode", "offline", "--quiet", "--timeout-seconds", "7"]);
            Check(options is { Mode: "offline", Quiet: true, TimeoutSeconds: 7 }, "安装器参数解析错误", failures);
            foreach (var arguments in new string[][] { ["--mode", "unknown"], ["--mode"], ["--timeout-seconds", "0"],
                ["--mode", "online", "--package", "test.zip"], ["--unknown"] })
                ExpectFailure<ArgumentException>(() => SetupOptions.Parse(arguments), "安装器未拒绝错误参数", failures);
            foreach (var name in new[] { "../bad.exe", "C:bad.exe", "bad\\file.exe", "CON.exe", "driver.msi.", ".." })
                ExpectFailure<InvalidDataException>(() => EmbeddedPayload.ValidateFileName(name), "安装器未拒绝危险文件名", failures);

            var zip = Path.Combine(root, "bundle.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("payload/client/clientsettings.json").Open());
                writer.Write("{\"message\":\"中文配置\"}");
            }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
            EmbeddedPayload.VerifyFile(zip, hash.ToLowerInvariant(), new FileInfo(zip).Length);
            ExpectFailure<InvalidDataException>(() => EmbeddedPayload.VerifyFile(zip, new string('0', 64)), "安装器未拒绝哈希不匹配", failures);
            ExpectFailure<InvalidDataException>(() => EmbeddedPayload.VerifyFile(zip, hash, 1), "安装器未拒绝大小不匹配", failures);
            var manifest = new SetupManifest { Role = "client", FileName = "bundle.zip", Sha256 = hash, Size = new FileInfo(zip).Length,
                DownloadUrl = "https://invalid.invalid/should-never-be-requested" };
            var work = Path.Combine(root, "prepare");
            Directory.CreateDirectory(work);
            await EmbeddedPayload.PrepareAsync(work, root, manifest, new("offline", zip, true, true, 1), _ => { }, default);
            Check(File.ReadAllText(Path.Combine(work, "payload/client/clientsettings.json")).Contains("中文配置"), "离线准备丢失载荷", failures);
            Check(!File.Exists(Path.Combine(work, "bundle.zip.partial")) && !File.Exists(Path.Combine(work, "bundle.zip")), "离线准备未清理归档文件", failures);
            try
            {
                await EmbeddedPayload.PrepareAsync(work, root, manifest, new("offline", Path.Combine(root, "missing.zip"), true, true, 1), _ => { }, default);
                failures.Add("指定缺失离线包应直接失败，不能联网");
            }
            catch (FileNotFoundException) { }
            foreach (var name in new[] { "../escape.txt", "..\\escape.txt", "/escape.txt", "C:/escape.txt", "payload/file:ads", "CON.txt", "payload/dir./x" })
            {
                var unsafeZip = Path.Combine(root, "unsafe.zip");
                File.Delete(unsafeZip);
                using (var archive = ZipFile.Open(unsafeZip, ZipArchiveMode.Create)) archive.CreateEntry(name);
                ExpectFailure<InvalidDataException>(() => EmbeddedPayload.ExtractArchive(unsafeZip, Path.Combine(root, "unsafe")),
                    $"安装器未拒绝危险 ZIP 路径: {name}", failures);
            }
            var duplicateZip = Path.Combine(root, "duplicate.zip");
            using (var archive = ZipFile.Open(duplicateZip, ZipArchiveMode.Create))
            {
                archive.CreateEntry("payload/a");
                archive.CreateEntry("payload/A");
            }
            ExpectFailure<InvalidDataException>(() => EmbeddedPayload.ExtractArchive(duplicateZip, Path.Combine(root, "duplicate")), "安装器未拒绝 Windows 重复路径", failures);
            var symlinkZip = Path.Combine(root, "symlink.zip");
            using (var archive = ZipFile.Open(symlinkZip, ZipArchiveMode.Create)) archive.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000);
            ExpectFailure<InvalidDataException>(() => EmbeddedPayload.ExtractArchive(symlinkZip, Path.Combine(root, "symlink")), "安装器未拒绝符号链接", failures);
            foreach (var length in new[] { 2, 4 })
            {
                try
                {
                    await EmbeddedPayload.CopyBoundedAsync(new MemoryStream(new byte[length]), Path.Combine(root, $"stream-{length}"), 3, default);
                    failures.Add("安装器未拒绝截断/超额下载");
                }
                catch (InvalidDataException) { }
            }
            // 可选网络集成：用已发布的固定 ZIP 验证真实 GitHub HTTPS/CDN 下载与哈希流程。
            if (Environment.GetEnvironmentVariable("MYUSBIP_SETUP_DOWNLOAD_TEST") == "1")
            {
                var download = Path.Combine(root, "download");
                Directory.CreateDirectory(download);
                var published = new SetupManifest { Role = "server", FileName = "MyUsbIP-Server-win-x64.zip", Version = "1.1.15",
                    DownloadUrl = "https://github.com/sunkejava/MyUsbIP/releases/download/v1.1.15/MyUsbIP-Server-win-x64.zip",
                    Size = 38735760, Sha256 = "3fd28be79df7a9fd8e89f1ca43a03189f0dd2211a89d005220aa59064ef717b4" };
                await EmbeddedPayload.PrepareAsync(download, root, published, new("online", null, true, true, 180), Console.WriteLine, default);
                Check(File.Exists(Path.Combine(download, "payload/server/myusbipd.exe")), "在线下载解包缺少服务端入口", failures);
            }
        }
        catch (Exception ex) { failures.Add("安装器 smoke 失败: " + ex); }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
    }
    private static void ExpectFailure<T>(Action action, string message, List<string> failures) where T : Exception
    {
        try { action(); failures.Add(message); }
        catch (T) { }
    }
}
