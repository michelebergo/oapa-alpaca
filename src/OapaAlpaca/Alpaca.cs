using System.Text.Json;
using OapaAlpaca.Core;

namespace OapaAlpaca;

/// <summary>
/// The Alpaca envelope and error numbers. Every member answers through <see cref="Run"/>: the value wrapped
/// in the envelope, a <see cref="DeviceException"/> as an Alpaca error, and a lost link as a failed request.
/// </summary>
public static class Alpaca {
    public const int NotImplemented = 0x400;
    public const int InvalidValue = 0x401;
    public const int NotConnected = 0x407;
    public const int InvalidOperation = 0x40B;
    public const int ActionNotImplemented = 0x40C;
    public const int DriverError = 0x500;

    // Alpaca names its fields in PascalCase; ASP.NET's web defaults would write camelCase.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };
    private static uint serverTransactionId;

    public static int ErrorNumber(DeviceErrorKind kind) => kind switch {
        DeviceErrorKind.NotImplemented => NotImplemented,
        DeviceErrorKind.InvalidValue => InvalidValue,
        DeviceErrorKind.NotConnected => NotConnected,
        DeviceErrorKind.InvalidOperation => InvalidOperation,
        DeviceErrorKind.ActionNotImplemented => ActionNotImplemented,
        _ => DriverError
    };

    /// <summary>Runs a member body. A body that returns null answers an envelope without a Value (a method).</summary>
    public static async Task<IResult> Run(HttpRequest request, Func<Task<object?>> body) {
        var clientId = await ClientTransactionId(request);
        try {
            return Json(Envelope(await body(), clientId, 0, ""));
        } catch (DeviceException e) when (e.Kind == DeviceErrorKind.Link) {
            // A dead link never answers with an Alpaca error: the request itself fails (spec 4.2).
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        } catch (DeviceException e) {
            return Json(Envelope(null, clientId, ErrorNumber(e.Kind), e.Message));
        }
    }

    public static Task<IResult> Run(HttpRequest request, Func<object?> body) => Run(request, () => Task.FromResult(body()));

    public static Task<IResult> Fail(HttpRequest request, int number, string message) =>
        Run(request, () => throw new DeviceException(KindOf(number), message));

    public static IResult Json(object envelope) => Results.Json(envelope, JsonOptions);

    public static object Envelope(object? value, uint clientId, int number, string message) {
        var serverId = Interlocked.Increment(ref serverTransactionId);
        return value is null
            ? new { ClientTransactionID = clientId, ServerTransactionID = serverId, ErrorNumber = number, ErrorMessage = message }
            : new { Value = value, ClientTransactionID = clientId, ServerTransactionID = serverId, ErrorNumber = number, ErrorMessage = message };
    }

    /// <summary>The form (PUT) or the query (GET) value, matched without regard to case as Alpaca requires.</summary>
    public static async Task<string?> Parameter(HttpRequest request, string name) {
        if (request.HasFormContentType) {
            var form = await request.ReadFormAsync();
            var key = form.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is not null) {
                return form[key].ToString();
            }
        }
        var queryKey = request.Query.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return queryKey is not null ? request.Query[queryKey].ToString() : null;
    }

    private static async Task<uint> ClientTransactionId(HttpRequest request) =>
        uint.TryParse(await Parameter(request, "ClientTransactionID"), out var id) ? id : 0;

    private static DeviceErrorKind KindOf(int number) => number switch {
        NotImplemented => DeviceErrorKind.NotImplemented,
        InvalidValue => DeviceErrorKind.InvalidValue,
        NotConnected => DeviceErrorKind.NotConnected,
        InvalidOperation => DeviceErrorKind.InvalidOperation,
        ActionNotImplemented => DeviceErrorKind.ActionNotImplemented,
        _ => DeviceErrorKind.Driver
    };
}
