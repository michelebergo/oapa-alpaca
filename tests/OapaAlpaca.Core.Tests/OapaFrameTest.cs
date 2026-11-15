using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class OapaFrameTest {
    [Test]
    public void A_status_frame_parses_state_steps_and_version() {
        OapaFrame.TryParse("<Idle|MPos:12.00,-3.00,0.00|V:1.3.6|>", out var f).Should().BeTrue();
        StatusFrame.Parse(f).Should().Be(new StatusFrame("Idle", 12, -3, "1.3.6"));
    }

    [Test]
    public void A_status_frame_without_a_version_has_a_null_version() {
        OapaFrame.TryParse("<Run|MPos:1234.00,-401.00,0.00|>", out var f).Should().BeTrue();
        StatusFrame.Parse(f).Should().Be(new StatusFrame("Run", 1234, -401, null));
    }

    [Test]
    public void A_plain_ok_is_not_a_frame() => OapaFrame.TryParse("ok", out _).Should().BeFalse();

    [Test]
    public void A_calibration_frame_parses_factors_and_signs() {
        OapaFrame.TryParse("<K|state:done|x:12.34,+1|y:40.10,-1|xplay:4.20,4.35,U|yplay:-|reason:Calibration complete|>", out var f);
        Calibration.Parse(f).Should().Be(new Calibration(true, 12.34, 1, 40.10, -1));
    }

    [Test]
    public void An_idle_calibration_has_no_factors() {
        OapaFrame.TryParse("<K|state:idle|x:-|y:-|xplay:-|yplay:-|reason:|>", out var f);
        Calibration.Parse(f).Should().Be(new Calibration(false, null, 0, null, 0));
    }

    [Test]
    public void An_alignment_frame_parses_phase_outcome_moves_and_reason() {
        OapaFrame.TryParse("<L|phase:moving_x|outcome:none|source:nina|moves:3|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Correction / capped|>", out var f);
        AlignmentState.Parse(f).Should().Be(new AlignmentState("moving_x", "none", 3, "Correction / capped"));
    }

    [Test]
    public void A_value_may_contain_a_colon() {
        OapaFrame.TryParse("<L|phase:ended|outcome:halted_runaway|source:nina|moves:9|az:-|alt:-|plan:0.00,0.00|reason:error grew: 3 times|>", out var f);
        f.Fields["reason"].Should().Be("error grew: 3 times");
    }
}
