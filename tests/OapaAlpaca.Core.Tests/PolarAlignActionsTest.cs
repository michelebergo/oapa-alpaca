using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class PolarAlignActionsTest {
    private static readonly DeviceInfo Calibrated = new("1.3.6", 180, 1 / 12.34, 1 / 40.10, true, true);

    [Test]
    public void Info_json_matches_the_contract() {
        using var doc = JsonDocument.Parse(PolarAlignJson.Info(Calibrated, "OAPA"));
        var root = doc.RootElement;
        root.GetProperty("specVersion").GetString().Should().Be("0.1");
        root.GetProperty("tiers").EnumerateArray().Select(t => t.GetInt32()).Should().Equal(1, 2);
        root.GetProperty("deviceName").GetString().Should().Be("OAPA");
        root.GetProperty("deviceVersion").GetString().Should().Be("1.3.6");
        var az = root.GetProperty("axes").GetProperty("az");
        az.GetProperty("maxMove").GetDouble().Should().Be(180);
        az.GetProperty("resolution").GetDouble().Should().BeApproximately(0.0810, 0.0001);
        az.GetProperty("backlashHandled").GetBoolean().Should().BeTrue();
        az.TryGetProperty("range", out _).Should().BeFalse();
        root.GetProperty("axes").GetProperty("alt").GetProperty("resolution").GetDouble().Should().BeApproximately(0.0249, 0.0001);
        root.GetProperty("simultaneousMoves").GetBoolean().Should().BeTrue();
        root.GetProperty("movesWhileTracking").GetBoolean().Should().BeTrue();
        root.GetProperty("movesWhileSlewing").GetBoolean().Should().BeFalse();
        root.GetProperty("canStop").GetBoolean().Should().BeTrue();
        root.GetProperty("settleSeconds").GetDouble().Should().Be(0);
    }

    [Test]
    public void A_board_without_relative_moves_offers_tier_2_only() {
        using var doc = JsonDocument.Parse(PolarAlignJson.Info(Calibrated with { CanMove = false }, "OAPA"));
        doc.RootElement.GetProperty("tiers").EnumerateArray().Select(t => t.GetInt32()).Should().Equal(2);
    }

    [Test]
    public void A_fault_is_written_on_its_axis_and_omitted_on_the_other() {
        var status = new DeviceStatus(new AxisStatus(false, 1, "timeout AZ (X)"), new AxisStatus(false, 1), new AlignmentBlock("idle", 0, null));
        using var doc = JsonDocument.Parse(PolarAlignJson.Status(status));
        doc.RootElement.GetProperty("az").GetProperty("fault").GetString().Should().Be("timeout AZ (X)");
        doc.RootElement.GetProperty("alt").TryGetProperty("fault", out _).Should().BeFalse();
    }

    [Test]
    public void An_uncalibrated_board_reports_zero_resolution() {
        using var doc = JsonDocument.Parse(PolarAlignJson.Info(new DeviceInfo("1.3.6", 120, null, null, false, true), "OAPA"));
        doc.RootElement.GetProperty("axes").GetProperty("az").GetProperty("resolution").GetDouble().Should().Be(0);
    }

    [Test]
    public void Status_json_omits_a_null_position_and_carries_the_alignment_block() {
        var status = new DeviceStatus(new AxisStatus(true, 12.5), new AxisStatus(false, null), new AlignmentBlock("moving", 3, "Correcting"));
        using var doc = JsonDocument.Parse(PolarAlignJson.Status(status));
        var root = doc.RootElement;
        root.GetProperty("az").GetProperty("moving").GetBoolean().Should().BeTrue();
        root.GetProperty("az").GetProperty("position").GetDouble().Should().Be(12.5);
        root.GetProperty("alt").TryGetProperty("position", out _).Should().BeFalse();
        root.GetProperty("alignment").GetProperty("phase").GetString().Should().Be("moving");
        root.GetProperty("alignment").GetProperty("moves").GetInt32().Should().Be(3);
        root.GetProperty("alignment").GetProperty("reason").GetString().Should().Be("Correcting");
    }

    [Test]
    public void A_null_reason_is_omitted() {
        var status = new DeviceStatus(new AxisStatus(false, 0), new AxisStatus(false, 0), new AlignmentBlock("idle", 0, null));
        using var doc = JsonDocument.Parse(PolarAlignJson.Status(status));
        doc.RootElement.GetProperty("alignment").TryGetProperty("reason", out _).Should().BeFalse();
    }

    [Test]
    public void Numbers_are_written_with_a_dot_under_an_italian_culture() {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try {
            PolarAlignJson.Info(Calibrated, "OAPA").Should().Contain("\"maxMove\":180").And.NotContain(",5");
            PolarAlignJson.Status(new DeviceStatus(new AxisStatus(false, 12.5), new AxisStatus(false, null), new AlignmentBlock("idle", 0, null)))
                .Should().Contain("\"position\":12.5");
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void Begin_parses_tolerance_and_optional_max_move() {
        PolarAlignJson.ParseBegin("{\"tolerance\":1.5,\"maxMove\":60}").Should().Be((1.5, (double?)60));
        PolarAlignJson.ParseBegin("{\"tolerance\":2}").Should().Be((2.0, (double?)null));
    }

    [Test]
    public void Error_parses_az_and_alt_and_ignores_time() =>
        PolarAlignJson.ParseError("{\"az\":-2.1,\"alt\":0.4,\"time\":\"2026-11-15T20:00:00Z\"}").Should().Be((-2.1, 0.4));

    [Test]
    public void Move_parses_axis_and_arcmin() => PolarAlignJson.ParseMove("{\"axis\":\"alt\",\"arcmin\":-1.25}").Should().Be(("alt", -1.25));

    [TestCase("{}")]
    [TestCase("{\"maxMove\":5}")]
    [TestCase("not json")]
    [TestCase("")]
    public void A_missing_field_or_bad_json_is_an_invalid_value(string json) {
        var act = () => PolarAlignJson.ParseBegin(json);
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.InvalidValue);
    }

    [Test]
    public void Names_lists_the_eight_actions() =>
        PolarAlignActions.Names.Should().Equal("PolarAlign:Info", "PolarAlign:Status", "PolarAlign:Move", "PolarAlign:Stop",
                                               "PolarAlign:Home", "PolarAlign:Begin", "PolarAlign:Error", "PolarAlign:End");

    [Test]
    public void Dispatch_routes_each_action_to_the_device() {
        var link = OapaDeviceConnectTest.CalibratedBoard();
        var device = new OapaDevice(link, "OAPA", new ManualClock());
        device.Connect();
        link.Sent.Clear();
        link.Expect("$T=2.00", "ok").Expect("$A=1", "ok");
        PolarAlignActions.Dispatch(device, "polaralign:begin", "{\"tolerance\":2}").Should().Be("");
        link.Expect("$E=1.00,-1.00", "ok").Expect("$L?", "<L|phase:waiting|outcome:none|source:nina|moves:1|az:1.00|alt:-1.00|plan:0.00,0.00|reason:|>");
        PolarAlignActions.Dispatch(device, "PolarAlign:Error", "{\"az\":1,\"alt\":-1}").Should().Be("");
        link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$L?", "<L|phase:waiting|outcome:none|source:nina|moves:1|az:-|alt:-|plan:0.00,0.00|reason:|>");
        PolarAlignActions.Dispatch(device, "PolarAlign:Status", "").Should().Contain("\"phase\":\"waiting\"");
        PolarAlignActions.Dispatch(device, "PolarAlign:Info", "").Should().Contain("\"specVersion\":\"0.1\"");
        link.Expect("$A=0", "ok");
        PolarAlignActions.Dispatch(device, "PolarAlign:End", "").Should().Be("");
        link.Expect("!", "ok");
        PolarAlignActions.Dispatch(device, "PolarAlign:Stop", "{\"axis\":\"az\"}").Should().Be("");
        link.Expect("$H", "ok");
        PolarAlignActions.Dispatch(device, "PolarAlign:Home", "").Should().Be("");
        link.Sent.Should().Equal("$T=2.00", "$A=1", "$E=1.00,-1.00", "$L?", "?", "$L?", "$A=0", "!", "$H");
    }

    [Test]
    public void Dispatch_move_answers_with_the_arcminutes_the_board_will_move() {
        var link = OapaDeviceConnectTest.CalibratedBoard();
        var device = new OapaDevice(link, "OAPA", new ManualClock());
        device.Connect();
        link.Expect("$R=1.00,0.00", "ok");
        var reply = PolarAlignActions.Dispatch(device, "PolarAlign:Move", "{\"axis\":\"az\",\"arcmin\":1}");
        using var doc = JsonDocument.Parse(reply);
        doc.RootElement.GetProperty("arcmin").GetDouble().Should().BeApproximately(12 / 12.34, 1e-9);
    }

    [Test]
    public void An_unknown_action_is_action_not_implemented() {
        var device = new OapaDevice(new ScriptedLink(), "OAPA", new ManualClock());
        var act = () => PolarAlignActions.Dispatch(device, "PolarAlign:Calibrate", "");
        act.Should().Throw<DeviceException>().Where(e => e.Kind == DeviceErrorKind.ActionNotImplemented);
    }
}
