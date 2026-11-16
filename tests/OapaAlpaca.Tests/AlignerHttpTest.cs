using System.Text.Json;
using FluentAssertions;
using OapaAlpaca.Core.Tests;

namespace OapaAlpaca.Tests;

/// <summary>The board as an Alpaca Aligner (ASCOM IAlignerV1 draft 1), next to the Switch with the actions.</summary>
public class AlignerHttpTest {
    private const string A = AlpacaClient.Aligner;
    private const string FailedR = "<R|state:failed|az:1.00|alt:0.00|reason:timeout AZ (X)|>";
    private const string MovingR = "<R|state:moving|az:1.00|alt:0.00|reason:|>";
    private const string RunStatus = "<Run|MPos:6.00,0.00,0.00|V:1.3.7|>";

    private static Task<JsonElement> Move(HttpClient client, string x, string y, string z = "0") =>
        client.PutAt(A, "move", ("X", x), ("Y", y), ("Z", z));

    [Test]
    public async Task The_server_lists_the_switch_and_the_aligner_as_two_devices_on_one_board() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        var devices = JsonDocument.Parse(await client.GetStringAsync("/management/v1/configureddevices")).RootElement.GetProperty("Value").EnumerateArray().ToArray();
        devices.Select(d => d.GetProperty("DeviceType").GetString()).Should().Equal("Switch", "Aligner");
        devices.Select(d => d.GetProperty("DeviceNumber").GetInt32()).Should().Equal(0, 0);
        devices.Select(d => d.GetProperty("UniqueID").GetString()).Distinct().Should().HaveCount(2);
        devices.Should().AllSatisfy(d => Guid.TryParse(d.GetProperty("UniqueID").GetString(), out _).Should().BeTrue());
        (await client.PutAt(A, "connected", ("Connected", "true"))).ErrorNumber().Should().Be(0);
        (await client.Get("connected")).GetProperty("Value").GetBoolean().Should().BeTrue("the aligner and the switch are the same board");
        (await client.GetAt(A, "interfaceversion")).GetProperty("Value").GetInt32().Should().Be(1);
        (await client.GetAt(A, "supportedactions")).GetProperty("Value").GetArrayLength().Should().Be(0);
        (await client.PutAt(A, "action", ("Action", "PolarAlign:Info"), ("Parameters", ""))).ErrorNumber().Should().Be(0x40C);
    }

    [Test]
    public async Task Configuration_is_one_object_in_degrees_with_the_board_cap_as_the_largest_move() {
        using var factory = ServerFactory.WithCalibratedBoard(maxMove: "<M|max:180|cap:30|>");
        var client = factory.CreateClient();
        (await client.GetAt(A, "configuration")).ErrorNumber().Should().Be(0x407);
        await client.PutAt(A, "connected", ("Connected", "true"));
        var c = (await client.GetAt(A, "configuration")).GetProperty("Value");
        c.GetProperty("AlignerPurpose").GetInt32().Should().Be(1);
        c.GetProperty("Units").GetInt32().Should().Be(1);
        c.GetProperty("CanMoveX").GetBoolean().Should().BeTrue();
        c.GetProperty("CanMoveY").GetBoolean().Should().BeTrue();
        c.GetProperty("CanMoveZ").GetBoolean().Should().BeFalse();
        c.GetProperty("CanFindHome").GetBoolean().Should().BeTrue();
        c.GetProperty("CanHalt").GetBoolean().Should().BeTrue();
        c.GetProperty("CanMoveAbsolute").GetBoolean().Should().BeFalse();
        c.GetProperty("CanSync").GetBoolean().Should().BeFalse();
        c.GetProperty("XMaxMove").GetDouble().Should().Be(0.5);
        c.GetProperty("YMaxMove").GetDouble().Should().Be(0.5);
        c.TryGetProperty("XMinPosition", out _).Should().BeFalse("the board does not know its travel");
        c.TryGetProperty("ZMaxMove", out _).Should().BeFalse();
        (await client.GetAt(A, "configurationversion")).GetProperty("Value").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task Move_in_degrees_reaches_the_board_as_one_relative_move_in_arcminutes() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Sent.Clear();
        factory.Link.Expect("$R=1.50,-2.00", "ok");
        var reply = await Move(client, "0.025", "-0.0333333333");
        reply.ErrorNumber().Should().Be(0);
        reply.TryGetProperty("Value", out _).Should().BeFalse("a method answers without a Value");
        factory.Link.Sent.Should().Equal("$R=1.50,-2.00");
    }

    [TestCase("1", "0", "0", "beyond the cap")]
    [TestCase("0", "0", "0.1", "Z is not an axis of this device")]
    [TestCase("abc", "0", "0", "not a number")]
    [TestCase("0.1", "NaN", "0", "not a number")]
    public async Task Move_refuses_an_amount_it_cannot_do_before_sending(string x, string y, string z, string why) {
        using var factory = ServerFactory.WithCalibratedBoard(maxMove: "<M|max:180|cap:30|>");
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Sent.Clear();
        (await Move(client, x, y, z)).ErrorNumber().Should().Be(0x401, why);
        factory.Link.Sent.Should().BeEmpty(why);
    }

    [Test]
    public async Task Move_without_every_axis_parameter_is_an_invalid_value() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        (await client.PutAt(A, "move", ("X", "0.1"), ("Y", "0"))).ErrorNumber().Should().Be(0x401);
    }

    [Test]
    public async Task Members_answer_not_connected_before_connecting() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.GetAt(A, "ismoving")).ErrorNumber().Should().Be(0x407);
        (await client.GetAt(A, "xposition")).ErrorNumber().Should().Be(0x407);
        (await Move(client, "0", "0")).ErrorNumber().Should().Be(0x407);
        (await client.PutAt(A, "halt")).ErrorNumber().Should().Be(0x407);
    }

    [Test]
    public async Task Is_moving_is_true_while_the_board_moves_and_throws_after_a_failed_move() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Expect("$R=1.00,0.00", "ok").Expect("?", RunStatus, "ok").Expect("$R?", MovingR);
        await Move(client, "0.0166666667", "0");
        (await client.GetAt(A, "ismoving")).GetProperty("Value").GetBoolean().Should().BeTrue();
        await Task.Delay(300); // past the status cache
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        var failed = await client.GetAt(A, "ismoving");
        failed.ErrorNumber().Should().Be(0x500);
        failed.GetProperty("ErrorMessage").GetString().Should().Contain("X").And.Contain("timeout AZ (X)");
        await Task.Delay(300);
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        (await client.GetAt(A, "ismoving")).ErrorNumber().Should().Be(0x500, "the failure stays until something clears it");
        factory.Link.Sent.Clear();
        (await Move(client, "0", "0")).ErrorNumber().Should().Be(0, "a zero move is valid and clears the failure");
        await Task.Delay(300);
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        (await client.GetAt(A, "ismoving")).GetProperty("Value").GetBoolean().Should().BeFalse();
        factory.Link.Sent.Should().Equal("?"); // the zero move never reached the board
    }

    [Test]
    public async Task Halt_clears_a_failure_and_find_home_homes_the_board() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        await Move(client, "0.0166666667", "0");
        (await client.GetAt(A, "ismoving")).ErrorNumber().Should().Be(0x500);
        factory.Link.Expect("!", "ok");
        (await client.PutAt(A, "halt")).ErrorNumber().Should().Be(0);
        await Task.Delay(300);
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        (await client.GetAt(A, "ismoving")).GetProperty("Value").GetBoolean().Should().BeFalse("after Halt the move is cancelled, not failed");
        factory.Link.Expect("$H", "ok").Expect("?", "<Home|MPos:0.00,0.00,0.00|V:1.3.7|>", "ok");
        (await client.PutAt(A, "findhome")).ErrorNumber().Should().Be(0);
        await Task.Delay(300);
        (await client.GetAt(A, "ismoving")).GetProperty("Value").GetBoolean().Should().BeTrue();
        factory.Link.Sent.Should().EndWith(new[] { "$H", "?" });
    }

    [Test]
    public async Task Positions_are_degrees_from_the_board_zero_and_z_is_not_implemented() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Expect("?", "<Idle|MPos:1234.00,-401.00,0.00|V:1.3.6|>", "ok");
        (await client.GetAt(A, "xposition")).GetProperty("Value").GetDouble().Should().BeApproximately(1234 / 12.34 / 60, 1e-9);
        (await client.GetAt(A, "yposition")).GetProperty("Value").GetDouble().Should().BeApproximately(-401 / 40.10 / 60, 1e-9);
        (await client.GetAt(A, "zposition")).ErrorNumber().Should().Be(0x400);
    }

    [Test]
    public async Task Positions_are_not_implemented_while_the_board_is_not_calibrated() {
        using var factory = ServerFactory.WithCalibratedBoard(OapaDeviceConnectTest.IdleK);
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Expect("?", OapaDeviceConnectTest.IdleStatus, "ok");
        (await client.GetAt(A, "xposition")).ErrorNumber().Should().Be(0x400);
        (await Move(client, "0.01", "0")).ErrorNumber().Should().Be(0x40B, "a move needs the factors");
    }

    [Test]
    public async Task Move_absolute_sync_and_the_raw_commands_are_not_implemented() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        await client.PutAt(A, "connected", ("Connected", "true"));
        (await client.PutAt(A, "moveabsolute", ("X", "0"), ("Y", "0"), ("Z", "0"))).ErrorNumber().Should().Be(0x400);
        (await client.PutAt(A, "sync", ("X", "0"), ("Y", "0"), ("Z", "0"))).ErrorNumber().Should().Be(0x400);
        (await client.PutAt(A, "commandblind", ("Command", "?"), ("Raw", "false"))).ErrorNumber().Should().Be(0x400);
    }

    [Test]
    public async Task Device_state_lists_the_operational_members_and_omits_is_moving_after_a_failure() {
        using var factory = ServerFactory.WithCalibratedBoard();
        var client = factory.CreateClient();
        (await client.GetAt(A, "devicestate")).GetProperty("Value").GetArrayLength().Should().Be(0);
        await client.PutAt(A, "connected", ("Connected", "true"));
        factory.Link.Expect("?", "<Idle|MPos:1234.00,-401.00,0.00|V:1.3.6|>", "ok");
        var state = (await client.GetAt(A, "devicestate")).GetProperty("Value").EnumerateArray().ToDictionary(e => e.GetProperty("Name").GetString()!, e => e.GetProperty("Value"));
        state.Keys.Should().BeEquivalentTo("TimeStamp", "IsMoving", "XPosition", "YPosition");
        state["IsMoving"].GetBoolean().Should().BeFalse();
        state["XPosition"].GetDouble().Should().BeApproximately(1234 / 12.34 / 60, 1e-9);
        await Task.Delay(300);
        factory.Link.Expect("$R=1.00,0.00", "ok").Expect("?", OapaDeviceConnectTest.IdleStatus, "ok").Expect("$R?", FailedR);
        await Move(client, "0.0166666667", "0");
        var after = (await client.GetAt(A, "devicestate")).GetProperty("Value").EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToArray();
        after.Should().BeEquivalentTo("TimeStamp", "XPosition", "YPosition");
    }
}
