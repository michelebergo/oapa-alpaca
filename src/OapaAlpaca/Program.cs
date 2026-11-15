using System.Globalization;
using System.Reflection;
using OapaAlpaca;
using OapaAlpaca.Core;

var builder = WebApplication.CreateBuilder(args);
// Until the config file lands (setup page), the link comes from appsettings or the defaults below.
var linkType = builder.Configuration["Link:Type"] ?? "tcp";
var linkAddress = builder.Configuration["Link:Address"] ?? "192.168.1.40";
var deviceName = builder.Configuration["Device:Name"] ?? "OAPA";
var uniqueId = builder.Configuration["Device:UniqueId"] ?? "0a9a1f2e-0000-4000-8000-000000000001";
builder.Services.AddSingleton<IOapaLink>(_ => linkType == "serial" ? new SerialOapaLink(linkAddress) : new TcpOapaLink(linkAddress));
builder.Services.AddSingleton(s => new OapaDevice(s.GetRequiredService<IOapaLink>(), deviceName, SystemClock.Instance));
var app = builder.Build();

var device = app.Services.GetRequiredService<OapaDevice>();
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

double? Resolution(int id) => id == 0 ? device.Info.ResolutionAz : device.Info.ResolutionAlt;

double Position(int id) {
    var status = device.Status();
    var position = id == 0 ? status.Az.Position : status.Alt.Position;
    return position ?? throw new DeviceException(DeviceErrorKind.NotImplemented, "the board is not calibrated, so it has no position yet");
}

// Management
app.MapGet("/management/apiversions", (HttpRequest r) => Alpaca.Run(r, () => new[] { 1 }));
app.MapGet("/management/v1/description", (HttpRequest r) => Alpaca.Run(r, () =>
    new { ServerName = "OAPA Alpaca server", Manufacturer = "Michele Bergo", ManufacturerVersion = version, Location = Environment.MachineName }));
app.MapGet("/management/v1/configureddevices", (HttpRequest r) => Alpaca.Run(r, () =>
    new[] { new { DeviceName = device.Name, DeviceType = "Switch", DeviceNumber = 0, UniqueID = uniqueId } }));

// Common members
app.MapGet(Device + "connected", (HttpRequest r) => Alpaca.Run(r, () => device.Connected));
app.MapPut(Device + "connected", (HttpRequest r) => Alpaca.Run(r, async () => {
    var wanted = string.Equals(await Alpaca.Parameter(r, "Connected"), "true", StringComparison.OrdinalIgnoreCase);
    if (wanted) { device.Connect(); } else { device.Disconnect(); }
    return null;
}));
app.MapPut(Device + "connect", (HttpRequest r) => Alpaca.Run(r, () => { device.Connect(); return (object?)null; }));
app.MapPut(Device + "disconnect", (HttpRequest r) => Alpaca.Run(r, () => { device.Disconnect(); return (object?)null; }));
app.MapGet(Device + "connecting", (HttpRequest r) => Alpaca.Run(r, () => false));
app.MapGet(Device + "name", (HttpRequest r) => Alpaca.Run(r, () => device.Name));
app.MapGet(Device + "description", (HttpRequest r) => Alpaca.Run(r, () => "OAPA polar alignment base"));
app.MapGet(Device + "driverinfo", (HttpRequest r) => Alpaca.Run(r, () => $"OAPA Alpaca server {version}"));
app.MapGet(Device + "driverversion", (HttpRequest r) => Alpaca.Run(r, () => version));
app.MapGet(Device + "interfaceversion", (HttpRequest r) => Alpaca.Run(r, () => 3));
app.MapGet(Device + "supportedactions", (HttpRequest r) => Alpaca.Run(r, () => PolarAlignActions.Names));
app.MapGet(Device + "devicestate", (HttpRequest r) => Alpaca.Run(r, () => {
    if (!device.Connected) {
        return Array.Empty<object>();
    }
    var state = new List<object> { new { Name = "TimeStamp", Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } };
    if (device.Info.Calibrated) {
        var status = device.Status();
        state.Add(new { Name = SwitchName(0), Value = (object?)status.Az.Position });
        state.Add(new { Name = SwitchName(1), Value = (object?)status.Alt.Position });
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
app.MapGet(Device + "switchstep", (HttpRequest r) => Alpaca.Run(r, () => Resolution(SwitchId(r)) ?? 0));
app.MapGet(Device + "statechangecomplete", (HttpRequest r) => Alpaca.Run(r, () => { SwitchId(r); return true; }));
foreach (var write in new[] { "setswitchname", "setswitch", "setswitchvalue", "setasync", "setasyncvalue" }) {
    app.MapPut(Device + write, (HttpRequest r) => Alpaca.Fail(r, Alpaca.NotImplemented, $"{write} is not implemented: the switches are read only"));
}
app.MapFallback(Device + "{member}", (HttpRequest r, string member) => Alpaca.Fail(r, Alpaca.NotImplemented, $"{member} is not implemented"));

app.Run();

public partial class Program { }
