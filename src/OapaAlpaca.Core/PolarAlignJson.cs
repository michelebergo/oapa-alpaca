using System.Text;
using System.Text.Json;

namespace OapaAlpaca.Core;

/// <summary>
/// The JSON of the PolarAlign actions (OpenAstroTech draft v0.1) for an OAPA board: Info and Status as
/// the board presents them (spec 3.3 and 3.4), and the parameters of Begin, Error and Move.
/// Numbers are always written with a dot; unknown fields in parameters are ignored.
/// </summary>
public static class PolarAlignJson {
    public const string SpecVersion = "0.1";

    public static string Info(DeviceInfo info, string name) =>
        Write(w => {
            w.WriteString("specVersion", SpecVersion);
            w.WriteStartArray("tiers");
            w.WriteNumberValue(2);
            w.WriteEndArray();
            w.WriteString("deviceName", name);
            w.WriteString("deviceVersion", info.FirmwareVersion);
            w.WriteStartObject("axes");
            foreach (var (axis, resolution) in new[] { ("az", info.ResolutionAz), ("alt", info.ResolutionAlt) }) {
                w.WriteStartObject(axis);
                w.WriteNumber("maxMove", info.MaxMove);
                w.WriteNumber("resolution", resolution ?? 0);
                w.WriteBoolean("backlashHandled", true);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteBoolean("simultaneousMoves", true);
            w.WriteBoolean("movesWhileTracking", true);
            w.WriteBoolean("movesWhileSlewing", false);
            w.WriteBoolean("canStop", true);
            w.WriteNumber("settleSeconds", 0);
        });

    public static string Status(DeviceStatus status) =>
        Write(w => {
            foreach (var (axis, s) in new[] { ("az", status.Az), ("alt", status.Alt) }) {
                w.WriteStartObject(axis);
                w.WriteBoolean("moving", s.Moving);
                if (s.Position is not null) {
                    w.WriteNumber("position", s.Position.Value);
                }
                w.WriteEndObject();
            }
            w.WriteStartObject("alignment");
            w.WriteString("phase", status.Alignment.Phase);
            w.WriteNumber("moves", status.Alignment.Moves);
            if (status.Alignment.Reason is not null) {
                w.WriteString("reason", status.Alignment.Reason);
            }
            w.WriteEndObject();
        });

    public static (double Tolerance, double? MaxMove) ParseBegin(string json) {
        using var doc = Parse(json);
        return (Required(doc.RootElement, "tolerance"), Optional(doc.RootElement, "maxMove"));
    }

    public static (double Az, double Alt) ParseError(string json) {
        using var doc = Parse(json);
        return (Required(doc.RootElement, "az"), Required(doc.RootElement, "alt"));
    }

    public static (string Axis, double Arcmin) ParseMove(string json) {
        using var doc = Parse(json);
        var axis = doc.RootElement.TryGetProperty("axis", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()!
            : throw Invalid("the parameters have no axis");
        return (axis, Required(doc.RootElement, "arcmin"));
    }

    private static JsonDocument Parse(string json) {
        try {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                doc.Dispose();
                throw Invalid("the parameters are not a JSON object");
            }
            return doc;
        } catch (JsonException e) {
            throw Invalid($"the parameters are not valid JSON: {e.Message}");
        }
    }

    private static double Required(JsonElement e, string name) =>
        Optional(e, name) ?? throw Invalid($"the parameters have no {name}");

    private static double? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DeviceException Invalid(string message) => new(DeviceErrorKind.InvalidValue, message);

    private static string Write(Action<Utf8JsonWriter> body) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
