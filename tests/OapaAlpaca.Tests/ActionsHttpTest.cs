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
        info.GetProperty("tiers").EnumerateArray().Select(t => t.GetInt32()).Should().Equal(2);
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
    public async Task Move_is_not_implemented() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.Connect();
        var reply = await Action(client, "PolarAlign:Move", "{\"axis\":\"az\",\"arcmin\":1}");
        reply.ErrorNumber().Should().Be(0x400);
        reply.GetProperty("ErrorMessage").GetString().Should().Contain("$R=");
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
