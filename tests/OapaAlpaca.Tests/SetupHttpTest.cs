using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OapaAlpaca.Core.Tests;

namespace OapaAlpaca.Tests;

public class SetupHttpTest {
    private static async Task<JsonElement> GetJson(HttpClient client, string url) {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Test]
    public async Task Post_config_is_saved_and_applies_at_the_next_connect() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/config", new { link = "tcp", address = "10.0.0.5", name = "OAPA bench" });
        response.EnsureSuccessStatusCode();
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("applied").GetBoolean().Should().BeTrue();
        var saved = JsonDocument.Parse(File.ReadAllText(factory.ConfigPath)).RootElement;
        saved.GetProperty("address").GetString().Should().Be("10.0.0.5");
        saved.GetProperty("name").GetString().Should().Be("OAPA bench");
        (await GetJson(client, "/api/state")).GetProperty("link").GetString().Should().Be("tcp://10.0.0.5:2323");
        (await GetJson(client, "/api/config")).GetProperty("uniqueId").GetString().Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Post_config_while_connected_is_saved_but_not_applied() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        var response = await client.PostAsJsonAsync("/api/config", new { link = "serial", address = "COM9", name = "OAPA" });
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("applied").GetBoolean().Should().BeFalse();
        JsonDocument.Parse(File.ReadAllText(factory.ConfigPath)).RootElement.GetProperty("address").GetString().Should().Be("COM9");
        (await GetJson(client, "/api/state")).GetProperty("link").GetString().Should().Be("scripted");
    }

    [Test]
    public async Task A_bad_link_type_is_refused() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/config", new { link = "bluetooth", address = "x", name = "OAPA" });
        ((int)response.StatusCode).Should().Be(400);
    }

    [Test]
    public async Task State_reports_connection_and_firmware() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var before = await GetJson(client, "/api/state");
        before.GetProperty("connected").GetBoolean().Should().BeFalse();
        await client.Connect();
        var after = await GetJson(client, "/api/state");
        after.GetProperty("connected").GetBoolean().Should().BeTrue();
        after.GetProperty("firmware").GetString().Should().Be("1.3.6");
        after.GetProperty("calibrated").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task State_carries_the_last_error() {
        using var factory = new ServerFactory();
        factory.Link.Expect("$P?", "ok");
        var client = factory.CreateClient();
        await client.Connect();
        (await GetJson(client, "/api/state")).GetProperty("lastError").GetString().Should().Contain("not an OAPA alignment device");
    }

    [Test]
    public async Task Ports_lists_the_serial_ports() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await GetJson(client, "/api/ports")).ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public async Task The_setup_page_is_served() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        html.Should().Contain("OAPA").And.Contain("/api/config");
    }
}
