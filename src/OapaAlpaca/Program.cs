using System.Globalization;
using System.Reflection;
using OapaAlpaca;
using OapaAlpaca.Core;

var builder = WebApplication.CreateBuilder(args);
// config.json next to the executable (ConfigPath overrides it: the tests point it at a temp file).
var configPath = builder.Configuration["ConfigPath"] ?? Path.Combine(AppContext.BaseDirectory, "config.json");
var config = ServerConfig.Load(configPath);
builder.Services.AddSingleton<IOapaLink>(_ => ServerConfig.CreateLink(config));
builder.Services.AddSingleton(s => new OapaDevice(s.GetRequiredService<IOapaLink>(), config.Name, SystemClock.Instance));
if (!builder.Environment.IsEnvironment("Testing")) {
    builder.Services.AddHostedService<AlpacaDiscovery>();
}
var app = builder.Build();
Alpaca.Logger = app.Logger;
app.UseDefaultFiles();
app.UseStaticFiles();

var device = app.Services.GetRequiredService<OapaDevice>();
var uniqueId = config.UniqueId;
// The Aligner is the same board under a second device type; its UniqueID is derived from the Switch's so both are stable.
var alignerUniqueId = DerivedId(uniqueId);
var configLock = new object();
device.OfferTier2 = config.OfferTier2;

// Setup page API
app.MapGet("/api/config", () => Results.Json(config, ServerConfigJson.Options));
app.MapPost("/api/config", (ServerConfig.StoredConfig posted) => {
    if (!ServerConfig.IsValidLink(posted.Link ?? "") || string.IsNullOrWhiteSpace(posted.Address)) {
        return Results.BadRequest("link must be serial or tcp, and address must not be empty");
    }
    lock (configLock) {
        config = config with { Link = posted.Link!, Address = posted.Address!.Trim(), Name = string.IsNullOrWhiteSpace(posted.Name) ? config.Name : posted.Name!.Trim(), OfferTier2 = posted.OfferTier2 ?? config.OfferTier2 };
        config.Save(configPath);
        device.OfferTier2 = config.OfferTier2; // read at the next PolarAlign:Info, no reconnect needed
        var applied = !device.Connected;
        if (applied) {
            device.UseLink(ServerConfig.CreateLink(config));
        }
        return Results.Json(new { applied }, ServerConfigJson.Options);
    }
});
app.MapGet("/api/state", () => Results.Json(new {
    connected = device.Connected,
    link = device.LinkDescription,
    firmware = device.Connected ? device.Info.FirmwareVersion : null,
    calibrated = device.Connected ? device.Info.Calibrated : (bool?)null,
    lastError = Alpaca.LastError
}, ServerConfigJson.Options));
app.MapGet("/api/ports", () => Results.Json(System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p).ToArray(), ServerConfigJson.Options));
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
const string Device = "/api/v1/switch/0/";
const int SwitchCount = 2;

// Two read-only switches carry the axis positions, so a client that knows nothing about the actions still shows something.
static string SwitchName(int id) => id == 0 ? "Azimuth position (arcmin)" : "Altitude position (arcmin)";

int SwitchId(HttpRequest r) {
    var text = Alpaca.Parameter(r, "Id").GetAwaiter().GetResult();
    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0 || id >= SwitchCount) {
        throw new DeviceException(DeviceErrorKind.InvalidValue, $"Id {text} is out of range 0..{SwitchCount - 1}");
    }
    return id;
}

double Position(int id) {
    var status = device.Status();
    var position = id == 0 ? status.Az.Position : status.Alt.Position;
    return position ?? throw new DeviceException(DeviceErrorKind.NotImplemented, "the board is not calibrated, so it has no position yet");
}

// Management
app.MapGet("/management/apiversions", (HttpRequest r) => Alpaca.Run(r, () => new[] { 1 }));
app.MapGet("/management/v1/description", (HttpRequest r) => Alpaca.Run(r, () =>
    new { ServerName = "OAPA Alpaca server", Manufacturer = "Michele Bergo", ManufacturerVersion = version, Location = Environment.MachineName }));
app.MapGet("/management/v1/configureddevices", (HttpRequest r) => Alpaca.Run(r, () => new[] {
    new { DeviceName = device.Name, DeviceType = "Switch", DeviceNumber = 0, UniqueID = uniqueId },
    new { DeviceName = device.Name, DeviceType = "Aligner", DeviceNumber = 0, UniqueID = alignerUniqueId }
}));

// Common members of the Switch (interface version 3), with the PolarAlign actions
CommonMembers.Map(app, Device, device, version, 3, PolarAlignActions.Names, (action, parameters) => PolarAlignActions.Dispatch(device, action, parameters));
app.MapGet(Device + "devicestate", (HttpRequest r) => Alpaca.Run(r, () => {
    if (!device.Connected) {
        return Array.Empty<object>();
    }
    // ISwitchV3 DeviceState carries only the operational members, under their member names.
    var state = new List<object> { new { Name = "TimeStamp", Value = (object)DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } };
    if (device.Info.Calibrated) {
        var status = device.Status();
        var positions = new[] { status.Az.Position, status.Alt.Position };
        for (var id = 0; id < SwitchCount; id++) {
            state.Add(new { Name = $"GetSwitch{id}", Value = (object)false });
            state.Add(new { Name = $"GetSwitchValue{id}", Value = (object)(positions[id] ?? 0) });
            state.Add(new { Name = $"StateChangeComplete{id}", Value = (object)true });
        }
    }
    return state;
}));

// Switch members
app.MapGet(Device + "maxswitch", (HttpRequest r) => Alpaca.Run(r, () => { _ = device.Info; return SwitchCount; }));
app.MapGet(Device + "getswitchname", (HttpRequest r) => Alpaca.Run(r, () => SwitchName(SwitchId(r))));
app.MapGet(Device + "getswitchdescription", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return "Read only"; }));
app.MapGet(Device + "canwrite", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return false; }));
app.MapGet(Device + "canasync", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return false; }));
app.MapGet(Device + "getswitch", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return false; }));
app.MapGet(Device + "getswitchvalue", (HttpRequest r) => Alpaca.Run(r, () => Position(SwitchId(r))));
app.MapGet(Device + "minswitchvalue", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return -10000.0; }));
app.MapGet(Device + "maxswitchvalue", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return 10000.0; }));
// The switch step is a display granularity, not the motor resolution (that is Info.resolution): 1/64 arcminute
// is exactly representable, so the ±10000 range is an exact multiple and ConformU's remainder check passes.
app.MapGet(Device + "switchstep", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return 1.0 / 64; }));
app.MapGet(Device + "statechangecomplete", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return true; }));
// Writes are refused as NotImplemented (the switches are read only), but an Id out of range is an InvalidValue first.
foreach (var write in new[] { "setswitchname", "setswitch", "setswitchvalue", "setasync", "setasyncvalue", "cancelasync" }) {
    app.MapPut(Device + write, (HttpRequest r) => Alpaca.Run(r, () => {
        _ = device.Info;
        SwitchId(r);
        throw new DeviceException(DeviceErrorKind.NotImplemented, $"{write} is not implemented: the switches are read only");
    }));
}
// The same board as an Aligner (ASCOM IAlignerV1 draft), in degrees
AlignerEndpoints.Map(app, device, version);

app.Run();

// A second GUID that follows the first: the last byte flipped.
static string DerivedId(string guid) {
    var bytes = Guid.Parse(guid).ToByteArray();
    bytes[15] ^= 0x01;
    return new Guid(bytes).ToString();
}

public partial class Program { }

/// <summary>camelCase JSON for the setup page, unlike the PascalCase of the Alpaca envelope.</summary>
public static class ServerConfigJson {
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}
