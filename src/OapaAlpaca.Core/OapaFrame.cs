using System.Globalization;

namespace OapaAlpaca.Core;

/// <summary>
/// A structured reply of the OAPA protocol: <c>&lt;Name|key:value|...|&gt;</c>. The first field names
/// the reply; every other field splits on its first colon, so a value may contain colons.
/// </summary>
public sealed record OapaFrame(string Name, IReadOnlyDictionary<string, string> Fields) {
    public static bool TryParse(string line, out OapaFrame frame) {
        frame = null!;
        var text = line.Trim();
        if (!text.StartsWith('<') || !text.EndsWith("|>", StringComparison.Ordinal)) {
            return false;
        }
        var parts = text[1..^2].Split('|');
        if (parts.Length == 0 || parts[0].Length == 0) {
            return false;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1)) {
            var colon = part.IndexOf(':');
            if (colon < 0) {
                fields[part] = "";
            } else {
                fields[part[..colon]] = part[(colon + 1)..];
            }
        }
        frame = new OapaFrame(parts[0], fields);
        return true;
    }

    public string Field(string key) => Fields.TryGetValue(key, out var value) ? value : "";

    internal static double ParseNumber(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>The reply to <c>?</c>: machine state, motor positions in steps and the firmware version.</summary>
public sealed record StatusFrame(string State, long X, long Y, string? Version) {
    public static StatusFrame Parse(OapaFrame frame) {
        var positions = frame.Field("MPos").Split(',');
        var x = positions.Length > 0 ? (long)Math.Round(OapaFrame.ParseNumber(positions[0])) : 0;
        var y = positions.Length > 1 ? (long)Math.Round(OapaFrame.ParseNumber(positions[1])) : 0;
        var version = frame.Fields.TryGetValue("V", out var v) && v.Length > 0 ? v : null;
        return new StatusFrame(frame.Name, x, y, version);
    }
}

/// <summary>The reply to <c>$K?</c>: whether the board has factors, and steps per arcminute with the sign per axis.</summary>
public sealed record Calibration(bool Done, double? FactorX, int SignX, double? FactorY, int SignY) {
    public static Calibration Parse(OapaFrame frame) {
        var (fx, sx) = ParseAxis(frame.Field("x"));
        var (fy, sy) = ParseAxis(frame.Field("y"));
        return new Calibration(frame.Field("state") == "done", fx, sx, fy, sy);
    }

    private static (double? Factor, int Sign) ParseAxis(string text) {
        if (text.Length == 0 || text == "-") {
            return (null, 0);
        }
        var parts = text.Split(',');
        var factor = OapaFrame.ParseNumber(parts[0]);
        var sign = parts.Length > 1 ? Math.Sign(OapaFrame.ParseNumber(parts[1])) : 0;
        return (factor, sign);
    }
}

/// <summary>The reply to <c>$L?</c>: the board's own alignment loop.</summary>
public sealed record AlignmentState(string Phase, string Outcome, int Moves, string Reason) {
    public static AlignmentState Parse(OapaFrame frame) {
        var moves = int.TryParse(frame.Field("moves"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return new AlignmentState(frame.Field("phase"), frame.Field("outcome"), moves, frame.Field("reason"));
    }
}
