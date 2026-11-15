namespace OapaAlpaca.Core;

/// <summary>
/// What the board said at connect: its firmware version, the largest single move it accepts (arcminutes),
/// when calibrated the arcminutes per motor step of each axis, and whether it has the relative move
/// command <c>$R=</c> (firmware 1.3.7 and later) that Tier 1 needs.
/// </summary>
public sealed record DeviceInfo(string FirmwareVersion, double MaxMove, double? ResolutionAz, double? ResolutionAlt, bool Calibrated, bool CanMove);
