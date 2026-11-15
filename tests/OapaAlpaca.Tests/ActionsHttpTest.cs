using System.Net;
using System.Text.Json;
using FluentAssertions;
using OapaAlpaca.Core.Tests;

namespace OapaAlpaca.Tests;

public class ActionsHttpTest {
    private const string WaitingL = "<L|phase:waiting|outcome:none|source:nina|moves:1|az:1.00|alt:-1.00|plan:0.00,0.00|reason:|>";

    private static Task<JsonElement> Action(HttpClient client, string action, string parameters = "") =>
        client.Put("action", ("Action", action), ("Parameters", parameters));

    [Test]
    public async Task Info_comes_back_as_the_contract_json() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        var reply = await Action(client, "PolarAlign:Info");
        reply.ErrorNumber().Should().Be(0);
        var info = JsonDocument.Parse(reply.GetProperty("Value").GetString()!).RootElement;
        info.GetProperty("tiers").EnumerateArray().Select(t => t.GetInt32()).Should().Equal(1, 2);
        info.GetProperty("deviceVersion").GetString().Should().Be("1.3.6");
        info.GetProperty("axes").GetProperty("az").GetProperty("maxMove").GetDouble().Should().Be(180);
    }

    [Test]
    public async Task A_tier_2_session_reaches_the_board_in_order() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Sent.Clear();
        factory.Link.Expect("$T=1.50", "ok").Expect("$A=1", "ok").Expect("$E=-2.10,0.40", "ok").Expect("$L?", WaitingL).Expect("$A=0", "ok");
        (await Action(client, "PolarAlign:Begin", "{\"tolerance\":1.5}")).ErrorNumber().Should().Be(0);
        (await Action(client, "PolarAlign:Error", "{\"az\":-2.1,\"alt\":0.4}")).ErrorNumber().Should().Be(0);
        (await Action(client, "PolarAlign:End")).ErrorNumber().Should().Be(0);
        factory.Link.Sent.Should().Equal("$T=1.50", "$A=1", "$E=-2.10,0.40", "$L?", "$A=0");
    }

    [Test]
    public async Task Status_during_a_session_carries_the_alignment_block() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Expect("$T=1.00", "ok").Expect("$A=1", "ok").Expect("$E=1.00,-1.00", "ok").Expect("$L?", WaitingL);
        await Action(client, "PolarAlign:Begin", "{\"tolerance\":1}");
        await Action(client, "PolarAlign:Error", "{\"az\":1,\"alt\":-1}");
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$L?", WaitingL);
        var status = JsonDocument.Parse((await Action(client, "PolarAlign:Status")).GetProperty("Value").GetString()!).RootElement;
        status.GetProperty("alignment").GetProperty("phase").GetString().Should().Be("waiting");
        status.GetProperty("alignment").GetProperty("moves").GetInt32().Should().Be(1);
        status.GetProperty("az").GetProperty("moving").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Move_reaches_the_board_and_answers_with_the_arcminutes() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Sent.Clear();
        factory.Link.Expect("$R=0.00,-2.00", "ok");
        var reply = await Action(client, "PolarAlign:Move", "{\"axis\":\"alt\",\"arcmin\":-2}");
        reply.ErrorNumber().Should().Be(0);
        JsonDocument.Parse(reply.GetProperty("Value").GetString()!).RootElement.GetProperty("arcmin").GetDouble().Should().BeApproximately(-80 / 40.10, 1e-9);
        factory.Link.Sent.Should().Equal("$R=0.00,-2.00");
    }

    [Test]
    public async Task Move_on_a_board_without_the_command_is_0x400() {
        using var factory = ServerFactory.WithCalibratedBoard(relative: "ok");
        var client = factory.CreateClient();
        await client.Connect();
        var reply = await Action(client, "PolarAlign:Move", "{\"axis\":\"az\",\"arcmin\":1}");
        reply.ErrorNumber().Should().Be(0x400);
        reply.GetProperty("ErrorMessage").GetString().Should().Contain("$R=");
    }

    [Test]
    public async Task Move_on_an_uncalibrated_board_is_0x40B() {
        using var factory = ServerFactory.WithCalibratedBoard(calibration: OapaDeviceConnectTest.IdleK);
        var client = factory.CreateClient();
        await client.Connect();
        var reply = await Action(client, "PolarAlign:Move", "{\"axis\":\"az\",\"arcmin\":1}");
        reply.ErrorNumber().Should().Be(0x40B);
        reply.GetProperty("ErrorMessage").GetString().Should().Contain("not calibrated");
    }

    [Test]
    public async Task Move_beyond_the_cap_is_0x401_and_nothing_is_sent() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        factory.Link.Sent.Clear();
        (await Action(client, "PolarAlign:Move", "{\"axis\":\"az\",\"arcmin\":181}")).ErrorNumber().Should().Be(0x401);
        factory.Link.Sent.Should().BeEmpty();
    }

    [Test]
    public async Task An_unknown_action_is_0x40C() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        (await Action(client, "PolarAlign:Calibrate")).ErrorNumber().Should().Be(0x40C);
    }

    [Test]
    public async Task A_bad_parameter_is_0x401() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        (await Action(client, "PolarAlign:Begin", "{\"tolerance\":0}")).ErrorNumber().Should().Be(0x401);
        (await Action(client, "PolarAlign:Error", "{\"az\":1}")).ErrorNumber().Should().Be(0x401);
    }

    [Test]
    public async Task Actions_before_connect_are_not_connected() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await Action(client, "PolarAlign:Info")).ErrorNumber().Should().Be(0x407);
    }

    [Test]
    public async Task A_lost_link_during_an_action_is_a_503_and_connected_still_answers() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        var sent = factory.Link.Sent.Count;
        factory.Link.FailNext();
        var response = await client.PutAsync(AlpacaClient.Device + "action",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Action"] = "PolarAlign:Stop", ["Parameters"] = "" }));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.Get("connected")).GetProperty("Value").GetBoolean().Should().BeTrue();
        factory.Link.Sent.Count.Should().Be(sent + 1); // the Stop that died, nothing for "connected"
    }
}
