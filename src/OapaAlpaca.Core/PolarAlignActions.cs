namespace OapaAlpaca.Core;

/// <summary>The PolarAlign actions an OAPA board answers through <c>Action()</c>, and their dispatch to the device.</summary>
public static class PolarAlignActions {
    public const string Info = "PolarAlign:Info";
    public const string Status = "PolarAlign:Status";
    public const string Move = "PolarAlign:Move";
    public const string Stop = "PolarAlign:Stop";
    public const string Home = "PolarAlign:Home";
    public const string Begin = "PolarAlign:Begin";
    public const string Error = "PolarAlign:Error";
    public const string End = "PolarAlign:End";

    public static IReadOnlyList<string> Names { get; } = new[] { Info, Status, Move, Stop, Home, Begin, Error, End };

    /// <summary>Runs one action; returns its reply (empty where the draft has none). Names are case-insensitive.</summary>
    public static string Dispatch(OapaDevice device, string action, string parameters) {
        switch (action.ToLowerInvariant()) {
            case "polaralign:info":
                return PolarAlignJson.Info(device.Info, device.Name, device.OfferTier2);
            case "polaralign:status":
                return PolarAlignJson.Status(device.Status());
            case "polaralign:move": {
                var (axis, arcmin) = PolarAlignJson.ParseMove(parameters);
                return PolarAlignJson.MoveReply(device.Move(axis, arcmin));
            }
            case "polaralign:stop":
                // One axis cannot stop alone on an OAPA board: both axes stop (spec 4.2).
                device.Stop();
                return "";
            case "polaralign:home":
                device.Home();
                return "";
            case "polaralign:begin": {
                var (tolerance, maxMove) = PolarAlignJson.ParseBegin(parameters);
                device.Begin(tolerance, maxMove);
                return "";
            }
            case "polaralign:error": {
                var (az, alt) = PolarAlignJson.ParseError(parameters);
                device.Error(az, alt);
                return "";
            }
            case "polaralign:end":
                device.End();
                return "";
            default:
                throw new DeviceException(DeviceErrorKind.ActionNotImplemented, $"{action} is not implemented");
        }
    }
}
