using System.Net;
using System.Text.Json;
using FluentAssertions;
using OapaAlpaca.Core.Tests;

namespace OapaAlpaca.Tests;

public class SwitchHttpTest {
    [Test]
    public async Task Connected_round_trips_and_runs_the_connect_sequence() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.Get("connected")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await client.Connect()).ErrorNumber().Should().Be(0);
        (await client.Get("connected")).GetProperty("Value").GetBoolean().Should().BeTrue();
        factory.Link.Sent.Should().Equal("$P?", "?", "$M?", "$R?", "$K?");
    }

    [Test]
    public async Task The_envelope_echoes_the_client_transaction_id() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var withId = await client.Get("interfaceversion", "ClientTransactionID=42&ClientID=7");
        withId.GetProperty("ClientTransactionID").GetUInt32().Should().Be(42);
        withId.GetProperty("Value").GetInt32().Should().Be(3);
        var without = await client.Get("interfaceversion");
        without.GetProperty("ClientTransactionID").GetUInt32().Should().Be(0);
        without.GetProperty("ServerTransactionID").GetUInt32().Should().BeGreaterThan(withId.GetProperty("ServerTransactionID").GetUInt32());
    }

    [Test]
    public async Task Switch_values_are_the_positions_in_arcminutes_when_calibrated() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Expect("?", "<Idle|MPos:1234.00,-401.00,0.00|V:1.3.6|>", "ok");
        (await client.Get("getswitchvalue", "Id=0")).GetProperty("Value").GetDouble().Should().BeApproximately(1234 / 12.34, 1e-9);
        (await client.Get("getswitchvalue", "Id=1")).GetProperty("Value").GetDouble().Should().BeApproximately(-401 / 40.10, 1e-9);
        (await client.Get("getswitchname", "Id=0")).GetProperty("Value").GetString().Should().Be("Azimuth position (arcmin)");
        (await client.Get("maxswitch")).GetProperty("Value").GetInt32().Should().Be(2);
        (await client.Get("switchstep", "Id=1")).GetProperty("Value").GetDouble().Should().Be(1.0 / 64);
    }

    [Test]
    public async Task Switch_values_are_not_implemented_while_not_calibrated() {
        using var factory = ServerFactory.WithCalibratedBoard(OapaDeviceConnectTest.IdleK);
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        (await client.Get("getswitchvalue", "Id=0")).ErrorNumber().Should().Be(0x400);
        (await client.Get("switchstep", "Id=0")).GetProperty("Value").GetDouble().Should().Be(1.0 / 64);
    }

    [TestCase(OapaDeviceConnectTest.CalibratedK)]
    [TestCase(OapaDeviceConnectTest.IdleK)]
    public async Task The_switch_range_is_an_integer_multiple_of_a_positive_step(string calibration) {
        // ConformU's ISwitchV3 rule: (MaxSwitchValue - MinSwitchValue) / SwitchStep must be a whole number, and the step must be > 0.
        using var factory = ServerFactory.WithCalibratedBoard(calibration);
        var client = factory.CreateClient();
        await client.Connect();
        foreach (var id in new[] { "0", "1" }) {
            var min = (await client.Get("minswitchvalue", $"Id={id}")).GetProperty("Value").GetDouble();
            var max = (await client.Get("maxswitchvalue", $"Id={id}")).GetProperty("Value").GetDouble();
            var step = (await client.Get("switchstep", $"Id={id}")).GetProperty("Value").GetDouble();
            // 1/64 arcminute is exactly representable, so ConformU's floating-point remainder is exactly zero.
            step.Should().Be(1.0 / 64);
            ((max - min) % step).Should().Be(0, $"switch {id}: range {min}..{max} step {step}");
            max.Should().Be(10000);
            min.Should().Be(-10000);
        }
    }

    [Test]
    public async Task Device_state_lists_only_the_operational_switch_properties() {
        // ISwitchV3 DeviceState: GetSwitch{n}, GetSwitchValue{n}, StateChangeComplete{n} and TimeStamp, nothing else (ConformU).
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.Get("devicestate")).GetProperty("Value").GetArrayLength().Should().Be(0);
        await client.Connect();
        factory.Link.Expect("?", "<Idle|MPos:1234.00,-401.00,0.00|V:1.3.6|>", "ok");
        var names = (await client.Get("devicestate")).GetProperty("Value").EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
        names.Should().Contain("TimeStamp").And.Contain("GetSwitchValue0").And.Contain("GetSwitchValue1")
             .And.Contain("StateChangeComplete0").And.Contain("GetSwitch0");
        names.Should().OnlyContain(n => n == "TimeStamp" || System.Text.RegularExpressions.Regex.IsMatch(n!, "^(GetSwitch|GetSwitchValue|StateChangeComplete)[01]$"));
    }

    [TestCase("setswitch")]
    [TestCase("setswitchvalue")]
    [TestCase("setasync")]
    [TestCase("setasyncvalue")]
    [TestCase("cancelasync")]
    public async Task Writes_check_the_id_before_refusing(string member) {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        (await client.Put(member, ("Id", "2"), ("Value", "1"), ("State", "true"))).ErrorNumber().Should().Be(0x401);
        (await client.Put(member, ("Id", "-1"), ("Value", "1"), ("State", "true"))).ErrorNumber().Should().Be(0x401);
        (await client.Put(member, ("Id", "0"), ("Value", "1"), ("State", "true"))).ErrorNumber().Should().Be(0x400);
    }

    [Test]
    public async Task An_id_out_of_range_is_an_invalid_value() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        (await client.Get("getswitchname", "Id=2")).ErrorNumber().Should().Be(0x401);
        (await client.Get("getswitchname", "Id=-1")).ErrorNumber().Should().Be(0x401);
    }

    [Test]
    public async Task Writes_are_not_implemented() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        (await client.Put("setswitchvalue", ("Id", "0"), ("Value", "1"))).ErrorNumber().Should().Be(0x400);
        (await client.Put("setswitch", ("Id", "0"), ("State", "true"))).ErrorNumber().Should().Be(0x400);
        (await client.Get("canwrite", "Id=0")).GetProperty("Value").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Members_before_connect_are_not_connected() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.Get("getswitchvalue", "Id=0")).ErrorNumber().Should().Be(0x407);
        (await client.Get("name")).GetProperty("Value").GetString().Should().StartWith("OAPA");
        (await client.Get("description")).GetProperty("Value").GetString().Should().Be("OAPA polar alignment base");
        (await client.Get("supportedactions")).GetProperty("Value").EnumerateArray().Should().HaveCount(8);
    }

    [Test]
    public async Task An_unknown_member_is_not_implemented() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.Get("temperature")).ErrorNumber().Should().Be(0x400);
    }

    [Test]
    public async Task Configured_devices_lists_the_switch_first() {
        // The Aligner that follows it is covered by AlignerHttpTest.
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/management/v1/configureddevices");
        var devices = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("Value");
        devices[0].GetProperty("DeviceType").GetString().Should().Be("Switch");
        devices[0].GetProperty("DeviceNumber").GetInt32().Should().Be(0);
        devices[0].GetProperty("UniqueID").GetString().Should().NotBeNullOrEmpty();
        var versions = await client.GetAsync("/management/apiversions");
        JsonDocument.Parse(await versions.Content.ReadAsStringAsync()).RootElement.GetProperty("Value")[0].GetInt32().Should().Be(1);
    }

    [Test]
    public async Task A_lost_link_is_a_503_not_an_alpaca_error() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.FailNext();
        var response = await client.GetAsync(AlpacaClient.Device + "getswitchvalue?Id=0");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task A_failed_connect_reports_the_driver_error() {
        using var factory = new ServerFactory();
        factory.Link.Expect("$P?", "ok");
        var client = factory.CreateClient();
        var reply = await client.Connect();
        reply.ErrorNumber().Should().Be(0x500);
        reply.GetProperty("ErrorMessage").GetString().Should().Contain("not an OAPA alignment device");
    }
}
