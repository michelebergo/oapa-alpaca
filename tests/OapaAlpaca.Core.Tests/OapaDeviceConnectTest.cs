using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaDeviceConnectTest {
    public const string IdleStatus = "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>";
    public const string CalibratedK = "<K|state:done|x:12.34,+1|y:40.10,-1|xplay:4.20,4.35,U|yplay:-|reason:Calibration complete|>";
    public const string IdleK = "<K|state:idle|x:-|y:-|xplay:-|yplay:-|reason:|>";

    /// <summary>A scripted link with the whole connect sequence of a calibrated 1.3.6 board queued.</summary>
    public static ScriptedLink CalibratedBoard(string calibration = CalibratedK, string maxMove = "<M|max:180|>") =>
        new ScriptedLink()
            .Expect("$P?", "<P|protocol:1|>")
            .Expect("?", IdleStatus, "ok")
            .Expect("$M?", maxMove)
            .Expect("$K?", calibration);

    [Test]
    public void Connect_runs_the_sequence_and_fills_info() {
        var link = CalibratedBoard();
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        d.Connect();
        d.Connected.Should().BeTrue();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, 1 / 12.34, 1 / 40.10, true));
        link.Sent.Should().Equal("$P?", "?", "$M?", "$K?");
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
    public void A_board_without_the_move_cap_reports_120() {
        var d = new OapaDevice(CalibratedBoard(maxMove: "ok"), "OAPA", new ManualClock());
        d.Connect();
        d.Info.MaxMove.Should().Be(120);
    }

    [Test]
    public void An_uncalibrated_board_has_no_resolution() {
        var d = new OapaDevice(CalibratedBoard(calibration: IdleK), "OAPA", new ManualClock());
        d.Connect();
        d.Info.Should().Be(new DeviceInfo("1.3.6", 180, null, null, false));
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
    public void Disconnect_closes_the_link() {
        var link = CalibratedBoard();
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        d.Connect();
        d.Disconnect();
        d.Connected.Should().BeFalse();
        link.IsOpen.Should().BeFalse();
    }
}
