using System.Text.Json.Serialization;

internal sealed class SetupManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

internal sealed class DependencyManifest
{
    public List<DependencyPackage> Packages { get; set; } = [];
}

internal sealed class DependencyPackage
{
    public string Role { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string InstallType { get; set; } = string.Empty;
}

// 安装器仅序列化明确的清单类型，不需要反射序列化元数据。
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SetupManifest))]
[JsonSerializable(typeof(DependencyManifest))]
internal partial class SetupJsonContext : JsonSerializerContext;
