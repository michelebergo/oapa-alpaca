namespace OapaAlpaca.Core;

/// <summary>
/// One axis: whether it moves, its position in arcminutes from the board's zero when calibrated, and the
/// board's reason when the last relative move of this axis failed (until the next Move of the axis, Stop or Home).
/// </summary>
public sealed record AxisStatus(bool Moving, double? Position, string? Fault = null);

/// <summary>The board's own alignment loop, in PolarAlign phases: idle, waiting, moving, done, failed.</summary>
public sealed record AlignmentBlock(string Phase, int Moves, string? Reason);

public sealed record DeviceStatus(AxisStatus Az, AxisStatus Alt, AlignmentBlock Alignment);
