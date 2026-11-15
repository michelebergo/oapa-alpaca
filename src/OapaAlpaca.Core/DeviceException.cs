namespace OapaAlpaca.Core;

/// <summary>Why a device call failed; the host maps each kind to an Alpaca error number, or to a failed request for <see cref="Link"/>.</summary>
public enum DeviceErrorKind { NotConnected, NotImplemented, ActionNotImplemented, InvalidValue, InvalidOperation, Link, Driver }

public sealed class DeviceException(DeviceErrorKind kind, string message) : Exception(message) {
    public DeviceErrorKind Kind { get; } = kind;
}
