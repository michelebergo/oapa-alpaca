namespace OapaAlpaca.Core;

/// <summary>
/// What the board said at connect: its firmware version, the largest single move it accepts (arcminutes)
/// and, when calibrated, the arcminutes per motor step of each axis.
/// </summary>
public sealed record DeviceInfo(string FirmwareVersion, double MaxMove, double? ResolutionAz, double? ResolutionAlt, bool Calibrated);
