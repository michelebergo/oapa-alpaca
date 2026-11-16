using System.Globalization;
namespace OapaAlpaca.Core;

/// <summary>
/// An OAPA board seen through the PolarAlign contract. Owns the link and lets one exchange through at
/// a time, so a status poll never overlaps another command.
/// </summary>
public sealed class OapaDevice {
    public const double DefaultMaxMove = 120;
    public static readonly TimeSpan StatusCacheAge = TimeSpan.FromMilliseconds(250);
    private static readonly AlignmentBlock IdleBlock = new("idle", 0, null);

    private readonly object gate = new();
    private readonly IClock clock;
    private IOapaLink link;
    private DeviceInfo? info;
    private Calibration calibration = new(false, null, 0, null, 0);
    private DeviceStatus? cachedStatus;
    private DateTimeOffset cachedAt;
    private AlignmentBlock alignment = IdleBlock;
    // Set by the Tier 2 methods: a session runs between Begin and End.
    private bool sessionActive;
    // Tier 1: a relative move sent with $R= whose outcome $R? has not reported yet, and the axes it moved.
    private bool movePending;
    private bool movedAz;
    private bool movedAlt;
    private string? faultAz;
    private string? faultAlt;

    public OapaDevice(IOapaLink link, string name, IClock clock) {
        this.link = link;
        this.clock = clock;
        Name = name;
    }

    public string Name { get; }

    public bool Connected { get; private set; }

    /// <summary>Where the current link goes (a port name or <c>tcp://host:port</c>), for the setup page.</summary>
    public string LinkDescription {
        get {
            lock (gate) {
                return link.Description;
            }
        }
    }

    /// <summary>Replaces the link, only while disconnected: the next <see cref="Connect"/> goes through the new one.</summary>
    public void UseLink(IOapaLink replacement) {
        lock (gate) {
            if (Connected) {
                throw new DeviceException(DeviceErrorKind.InvalidOperation, "disconnect before changing the link; the change applies at the next connect");
            }
            link.Close();
            link = replacement;
        }
    }

    public DeviceInfo Info {
        get {
            lock (gate) {
                return info ?? throw NotConnected();
            }
        }
    }

    public void Connect() {
        lock (gate) {
            if (Connected) {
                return;
            }
            try {
                try {
                    link.Open();
                } catch (LinkException e) {
                    throw new DeviceException(DeviceErrorKind.Link, e.Message);
                }
                var protocol = Exchange("$P?");
                if (!OapaFrame.TryParse(protocol[0], out var p) || p.Name != "P" || !int.TryParse(p.Field("protocol"), out var version) || version < 1) {
                    throw new DeviceException(DeviceErrorKind.Driver, $"{link.Description} is not an OAPA alignment device (no protocol version)");
                }
                var status = StatusFrame.Parse(Frame(Exchange("?", replyLines: 2)[0], "?"));
                // $M? "max" is the largest cap $M= accepts; since firmware 1.3.7 "cap" is the cap the board
                // enforces on a relative move (its page setting), and that is what bounds a Move and a Begin.
                var maxMove = OapaFrame.TryParse(Exchange("$M?")[0], out var m) && m.Name == "M"
                    ? OapaFrame.ParseNumber(m.Fields.TryGetValue("cap", out var cap) && cap.Length > 0 ? cap : m.Field("max"))
                    : DefaultMaxMove;
                // A board with $R= answers $R? with a frame; an older one answers a plain "ok".
                var canMove = OapaFrame.TryParse(Exchange("$R?")[0], out var r) && r.Name == "R";
                calibration = OapaFrame.TryParse(Exchange("$K?")[0], out var k) && k.Name == "K"
                    ? Calibration.Parse(k)
                    : new Calibration(false, null, 0, null, 0);
                info = new DeviceInfo(status.Version ?? "", maxMove,
                                      Resolution(calibration.FactorX), Resolution(calibration.FactorY), calibration.Done, canMove);
                Connected = true;
            } catch {
                link.Close();
                info = null;
                Connected = false;
                throw;
            }
        }
    }

    public void Disconnect() {
        lock (gate) {
            link.Close();
            info = null;
            Connected = false;
            cachedStatus = null;
            alignment = IdleBlock;
            sessionActive = false;
            ForgetMoves();
        }
    }

    /// <summary>
    /// The axes and the alignment block. Polls <c>?</c> (plus <c>$L?</c> during a session and <c>$R?</c>
    /// while a relative move is pending) unless the last poll is younger than <see cref="StatusCacheAge"/>.
    /// </summary>
    public DeviceStatus Status() {
        lock (gate) {
            if (!Connected) {
                throw NotConnected();
            }
            if (cachedStatus is not null && clock.UtcNow - cachedAt < StatusCacheAge) {
                return cachedStatus;
            }
            var frame = StatusFrame.Parse(Frame(Exchange("?", replyLines: 2)[0], "?"));
            if (sessionActive) {
                RefreshAlignment();
            }
            var moving = frame.State is "Run" or "Home";
            if (movePending) {
                moving |= RefreshMove();
            }
            cachedStatus = new DeviceStatus(
                new AxisStatus(moving, Position(frame.X, calibration.FactorX), faultAz),
                new AxisStatus(moving, Position(frame.Y, calibration.FactorY), faultAlt),
                alignment);
            cachedAt = clock.UtcNow;
            return cachedStatus;
        }
    }

    /// <summary>Spec 4.4: the board's loop phases and outcomes as PolarAlign phases.</summary>
    public static AlignmentBlock MapAlignment(AlignmentState state, bool sessionActive) {
        var reason = state.Reason.Length > 0 ? state.Reason : null;
        var (phase, text) = state.Phase switch {
            "idle" => sessionActive ? ("failed", "the board lost the alignment") : ("idle", null),
            "waiting" => ("waiting", reason),
            "moving_x" or "moving_y" => ("moving", reason),
            "ended" => state.Outcome switch {
                "finished" or "finished_best_effort" => ("done", reason),
                "stopped_no_source" => ("failed", "no readings for 30 s"),
                var o when o.StartsWith("halted_", StringComparison.Ordinal) => ("failed", reason),
                var o when o.StartsWith("stopped_", StringComparison.Ordinal) => sessionActive ? ("failed", "stopped") : ("idle", null),
                _ => ("failed", reason)
            },
            _ => ("failed", reason)
        };
        return new AlignmentBlock(phase, state.Moves, text);
    }

    /// <summary>Tier 2: hand the alignment to the board. Spec 4.3: <c>$T=</c>, <c>$M=</c> when given, <c>$A=1</c>.</summary>
    public void Begin(double tolerance, double? maxMove) {
        lock (gate) {
            var current = Info;
            if (!double.IsFinite(tolerance) || tolerance <= 0 || tolerance > 60) {
                throw new DeviceException(DeviceErrorKind.InvalidValue, $"tolerance {Arcmin(tolerance)}' is outside (0, 60]");
            }
            if (maxMove is not null && (!double.IsFinite(maxMove.Value) || maxMove < 1 || maxMove > current.MaxMove)) {
                throw new DeviceException(DeviceErrorKind.InvalidValue, $"maxMove {Arcmin(maxMove.Value)}' is outside [1, {Arcmin(current.MaxMove)}]");
            }
            Command($"$T={Arcmin(tolerance)}");
            if (maxMove is not null) {
                Command($"$M={maxMove.Value.ToString("0", CultureInfo.InvariantCulture)}");
            }
            Command("$A=1");
            sessionActive = true;
            alignment = new AlignmentBlock("waiting", 0, null);
            UpdateCachedAlignment();
        }
    }

    /// <summary>Tier 2: one error reading for the board, then the board's verdict through <c>$L?</c>.</summary>
    public void Error(double az, double alt) {
        lock (gate) {
            _ = Info;
            foreach (var (value, axis) in new[] { (az, "az"), (alt, "alt") }) {
                if (!double.IsFinite(value) || Math.Abs(value) > 3600) {
                    throw new DeviceException(DeviceErrorKind.InvalidValue, $"{axis} error {value} is not a usable reading");
                }
            }
            Command($"$E={Arcmin(az)},{Arcmin(alt)}");
            RefreshAlignment();
        }
    }

    /// <summary>Tier 2: the host ends the session. The board's later "stopped" state reads as idle.</summary>
    public void End() {
        lock (gate) {
            _ = Info;
            Command("$A=0");
            EndSession();
        }
    }

    /// <summary>Stops both axes now; also ends a session the host is feeding and clears the move faults.</summary>
    public void Stop() {
        lock (gate) {
            _ = Info;
            Command("!");
            EndSession();
            ForgetMoves();
        }
    }

    /// <summary>Homes both axes (an endstop, or zero in place); also ends a session the host is feeding and clears the move faults.</summary>
    public void Home() {
        lock (gate) {
            _ = Info;
            Command("$H");
            EndSession();
            ForgetMoves();
        }
    }

    /// <summary>
    /// Tier 1: move one axis by <paramref name="arcmin"/> through <c>$R=</c> (spec 4.2). The value returned
    /// is the request rounded to whole steps, what the axis will net.
    /// </summary>
    public double Move(string axis, double arcmin) {
        if (axis is not ("az" or "alt")) {
            throw new DeviceException(DeviceErrorKind.InvalidValue, $"axis '{axis}' is not az or alt");
        }
        var moved = axis == "az" ? Move(arcmin, 0) : Move(0, arcmin);
        return axis == "az" ? moved.Az : moved.Alt;
    }

    /// <summary>
    /// A relative move of both axes, in arcminutes, through one <c>$R=</c>. The board converts with its own
    /// factors and compensates backlash; the values returned are the requests rounded to whole steps. A move
    /// of zero on both axes sends nothing and completes at once; any move clears the faults of the axes it
    /// moves (a zero move clears both). A move ends a Tier 2 session the host is feeding, as a hand move does.
    /// </summary>
    public (double Az, double Alt) Move(double azArcmin, double altArcmin) {
        lock (gate) {
            var current = Info;
            if (!current.CanMove) {
                throw new DeviceException(DeviceErrorKind.NotImplemented, "Tier 1 needs firmware 1.3.7 or later ($R=); this board offers Tier 2 only");
            }
            foreach (var (value, axis) in new[] { (azArcmin, "az"), (altArcmin, "alt") }) {
                if (!double.IsFinite(value) || Math.Abs(value) > current.MaxMove) {
                    throw new DeviceException(DeviceErrorKind.InvalidValue, $"{axis} move {value} is not within +/-{Arcmin(current.MaxMove)}'");
                }
            }
            if (!current.Calibrated) {
                throw new DeviceException(DeviceErrorKind.InvalidOperation, "not calibrated: run an alignment with the controller first, or enter the factors");
            }
            var zero = azArcmin == 0 && altArcmin == 0;
            if (!zero && Exchange($"$R={Arcmin(azArcmin)},{Arcmin(altArcmin)}")[0] == "error") {
                // Calibrated and within $M? from here; the board still refuses while an axis moves, while it
                // calibrates, and beyond its own move cap (the setting on its page, or one pushed with $M=).
                throw new DeviceException(DeviceErrorKind.InvalidOperation, "the board refused the move: an axis is moving, a calibration is running, or the move exceeds the board's own cap");
            }
            movePending = !zero;
            movedAz = zero || azArcmin != 0;
            movedAlt = zero || altArcmin != 0;
            if (movedAz) {
                faultAz = null;
            }
            if (movedAlt) {
                faultAlt = null;
            }
            if (sessionActive) {
                sessionActive = false;
                alignment = new AlignmentBlock("failed", alignment.Moves, "interrupted by a move");
            }
            cachedStatus = null; // the next poll must see the motion (or the cleared fault), not a cached one
            return (Round(azArcmin, calibration.FactorX), Round(altArcmin, calibration.FactorY));
        }
    }

    private static double Round(double arcmin, double? factor) => arcmin == 0 ? 0 : Math.Round(arcmin * factor!.Value) / factor.Value;

    private void Command(string command) {
        var reply = Exchange(command)[0];
        if (reply == "error") {
            throw new DeviceException(DeviceErrorKind.InvalidValue, $"the board refused {command}");
        }
    }

    private void EndSession() {
        sessionActive = false;
        alignment = IdleBlock;
        UpdateCachedAlignment();
    }

    private void UpdateCachedAlignment() {
        if (cachedStatus is not null) {
            cachedStatus = cachedStatus with { Alignment = alignment };
        }
    }

    private static string Arcmin(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// One <c>$R?</c> for a pending relative move: true while the board still moves; once done, failed or
    /// forgotten (the board restarted) the poll stops and a failure becomes the fault of the moved axis.
    /// </summary>
    private bool RefreshMove() {
        var frame = Frame(Exchange("$R?")[0], "$R?");
        var state = frame.Field("state");
        if (state == "moving") {
            return true;
        }
        movePending = false;
        var fault = state switch {
            "done" => null,
            "failed" => frame.Field("reason") is { Length: > 0 } reason ? reason : "failed",
            _ => "the board lost the move"
        };
        if (movedAz) {
            faultAz = fault;
        }
        if (movedAlt) {
            faultAlt = fault;
        }
        return false;
    }

    private void ForgetMoves() {
        movePending = false;
        movedAz = false;
        movedAlt = false;
        faultAz = null;
        faultAlt = null;
        if (cachedStatus is not null) {
            cachedStatus = cachedStatus with { Az = cachedStatus.Az with { Fault = null }, Alt = cachedStatus.Alt with { Fault = null } };
        }
    }

    private void RefreshAlignment() {
        var state = AlignmentState.Parse(Frame(Exchange("$L?")[0], "$L?"));
        alignment = MapAlignment(state, sessionActive);
        if (cachedStatus is not null) {
            cachedStatus = cachedStatus with { Alignment = alignment };
        }
    }

    private static double? Position(long steps, double? factor) => factor is > 0 ? steps / factor.Value : null;

    private static double? Resolution(double? factor) => factor is > 0 ? 1 / factor.Value : null;

    private static OapaFrame Frame(string line, string command) =>
        OapaFrame.TryParse(line, out var frame) ? frame : throw new DeviceException(DeviceErrorKind.Driver, $"the board answered {command} with '{line}' instead of a frame");

    /// <summary>
    /// One command and its reply lines, under the gate. A link failure becomes a Link error and closes
    /// the link: a half-read reply left in the buffer would shift every later reply by one line, so
    /// every call fails fast from then on until the client disconnects and connects again.
    /// </summary>
    private string[] Exchange(string command, int replyLines = 1) {
        try {
            link.WriteLine(command);
            var lines = new string[replyLines];
            for (var i = 0; i < replyLines; i++) {
                lines[i] = link.ReadLine();
            }
            return lines;
        } catch (LinkException e) {
            link.Close();
            throw new DeviceException(DeviceErrorKind.Link, e.Message);
        }
    }

    private static DeviceException NotConnected() => new(DeviceErrorKind.NotConnected, "the board is not connected");
}
