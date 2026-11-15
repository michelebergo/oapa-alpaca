using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Tests;

public class ServerConfigTest {
    private string path = "";

    [SetUp]
    public void TempPath() => path = Path.Combine(Path.GetTempPath(), $"oapa-alpaca-test-{Guid.NewGuid():N}.json");

    [TearDown]
    public void Cleanup() => File.Delete(path);

    [Test]
    public void A_missing_file_yields_defaults_with_a_fresh_unique_id_and_saves_it() {
        var config = ServerConfig.Load(path);
        config.Link.Should().Be("tcp");
        config.Address.Should().Be("192.168.1.40");
        config.Name.Should().Be("OAPA");
        Guid.TryParse(config.UniqueId, out _).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        ServerConfig.Load(path).UniqueId.Should().Be(config.UniqueId);
    }

    [Test]
    public void Save_then_load_round_trips() {
        var config = new ServerConfig("serial", "COM7", "OAPA bench", Guid.NewGuid().ToString());
        config.Save(path);
        ServerConfig.Load(path).Should().Be(config);
    }

    [Test]
    public void Create_link_picks_serial_or_tcp() {
        ServerConfig.CreateLink(new ServerConfig("serial", "COM7", "OAPA", "id")).Should().BeOfType<SerialOapaLink>().Which.Description.Should().Be("COM7");
        ServerConfig.CreateLink(new ServerConfig("tcp", "10.0.0.5", "OAPA", "id")).Should().BeOfType<TcpOapaLink>().Which.Description.Should().Be("tcp://10.0.0.5:2323");
    }
}
