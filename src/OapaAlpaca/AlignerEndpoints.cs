using System.Globalization;
using OapaAlpaca.Core;

namespace OapaAlpaca;

/// <summary>
/// The board as an Alpaca Aligner, after the ASCOM IAlignerV1 draft (ascom-standards.org/ialigner, 18/11/2026):
/// a polar aligner working in degrees, X azimuth and Y altitude, relative moves only. Positive X increases
/// the azimuth of the polar axis (clockwise seen from above), positive Y raises it: the board's own rule.
/// A failed move is reported by <c>ismoving</c> as a driver error, with the axis and the board's reason,
/// until the next move (a zero move will do), halt, findhome or reconnect.
/// </summary>
public static class AlignerEndpoints {
    public const string Prefix = "/api/v1/aligner/0/";
    public const int InterfaceVersion = 1;
    public const int ConfigurationVersion = 1;
    private const int PolarAligner = 1; // AlignerPurpose
    private const int Degrees = 1;      // AlignerUnits

    public static void Map(WebApplication app, OapaDevice device, string version) {
        CommonMembers.Map(app, Prefix, device, version, InterfaceVersion, Array.Empty<string>(),
            (action, _) => throw new DeviceException(DeviceErrorKind.ActionNotImplemented, $"{action} is not implemented: the PolarAlign actions are on the Switch device"));

        app.MapGet(Prefix + "configuration", (HttpRequest r) => Alpaca.Run(r, () => Configuration(device.Info)));
        app.MapGet(Prefix + "configurationversion", (HttpRequest r) => Alpaca.Run(r, () => ConfigurationVersion));
        app.MapGet(Prefix + "ismoving", (HttpRequest r) => Alpaca.Run(r, () => IsMoving(device.Status())));
        app.MapGet(Prefix + "xposition", (HttpRequest r) => Alpaca.Run(r, () => Position(device.Status().Az, "X")));
        app.MapGet(Prefix + "yposition", (HttpRequest r) => Alpaca.Run(r, () => Position(device.Status().Alt, "Y")));
        app.MapGet(Prefix + "zposition", (HttpRequest r) => Alpaca.Run(r, () => { _ = device.Info; throw NoZ(); }));

        app.MapPut(Prefix + "move", (HttpRequest r) => Alpaca.Run(r, async () => {
            var x = await Amount(r, "X");
            var y = await Amount(r, "Y");
            if (await Amount(r, "Z") != 0) {
                throw new DeviceException(DeviceErrorKind.InvalidValue, "Z must be 0: the device has no Z axis (CanMoveZ is false)");
            }
            device.Move(x * 60, y * 60);
            return null;
        }));
        app.MapPut(Prefix + "moveabsolute", (HttpRequest r) => Alpaca.Fail(r, Alpaca.NotImplemented, "moveabsolute is not implemented: the board has no absolute position (CanMoveAbsolute is false)"));
        app.MapPut(Prefix + "sync", (HttpRequest r) => Alpaca.Fail(r, Alpaca.NotImplemented, "sync is not implemented (CanSync is false)"));
        app.MapPut(Prefix + "setbacklashcompensation", (HttpRequest r) => Alpaca.Fail(r, Alpaca.NotImplemented, "setbacklashcompensation is not implemented: the board always compensates its backlash (CanSetBacklash is false)"));
        app.MapPut(Prefix + "halt", (HttpRequest r) => Alpaca.Run(r, () => { device.Stop(); return (object?)null; }));
        app.MapPut(Prefix + "findhome", (HttpRequest r) => Alpaca.Run(r, () => { device.Home(); return (object?)null; }));

        app.MapGet(Prefix + "devicestate", (HttpRequest r) => Alpaca.Run(r, () => {
            if (!device.Connected) {
                return Array.Empty<object>();
            }
            // The operational members only: IsMoving and the positions, each left out when its getter would throw.
            var state = new List<object> { new { Name = "TimeStamp", Value = (object)DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } };
            var status = device.Status();
            if (status.Az.Fault is null && status.Alt.Fault is null) {
                state.Add(new { Name = "IsMoving", Value = (object)IsMoving(status) });
            }
            if (status.Az.Position is { } x && status.Alt.Position is { } y) {
                state.Add(new { Name = "XPosition", Value = (object)(x / 60) });
                state.Add(new { Name = "YPosition", Value = (object)(y / 60) });
            }
            return state;
        }));
    }

    /// <summary>
    /// IAlignerConfigurationV1 in one object. Left out when the board does not know them, as the Alpaca
    /// definition allows: the travel limits, and the resolution until the board has calibration factors.
    /// The board takes up its own backlash on every reversal (the <c>$B=</c> values a host pushed), so
    /// compensation is always on and a client cannot switch it.
    /// </summary>
    private static Dictionary<string, object> Configuration(DeviceInfo info) {
        var c = new Dictionary<string, object> {
            ["AlignerPurpose"] = PolarAligner,
            ["BacklashCompensation"] = true,
            ["CanFindHome"] = true,
            ["CanMoveAbsolute"] = false,
            ["CanMoveX"] = true,
            ["CanMoveY"] = true,
            ["CanMoveZ"] = false,
            ["CanSetBacklash"] = false,
            ["CanSync"] = false,
            ["Units"] = Degrees,
            ["XMaxMove"] = info.MaxMove / 60,
            ["YMaxMove"] = info.MaxMove / 60
        };
        if (info.ResolutionAz is { } x) {
            c["XResolution"] = x / 60;
        }
        if (info.ResolutionAlt is { } y) {
            c["YResolution"] = y / 60;
        }
        return c;
    }

    private static bool IsMoving(DeviceStatus status) {
        var faults = new[] { (status.Az.Fault, "X"), (status.Alt.Fault, "Y") }.Where(f => f.Item1 is not null).Select(f => $"{f.Item2} axis: {f.Item1}").ToArray();
        if (faults.Length > 0) {
            throw new DeviceException(DeviceErrorKind.Driver, "the last move failed, " + string.Join("; ", faults) + " (a move, halt or findhome clears this)");
        }
        return status.Az.Moving || status.Alt.Moving;
    }

    private static double Position(AxisStatus axis, string name) =>
        axis.Position is { } arcmin ? arcmin / 60 : throw new DeviceException(DeviceErrorKind.NotImplemented, $"{name}Position: the board is not calibrated, so it has no position yet");

    private static async Task<double> Amount(HttpRequest r, string name) {
        var text = await Alpaca.Parameter(r, name);
        if (text is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) {
            throw new DeviceException(DeviceErrorKind.InvalidValue, $"{name} '{text}' is not a finite number of degrees");
        }
        return value;
    }

    private static DeviceException NoZ() => new(DeviceErrorKind.NotImplemented, "the device has no Z axis (CanMoveZ is false)");
}
