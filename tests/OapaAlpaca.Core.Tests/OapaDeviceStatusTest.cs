using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaDeviceStatusTest {
    private static (OapaDevice Device, ScriptedLink Link, ManualClock Clock) Connected(string calibration = OapaDeviceConnectTest.CalibratedK) {
        var link = OapaDeviceConnectTest.CalibratedBoard(calibration);
        var clock = new ManualClock();
        var device = new OapaDevice(link, "OAPA", clock);
        device.Connect();
        return (device, link, clock);
    }

    [Test]
    public void Status_reports_moving_and_positions_in_arcminutes() {
        var (d, link, _) = Connected();
        link.Expect("?", "<Run|MPos:1234.00,-401.00,0.00|V:1.3.6|>", "ok");
        var s = d.Status();
        s.Az.Should().Be(new AxisStatus(true, 1234 / 12.34));
        s.Alt.Should().Be(new AxisStatus(true, -401 / 40.10));
    }

    [Test]
    public void An_idle_board_is_not_moving() {
        var (d, link, _) = Connected();
        link.Expect("?", "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
        d.Status().Az.Moving.Should().BeFalse();
    }

    [Test]
    public void Homing_counts_as_moving() {
        var (d, link, _) = Connected();
        link.Expect("?", "<Home|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
        d.Status().Alt.Moving.Should().BeTrue();
    }

    [Test]
    public void Positions_are_null_while_not_calibrated() {
        var (d, link, _) = Connected(OapaDeviceConnectTest.IdleK);
        link.Expect("?", "<Idle|MPos:500.00,500.00,0.00|V:1.3.6|>", "ok");
        var s = d.Status();
        s.Az.Position.Should().BeNull();
        s.Alt.Position.Should().BeNull();
    }

    [Test]
    public void A_second_status_within_250_ms_does_not_poll_the_board() {
        var (d, link, clock) = Connected();
        link.Expect("?", "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
        d.Status();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        d.Status();
        link.Sent.Count(c => c == "?").Should().Be(2); // one at connect, one poll
    }

    [Test]
    public void A_status_after_250_ms_polls_again() {
        var (d, link, clock) = Connected();
        link.Expect("?", "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
        link.Expect("?", "<Run|MPos:10.00,0.00,0.00|V:1.3.6|>", "ok");
        d.Status();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        d.Status().Az.Moving.Should().BeTrue();
        link.Sent.Count(c => c == "?").Should().Be(3);
    }

    [Test]
    public void Outside_a_session_no_L_query_is_sent_and_the_block_is_idle() {
        var (d, link, _) = Connected();
        link.Expect("?", "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
        d.Status().Alignment.Should().Be(new AlignmentBlock("idle", 0, null));
        link.Sent.Should().NotContain("$L?");
    }

    [TestCase("idle", "none", false, "idle", null)]
    [TestCase("idle", "none", true, "failed", "the board lost the alignment")]
    [TestCase("waiting", "none", true, "waiting", "r")]
    [TestCase("moving_x", "none", true, "moving", "r")]
    [TestCase("moving_y", "none", true, "moving", "r")]
    [TestCase("ended", "finished", true, "done", "r")]
    [TestCase("ended", "finished_best_effort", true, "done", "r")]
    [TestCase("ended", "halted_runaway", true, "failed", "r")]
    [TestCase("ended", "halted_calibration_suspect", true, "failed", "r")]
    [TestCase("ended", "stopped_no_source", true, "failed", "no readings for 30 s")]
    [TestCase("ended", "stopped_by_user", true, "failed", "stopped")]
    [TestCase("ended", "stopped_by_nina", false, "idle", null)]
    public void The_alignment_phase_follows_the_table(string phase, string outcome, bool sessionActive, string expectedPhase, string? expectedReason) {
        var state = new AlignmentState(phase, outcome, 3, "r");
        var block = OapaDevice.MapAlignment(state, sessionActive);
        block.Phase.Should().Be(expectedPhase);
        block.Reason.Should().Be(expectedReason);
        block.Moves.Should().Be(3);
    }

    [Test]
    public void Two_threads_polling_never_interleave_commands() {
        var (d, link, clock) = Connected();
        link.Expect("?", "<Idle|MPos:1.00,0.00,0.00|V:1.3.6|>", "ok");
        link.Expect("?", "<Idle|MPos:2.00,0.00,0.00|V:1.3.6|>", "ok");
        var gate = new ManualResetEventSlim();
        var results = new double?[2];
        var threads = new[] { 0, 1 }.Select(i => new Thread(() => {
            gate.Wait();
            results[i] = d.Status().Az.Position;
            clock.Advance(TimeSpan.FromMilliseconds(300));
        })).ToList();
        threads.ForEach(t => t.Start());
        gate.Set();
        threads.ForEach(t => t.Join());
        link.Sent.Skip(5).Should().AllBe("?"); // five commands at connect
        results.Should().NotContainNulls();
    }
}
