using System.Text.Json.Serialization;

// 配置保持原有大小写不敏感规则，不依赖反射发现配置成员。
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClientCliConfig))]
internal partial class ClientCliJsonContext : JsonSerializerContext;
