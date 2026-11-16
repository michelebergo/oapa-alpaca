using OapaAlpaca.Core;

namespace OapaAlpaca;

/// <summary>
/// The members every Alpaca device type shares, mapped under one device prefix. The Switch and the Aligner
/// are the same board, so connecting one connects the other.
/// </summary>
public static class CommonMembers {
    public static void Map(WebApplication app, string prefix, OapaDevice device, string version, int interfaceVersion,
                           IReadOnlyList<string> actions, Func<string, string, string> dispatch) {
        app.MapGet(prefix + "connected", (HttpRequest r) => Alpaca.Run(r, () => device.Connected));
        app.MapPut(prefix + "connected", (HttpRequest r) => Alpaca.Run(r, async () => {
            var wanted = string.Equals(await Alpaca.Parameter(r, "Connected"), "true", StringComparison.OrdinalIgnoreCase);
            if (wanted) { device.Connect(); } else { device.Disconnect(); }
            return null;
        }));
        app.MapPut(prefix + "connect", (HttpRequest r) => Alpaca.Run(r, () => { device.Connect(); return (object?)null; }));
        app.MapPut(prefix + "disconnect", (HttpRequest r) => Alpaca.Run(r, () => { device.Disconnect(); return (object?)null; }));
        app.MapGet(prefix + "connecting", (HttpRequest r) => Alpaca.Run(r, () => false));
        app.MapGet(prefix + "name", (HttpRequest r) => Alpaca.Run(r, () => device.Name));
        app.MapGet(prefix + "description", (HttpRequest r) => Alpaca.Run(r, () => "OAPA polar alignment base"));
        app.MapGet(prefix + "driverinfo", (HttpRequest r) => Alpaca.Run(r, () => $"OAPA Alpaca server {version}"));
        app.MapGet(prefix + "driverversion", (HttpRequest r) => Alpaca.Run(r, () => version));
        app.MapGet(prefix + "interfaceversion", (HttpRequest r) => Alpaca.Run(r, () => interfaceVersion));
        app.MapGet(prefix + "supportedactions", (HttpRequest r) => Alpaca.Run(r, () => actions));
        app.MapPut(prefix + "action", (HttpRequest r) => Alpaca.Run(r, async () => {
            var action = await Alpaca.Parameter(r, "Action") ?? "";
            var parameters = await Alpaca.Parameter(r, "Parameters") ?? "";
            return dispatch(action, parameters);
        }));
        // Everything else under this device, the raw command members included, is not implemented.
        app.MapFallback(prefix + "{member}", (HttpRequest r, string member) => Alpaca.Fail(r, Alpaca.NotImplemented, $"{member} is not implemented"));
    }
}
