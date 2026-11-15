using System.Text.Json;
using System.Text.Json.Serialization;
using OapaAlpaca.Core;

namespace OapaAlpaca;

/// <summary>
/// The server's settings in <c>config.json</c>: how to reach the board, the device name, and the Alpaca
/// <c>UniqueID</c>, generated once on the first start and kept, so clients recognise the device across restarts.
/// </summary>
public sealed record ServerConfig(string Link, string Address, string Name, string UniqueId) {
    public const string Serial = "serial";
    public const string Tcp = "tcp";

    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ServerConfig Defaults() => new(Tcp, "192.168.1.40", "OAPA", Guid.NewGuid().ToString());

    public static bool IsValidLink(string link) => link is Serial or Tcp;

    public static ServerConfig Load(string path) {
        if (File.Exists(path)) {
            var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(path), Json) ?? new StoredConfig();
            var defaults = Defaults();
            var config = new ServerConfig(
                IsValidLink(stored.Link ?? "") ? stored.Link! : defaults.Link,
                string.IsNullOrWhiteSpace(stored.Address) ? defaults.Address : stored.Address!,
                string.IsNullOrWhiteSpace(stored.Name) ? defaults.Name : stored.Name!,
                Guid.TryParse(stored.UniqueId, out _) ? stored.UniqueId! : defaults.UniqueId);
            if (config.UniqueId != stored.UniqueId) {
                config.Save(path);
            }
            return config;
        }
        var fresh = Defaults();
        fresh.Save(path);
        return fresh;
    }

    public void Save(string path) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new StoredConfig { Link = Link, Address = Address, Name = Name, UniqueId = UniqueId }, Json));
    }

    public static IOapaLink CreateLink(ServerConfig config) =>
        config.Link == Serial ? new SerialOapaLink(config.Address) : new TcpOapaLink(config.Address);

    /// <summary>The file's shape; every field optional so an edited or older file still loads.</summary>
    public sealed class StoredConfig {
        public string? Link { get; set; }
        public string? Address { get; set; }
        public string? Name { get; set; }
        public string? UniqueId { get; set; }
    }
}
