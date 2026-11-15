using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OapaAlpaca.Core;
using OapaAlpaca.Core.Tests;

namespace OapaAlpaca.Tests;

/// <summary>The server in memory, talking to a scripted board instead of a real link.</summary>
public sealed class ServerFactory : WebApplicationFactory<Program> {
    public ScriptedLink Link { get; } = new();

    /// <summary>A config file of its own per factory, in the temp folder, so tests never touch a real one.</summary>
    public string ConfigPath { get; } = Path.Combine(Path.GetTempPath(), $"oapa-alpaca-test-{Guid.NewGuid():N}.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConfigPath", ConfigPath);
        builder.ConfigureServices(services => {
            services.AddSingleton<IOapaLink>(Link);
        });
    }

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        File.Delete(ConfigPath);
    }

    /// <summary>A factory whose board answers the whole connect sequence of a calibrated board with <c>$R=</c>.</summary>
    public static ServerFactory WithCalibratedBoard(string calibration = OapaDeviceConnectTest.CalibratedK, string relative = OapaDeviceConnectTest.IdleR) {
        var factory = new ServerFactory();
        factory.Link.Expect("$P?", "<P|protocol:1|>")
            .Expect("?", OapaDeviceConnectTest.IdleStatus, "ok")
            .Expect("$M?", "<M|max:180|>")
            .Expect("$R?", relative)
            .Expect("$K?", calibration);
        return factory;
    }
}

public static class AlpacaClient {
    public const string Device = "/api/v1/switch/0/";

    public static async Task<JsonElement> Get(this HttpClient client, string member, string query = "") {
        var response = await client.GetAsync(Device + member + (query.Length > 0 ? "?" + query : ""));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public static async Task<JsonElement> Put(this HttpClient client, string member, params (string Key, string Value)[] form) {
        var response = await client.PutAsync(Device + member, new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public static async Task<JsonElement> Connect(this HttpClient client) => await client.Put("connected", ("Connected", "true"));

    public static int ErrorNumber(this JsonElement e) => e.GetProperty("ErrorNumber").GetInt32();
}
