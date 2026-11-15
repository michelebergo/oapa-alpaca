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

    public OapaDevice(IOapaLink link, string name, IClock clock) {
        this.link = link;
        this.clock = clock;
        Name = name;
    }

    public string Name { get; }

    public bool Connected { get; private set; }

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
                link.Open();
                var protocol = Exchange("$P?");
                if (!OapaFrame.TryParse(protocol[0], out var p) || p.Name != "P" || !int.TryParse(p.Field("protocol"), out var version) || version < 1) {
                    throw new DeviceException(DeviceErrorKind.Driver, $"{link.Description} is not an OAPA alignment device (no protocol version)");
                }
                var status = StatusFrame.Parse(Frame(Exchange("?", replyLines: 2)[0], "?"));
                var maxMove = OapaFrame.TryParse(Exchange("$M?")[0], out var m) && m.Name == "M"
                    ? OapaFrame.ParseNumber(m.Field("max"))
                    : DefaultMaxMove;
                calibration = OapaFrame.TryParse(Exchange("$K?")[0], out var k) && k.Name == "K"
                    ? Calibration.Parse(k)
                    : new Calibration(false, null, 0, null, 0);
                info = new DeviceInfo(status.Version ?? "", maxMove,
                                      Resolution(calibration.FactorX), Resolution(calibration.FactorY), calibration.Done);
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
        }
    }

    /// <summary>
    /// The axes and the alignment block. Polls <c>?</c> (and <c>$L?</c> during a session) unless the
    /// last poll is younger than <see cref="StatusCacheAge"/>.
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
            cachedStatus = new DeviceStatus(
                new AxisStatus(moving, Position(frame.X, calibration.FactorX)),
                new AxisStatus(moving, Position(frame.Y, calibration.FactorY)),
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

    /// <summary>One command and its reply lines, under the gate; a link failure becomes a Link error.</summary>
    private string[] Exchange(string command, int replyLines = 1) {
        try {
            link.WriteLine(command);
            var lines = new string[replyLines];
            for (var i = 0; i < replyLines; i++) {
                lines[i] = link.ReadLine();
            }
            return lines;
        } catch (LinkException e) {
            throw new DeviceException(DeviceErrorKind.Link, e.Message);
        }
    }

    private static DeviceException NotConnected() => new(DeviceErrorKind.NotConnected, "the board is not connected");
}
