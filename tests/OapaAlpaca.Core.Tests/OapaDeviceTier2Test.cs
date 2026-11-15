using System.Globalization;
using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaDeviceTier2Test {
    private const string MovingL = "<L|phase:moving_x|outcome:none|source:nina|moves:3|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Correction / capped|>";
    private const string IdleL = "<L|phase:idle|outcome:none|source:none|moves:0|az:-|alt:-|plan:0.00,0.00|reason:|>";

    private static (OapaDevice Device, ScriptedLink Link, ManualClock Clock) Connected() {
        var link = OapaDeviceConnectTest.CalibratedBoard();
        var clock = new ManualClock();
        var device = new OapaDevice(link, "OAPA", clock);
        device.Connect();
        link.Sent.Clear();
        return (device, link, clock);
    }

    [Test]
    public void Begin_sends_tolerance_cap_and_start_in_order() {
        var (d, link, _) = Connected();
        link.Expect("$T=1.50", "ok").Expect("$M=60", "ok").Expect("$A=1", "ok");
        d.Begin(1.5, 60);
        link.Sent.Should().Equal("$T=1.50", "$M=60", "$A=1");
    }

    [Test]
    public void Begin_without_a_cap_sends_two_commands() {
        var (d, link, _) = Connected();
        link.Expect("$T=2.00", "ok").Expect("$A=1", "ok");
        d.Begin(2, null);
        link.Sent.Should().Equal("$T=2.00", "$A=1");
    }

    [Test]
    public void Numbers_are_written_with_a_dot_under_an_italian_culture() {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try {
            var (d, link, _) = Connected();
            link.Expect("$T=1.50", "ok").Expect("$A=1", "ok").Expect("$E=-2.10,0.40", "ok").Expect("$L?", MovingL);
            d.Begin(1.5, null);
            d.Error(-2.1, 0.4);
            link.Sent.Should().Contain("$E=-2.10,0.40");
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase(0)]
    [TestCase(61)]
    [TestCase(double.NaN)]
    [TestCase(-1)]
    public void Begin_refuses_a_bad_tolerance_before_sending(double tolerance) {
        var (d, link, _) = Connected();
        var act = () => d.Begin(tolerance, null);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue);
        link.Sent.Should().BeEmpty();
    }

    [TestCase(0.5)]
    [TestCase(181)]
    public void Begin_refuses_a_cap_outside_the_board_limits(double cap) {
        var (d, link, _) = Connected();
        var act = () => d.Begin(1, cap);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue);
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void Error_sends_the_reading_and_refreshes_the_alignment_block() {
        var (d, link, _) = Connected();
        link.Expect("$T=1.50", "ok").Expect("$A=1", "ok");
        d.Begin(1.5, null);
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$L?", IdleL);
        d.Status(); // fills the cache before the reading
        link.Expect("$E=-2.10,0.40", "ok").Expect("$L?", MovingL);
        d.Error(-2.1, 0.4);
        var status = d.Status(); // within the cache age: no new "?"
        status.Alignment.Should().Be(new AlignmentBlock("moving", 3, "Correction / capped"));
        link.Sent.Should().Equal("$T=1.50", "$A=1", "?", "$L?", "$E=-2.10,0.40", "$L?");
    }

    [TestCase(double.NaN, 0)]
    [TestCase(1e9, 0)]
    [TestCase(0, double.PositiveInfinity)]
    [TestCase(0, -3601)]
    public void Error_refuses_a_bad_value_before_sending(double az, double alt) {
        var (d, link, _) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok");
        d.Begin(1, null);
        var act = () => d.Error(az, alt);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue);
        link.Sent.Should().NotContain(c => c.StartsWith("$E="));
    }

    [Test]
    public void End_sends_stop_and_a_later_status_reads_idle_without_querying_L() {
        var (d, link, clock) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok").Expect("$A=0", "ok");
        d.Begin(1, null);
        d.End();
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        clock.Advance(TimeSpan.FromSeconds(1));
        d.Status().Alignment.Phase.Should().Be("idle");
        link.Sent.Should().NotContain("$L?");
    }

    [Test]
    public void A_board_restart_during_a_session_reads_failed() {
        var (d, link, clock) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok");
        d.Begin(1, null);
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$L?", IdleL);
        clock.Advance(TimeSpan.FromSeconds(1));
        d.Status().Alignment.Should().Be(new AlignmentBlock("failed", 0, "the board lost the alignment"));
    }

    [Test]
    public void Stop_sends_bang_and_ends_the_session() {
        var (d, link, clock) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok").Expect("!", "ok");
        d.Begin(1, null);
        d.Stop();
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        clock.Advance(TimeSpan.FromSeconds(1));
        d.Status();
        link.Sent.Should().Equal("$T=1.00", "$A=1", "!", "?");
    }

    [Test]
    public void Home_sends_H() {
        var (d, link, _) = Connected();
        link.Expect("$H", "ok");
        d.Home();
        link.Sent.Should().Equal("$H");
    }

    [Test]
    public void Move_is_not_implemented_in_this_version() {
        var (d, link, _) = Connected();
        var act = () => d.Move("az", 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.NotImplemented && e.Message.Contains("$R="));
        link.Sent.Should().BeEmpty();
    }

    [Test]
    public void A_refused_command_is_an_invalid_value() {
        var (d, link, _) = Connected();
        link.Expect("$T=5.00", "error");
        var act = () => d.Begin(5, null);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue && e.Message.Contains("$T=5.00"));
    }

    [Test]
    public void A_dead_link_is_a_link_error_and_the_device_stays_connected() {
        var (d, link, _) = Connected();
        link.Expect("$T=1.00", "ok").Expect("$A=1", "ok");
        d.Begin(1, null);
        link.FailNext();
        var act = () => d.Error(1, 1);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.Link);
        d.Connected.Should().BeTrue();
    }

    [Test]
    public void Methods_throw_not_connected_before_connect() {
        var d = new OapaDevice(new ScriptedLink(), "OAPA", new ManualClock());
        foreach (var act in new Action[] { () => d.Begin(1, null), () => d.Error(0, 0), () => d.End(), () => d.Stop(), () => d.Home(), () => d.Status() }) {
            act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.NotConnected);
        }
    }
}
