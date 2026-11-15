namespace OapaAlpaca.Core;

/// <summary>One axis: whether it moves, and its position in arcminutes from the board's zero when calibrated.</summary>
public sealed record AxisStatus(bool Moving, double? Position);

/// <summary>The board's own alignment loop, in PolarAlign phases: idle, waiting, moving, done, failed.</summary>
public sealed record AlignmentBlock(string Phase, int Moves, string? Reason);

public sealed record DeviceStatus(AxisStatus Az, AxisStatus Alt, AlignmentBlock Alignment);
