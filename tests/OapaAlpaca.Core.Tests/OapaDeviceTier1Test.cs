using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaDeviceTier1Test {
    private const string MovingR = "<R|state:moving|az:0.50|alt:0.00|reason:|>";
    private const string DoneR = "<R|state:done|az:1.54|alt:0.00|reason:|>";
    private const string FailedR = "<R|state:failed|az:0.50|alt:0.00|reason:timeout AZ (X)|>";
    private const string RunStatus = "<Run|MPos:6.00,0.00,0.00|V:1.3.7|>";

    private static (OapaDevice Device, ScriptedLink Link, ManualClock Clock) Connected(string calibration = OapaDeviceConnectTest.CalibratedK, string relative = OapaDeviceConnectTest.IdleR) {
        var link = OapaDeviceConnectTest.CalibratedBoard(calibration, relative: relative);
        var clock = new ManualClock();
        var device = new OapaDevice(link, "OAPA", clock);
        device.Connect();
        link.Sent.Clear();
        return (device, link, clock);
    }

    [Test]
    public void Move_az_sends_R_with_a_zero_altitude_and_answers_the_amount_rounded_to_steps() {
        var (d, link, _) = Connected();
        link.Expect("$R=1.50,0.00", "ok");
        d.Move("az", 1.5).Should().BeApproximately(19 / 12.34, 1e-9); // 1.5' * 12.34 steps/' = 18.51 -> 19 steps
        link.Sent.Should().Equal("$R=1.50,0.00");
    }

    [Test]
    public void Move_alt_sends_R_with_a_zero_azimuth() {
        var (d, link, _) = Connected();
        link.Expect("$R=0.00,-2.00", "ok");
        d.Move("alt", -2).Should().BeApproximately(-80 / 40.10, 1e-9);
        link.Sent.Should().Equal("$R=0.00,-2.00");
    }

    [Test]
    public void Move_refuses_an_unknown_axis_before_sending() {
        var (d, link, _) = Connected();
        var act = () => d.Move("ra", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue && e.Message.Contains("ra"));
        link.Sent.Should().BeEmpty();
    }

    [TestCase(181)]
    [TestCase(-180.01)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void Move_beyond_the_cap_or_not_a_number_is_an_invalid_value(double arcmin) {
        var (d, link, _) = Connected();
        var act = () => d.Move("az", arcmin);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue);
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void Move_beyond_the_cap_the_board_enforces_is_refused_before_sending() {
        var link = OapaDeviceConnectTest.CalibratedBoard(maxMove: "<M|max:180|cap:30|>");
        var d = new OapaDevice(link, "OAPA", new ManualClock());
        d.Connect();
        link.Sent.Clear();
        var act = () => d.Move("az", 31);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue && e.Message.Contains("30.00"));
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void Move_on_a_board_without_the_command_is_not_implemented() {
        var (d, link, _) = Connected(relative: "ok");
        var act = () => d.Move("az", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.NotImplemented && e.Message.Contains("$R="));
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void Move_on_an_uncalibrated_board_is_an_invalid_operation() {
        var (d, link, _) = Connected(calibration: OapaDeviceConnectTest.IdleK);
        var act = () => d.Move("az", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidOperation && e.Message.Contains("not calibrated"));
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void Move_with_pushed_factors_goes_through() {
        var (d, link, _) = Connected(calibration: OapaDeviceConnectTest.PushedK);
        link.Expect("$R=1.00,0.00", "ok");
        d.Move("az", 1).Should().BeApproximately(60 / 59.61, 1e-9);
    }

    [Test]
    public void A_move_the_board_refuses_is_an_invalid_operation() {
        var (d, link, _) = Connected();
        link.Expect("$R=1.00,0.00", "error");
        var act = () => d.Move("az", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidOperation && e.Message.Contains("moving") && e.Message.Contains("cap"));
    }

    [Test]
    public void Status_polls_R_while_the_move_runs_and_stops_once_it_is_done() {
        var (d, link, clock) = Connected();
        link.Expect("$R=1.50,0.00", "ok");
        d.Move("az", 1.5);
        link.Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        var running = d.Status();
        running.Az.Should().Be(new AxisStatus(true, 6 / 12.34, null));
        clock.Advance(TimeSpan.FromSeconds(1));
        link.Expect("?", "<Idle|MPos:19.00,0.00,0.00|V:1.3.7|>", "ok").Expect("$R?", DoneR);
        var done = d.Status();
        done.Az.Should().Be(new AxisStatus(false, 19 / 12.34, null));
        clock.Advance(TimeSpan.FromSeconds(1));
        link.Expect("?", "<Idle|MPos:19.00,0.00,0.00|V:1.3.7|>", "ok");
        d.Status();
        link.Sent.Should().Equal("$R=1.50,0.00", "?", "$R?", "?", "$R?", "?");
    }

    [Test]
    public void A_move_drops_the_status_cache_so_the_next_poll_sees_the_motion() {
        var (d, link, _) = Connected();
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        d.Status().Az.Moving.Should().BeFalse();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        d.Move("az", 1);
        d.Status().Az.Moving.Should().BeTrue(); // no clock advance: without the drop this would come from the cache
    }

    [Test]
    public void A_failed_move_leaves_a_fault_on_the_moved_axis_until_the_next_move_of_that_axis() {
        var (d, link, clock) = Connected();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        d.Move("az", 1);
        var failed = d.Status();
        failed.Az.Fault.Should().Be("timeout AZ (X)");
        failed.Alt.Fault.Should().BeNull();
        clock.Advance(TimeSpan.FromSeconds(1));
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        d.Status().Az.Fault.Should().Be("timeout AZ (X)"); // still there, and no more $R? polls
        link.Expect("$R=0.00,1.00", "ok").Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        d.Move("alt", 1);
        d.Status().Az.Fault.Should().Be("timeout AZ (X)"); // a move of the other axis does not clear it
        clock.Advance(TimeSpan.FromSeconds(1));
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", DoneR);
        d.Status();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        d.Move("az", 1);
        d.Status().Az.Fault.Should().BeNull();
    }

    [Test]
    public void Stop_clears_the_faults_and_ends_the_R_polling() {
        var (d, link, clock) = Connected();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        d.Move("az", 1);
        d.Status().Az.Fault.Should().NotBeNull();
        link.Expect("!", "ok");
        d.Stop();
        clock.Advance(TimeSpan.FromSeconds(1));
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        d.Status().Az.Fault.Should().BeNull();
        link.Sent.Should().Equal("$R=1.00,0.00", "?", "$R?", "!", "?");
    }

    [Test]
    public void Home_clears_the_faults_too() {
        var (d, link, clock) = Connected();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        d.Move("az", 1);
        d.Status();
        link.Expect("$H", "ok").Expect("?", "<Home|MPos:0.00,0.00,0.00|V:1.3.7|>", "ok");
        d.Home();
        clock.Advance(TimeSpan.FromSeconds(1));
        var s = d.Status();
        s.Az.Fault.Should().BeNull();
        s.Az.Moving.Should().BeTrue();
    }

    [Test]
    public void A_stop_from_the_board_page_reads_as_a_fault_stopped() {
        var (d, link, _) = Connected();
        link.Expect("$R=0.00,5.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", "<R|state:failed|az:0.00|alt:1.20|reason:stopped|>");
        d.Move("alt", 5);
        var s = d.Status();
        s.Alt.Fault.Should().Be("stopped");
        s.Az.Fault.Should().BeNull();
    }

    [Test]
    public void A_board_restart_during_a_move_reads_as_a_fault() {
        var (d, link, _) = Connected();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", OapaDeviceConnectTest.IdleR);
        d.Move("az", 1);
        d.Status().Az.Fault.Should().Be("the board lost the move");
    }

    [Test]
    public void A_move_during_a_tier_2_session_ends_it_as_interrupted() {
        var (d, link, _) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok").Expect("$R=1.00,0.00", "ok").Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        d.Begin(1, null);
        d.Move("az", 1);
        d.Status().Alignment.Should().Be(new AlignmentBlock("failed", 0, "interrupted by a move"));
        link.Sent.Should().Equal("$T=1.00", "$A=1", "$R=1.00,0.00", "?", "$R?"); // no $L? once the session is over
    }

    [Test]
    public void Disconnect_forgets_faults_and_pending_moves() {
        var (d, link, _) = Connected();
        link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        d.Move("az", 1);
        d.Status();
        d.Disconnect();
        link.Expect("$P?", "<P|protocol:1|>").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$M?", "<M|max:180|>")
            .Expect("$R?", FailedR).Expect("$K?", OapaDeviceConnectTest.CalibratedK).Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        d.Connect();
        d.Status().Az.Fault.Should().BeNull(); // a failure from before this connection is not ours to report
    }

    [Test]
    public void Move_throws_not_connected_before_connect() {
        var d = new OapaDevice(new ScriptedLink(), "OAPA", new ManualClock());
        var act = () => d.Move("az", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.NotConnected);
    }
}
