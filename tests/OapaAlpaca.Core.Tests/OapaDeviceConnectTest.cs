using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaDeviceConnectTest {
    public const string IdleStatus = "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>";
    public const string CalibratedK = "<K|state:done|x:12.34,+1|y:40.10,-1|xplay:4.20,4.35,U|yplay:-|reason:Calibration complete|>";
    public const string IdleK = "<K|state:idle|x:-|y:-|xplay:-|yplay:-|reason:|>";
    /// <summary>Factors entered by the host with <c>$F=</c>: valid, sign 0 because not measured (firmware 1.3.7).</summary>
    public const string PushedK = "<K|state:pushed|x:59.61,0|y:59.44,0|xplay:-|yplay:-|reason:factors from the host|>";
    public const string IdleR = "<R|state:idle|az:0.00|alt:0.00|reason:|>";

    /// <summary>
    /// A scripted link with the whole connect sequence of a calibrated board queued. The board answers
    /// <c>$R?</c> with a frame (firmware 1.3.7 and later); <paramref name="relative"/> "ok" is an older one.
    /// </summary>
    public static ScriptedLink CalibratedBoard(string calibration = CalibratedK, string maxMove = "<M|max:180|>", string relative = IdleR) =>
        new ScriptedLink()
            .Expect("$P?", "<P|protocol:1|>")
            .Expect("?", IdleStatus, "ok")
            .Expect("$M?", maxMove)
            .Expect("$R?", relative)
            .Expect("$K?", calibration);

    [Test]
    public void Connect_runs_the_sequence_and_fills_info() {
        var link = CalibratedBoard();
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        d.Connect();
        d.Connected.Should().BeTrue();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, 1 / 12.34, 1 / 40.10, true, true));
        link.Sent.Should().Equal("$P?", "?", "$M?", "$R?", "$K?");
    }

    [Test]
    public void A_board_without_the_relative_move_command_cannot_move() {
        var d = new OapaDevice(CalibratedBoard(relative: "ok"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, 1 / 12.34, 1 / 40.10, true, false));
    }

    [Test]
    public void Factors_pushed_by_the_host_count_as_calibrated() {
        var d = new OapaDevice(CalibratedBoard(calibration: PushedK), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, 1 / 59.61, 1 / 59.44, true, true));
    }

    [Test]
    public void A_calibration_with_one_factor_missing_is_not_calibrated() {
        var d = new OapaDevice(CalibratedBoard(calibration: "<K|state:running|x:12.34,+1|y:-|xplay:-|yplay:-|reason:|>"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Calibrated.Should().BeFalse();
    }

    [Test]
    public void A_board_without_the_protocol_is_refused_and_the_link_closed() {
        var link = new ScriptedLink().Expect("$P?", "ok");
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        var act = () => d.Connect();
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.Driver && e.Message.Contains("not an OAPA alignment device"));
        d.Connected.Should().BeFalse();
        link.IsOpen.Should().BeFalse();
    }

    [Test]
    public void The_cap_the_board_enforces_on_moves_is_the_max_move_when_the_board_reports_it() {
        // Firmware 1.3.7: $M? carries the page's move cap, which bounds $R=; the 180 is only what $M= accepts.
        var d = new OapaDevice(CalibratedBoard(maxMove: "<M|max:180|cap:30|>"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.MaxMove.Should().Be(30);
    }

    [Test]
    public void A_board_without_the_move_cap_reports_120() {
        var d = new OapaDevice(CalibratedBoard(maxMove: "ok"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.MaxMove.Should().Be(120);
    }

    [Test]
    public void An_uncalibrated_board_has_no_resolution() {
        var d = new OapaDevice(CalibratedBoard(calibration: IdleK), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, null, null, false, true));
    }

    [Test]
    public void A_board_that_answers_ok_to_the_calibration_query_is_not_calibrated() {
        var d = new OapaDevice(CalibratedBoard(calibration: "ok"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Calibrated.Should().BeFalse();
    }

    [Test]
    public void Info_throws_not_connected_before_connect() {
        var d = new OapaDevice(CalibratedBoard(), "OAPA", new ManualClock());
        var act = () => d.Info;
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.NotConnected);
    }

    [Test]
    public void A_dead_link_during_connect_is_a_link_error() {
        var link = CalibratedBoard();
        link.FailNext();
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        var act = () => d.Connect();
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.Link);
        d.Connected.Should().BeFalse();
    }

    [Test]
    public void Use_link_while_connected_is_an_invalid_operation() {
        var d = new OapaDevice(CalibratedBoard(), "OAPA", new ManualClock());
        d.Connect();
        var act = () => d.UseLink(new ScriptedLink());
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidOperation);
    }

    [Test]
    public void Use_link_while_disconnected_closes_the_old_link_and_connects_through_the_new_one() {
        var old = new ScriptedLink();
        var d = new OapaDevice(old, "OAPA", new ManualClock());
        var replacement = CalibratedBoard();
        d.UseLink(replacement);
        d.Connect();
        d.Connected.Should().BeTrue();
        d.LinkDescription.Should().Be("scripted");
        replacement.Sent.Should().Equal("$P?", "?", "$M?", "$R?", "$K?");
        old.Sent.Should().BeEmpty();
    }

    [Test]
    public void A_link_that_cannot_open_is_a_link_error() {
        var link = new ScriptedLink { FailOpen = true };
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        var act = () => d.Connect();
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.Link && e.Message.Contains("cannot open"));
        d.Connected.Should().BeFalse();
    }

    [Test]
    public void Disconnect_closes_the_link() {
        var link = CalibratedBoard();
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        d.Connect();
        d.Disconnect();
        d.Connected.Should().BeFalse();
        link.IsOpen.Should().BeFalse();
    }
}
