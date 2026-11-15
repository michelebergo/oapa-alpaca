# OAPA Alpaca server (Tier 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET 8 program that presents an OAPA board (USB serial or TCP) as an Alpaca Switch with the PolarAlign actions, Tier 2 only, so that the Polar Alignment Correctors plugin can run a TPPA session through Alpaca with today's firmware (1.3.6).

**Architecture:** `OapaAlpaca.Core` holds the contract: a line link to the board (`IOapaLink`), the OAPA frame parsing, and `OapaDevice`, which runs the connect sequence, translates the PolarAlign actions into OAPA commands and maps the board's state back (sections 3–4 of the spec). `OapaAlpaca` is an ASP.NET Core minimal-API host that puts the Alpaca envelope, the Switch members, discovery, a config file and a setup page around one `OapaDevice`. Tests drive `OapaDevice` through a scripted link and the host through `WebApplicationFactory`, so no board is needed until the last task.

**Tech Stack:** .NET 8 (SDK 8.0.416), ASP.NET Core minimal API, System.IO.Ports, NUnit 3.14 + FluentAssertions 6.12 + Microsoft.AspNetCore.Mvc.Testing 8.0.8 (the versions the Correctors plugin uses), Python 3 for the bench script.

**Spec:** `docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md` (sections 3, 4, 6 column "C# server", 7). Tier 1 (`$R=`, `fault`) is out of this plan: it follows the firmware command.

## Global Constraints

- Licence MPL-2.0; `LICENSE` and `NOTICE` as in `nina.plugin.aiweather`; the name "OAPA" and the author stay in the NOTICE.
- `Directory.Build.props` as in the Correctors plugin: `LangVersion 12.0`, `Nullable enable`, `ImplicitUsings enable`.
- All code, comments, commit messages and docs in English. No AI attribution trailers in commits.
- One command on the wire at a time; the status poll never overlaps another command (spec 4).
- `Status` may be answered from a cache no older than 250 ms (spec 4.5).
- Alpaca error numbers: NotImplemented 0x400, InvalidValue 0x401, NotConnected 0x407, InvalidOperation 0x40B, ActionNotImplemented 0x40C, driver failure 0x500. A lost link fails the HTTP request with 503 and is never an Alpaca error (spec 4.2).
- Units on the wire: arcminutes. `InterfaceVersion` 3. `specVersion` "0.1". `tiers` `[2]` in this plan.
- Default HTTP port 11113 (the Correctors simulator uses 11112). Discovery on UDP 32227.
- Connect never homes or calibrates and takes under two seconds (spec 4.1).

## Review Focus

1. A board that answers `$P?` with a plain `ok` (firmware before 1.3.3) must make `Connect` fail with "not an OAPA alignment device", not connect with empty capabilities. (Task 3)
2. Two clients polling at once (N.I.N.A. and ConformU, or the setup page) must never interleave their commands on the wire: `Status` from two threads yields two complete `?` exchanges, in order. (Task 4)
3. A board that restarts during a Tier 2 session (USB reset) comes back with `$L?` `phase:idle`; between `Begin` and `End` that must read as `failed` "the board lost the alignment", not as `idle`, or the plugin waits forever. (Task 5)
4. `Error` with a non-finite or absurd value (NaN, 1e9) must be refused with InvalidValue before anything reaches the board; `$E=nan,0` would be answered `error` and the session would silently stall. (Task 5)
5. The USB cable pulled while connected: `Status` must fail with 503 on every call and `connected` must keep answering true without touching the board, so the client decides whether to reconnect. (Task 8)

---

### Task 1: Repository, solution and the OAPA frames

**Files:**
- Create: `OapaAlpaca.sln`, `Directory.Build.props`, `LICENSE`, `NOTICE`, `README.md` (title and one paragraph), `.gitignore` (`dotnet new gitignore`)
- Create: `src/OapaAlpaca.Core/OapaAlpaca.Core.csproj` (net8.0, classlib)
- Create: `src/OapaAlpaca.Core/OapaFrame.cs`
- Create: `tests/OapaAlpaca.Core.Tests/OapaAlpaca.Core.Tests.csproj` (NUnit, FluentAssertions, Test.Sdk, project reference to Core)
- Test: `tests/OapaAlpaca.Core.Tests/OapaFrameTest.cs`

**Interfaces:**
- Produces:
  - `public sealed record OapaFrame(string Name, IReadOnlyDictionary<string, string> Fields)` with `static bool TryParse(string line, out OapaFrame frame)`; a line is a frame when it starts with `<` and ends with `|>`; the first field is the name, the rest `key:value` split on the first `:`; a value `-` stays `"-"`.
  - `public sealed record StatusFrame(string State, long X, long Y, string? Version)` with `static StatusFrame Parse(OapaFrame frame)` for `<Idle|MPos:12.00,-3.00,0.00|V:1.3.6|>` (`X`, `Y` rounded from the decimals; `Version` null when there is no `V`).
  - `public sealed record Calibration(bool Done, double? FactorX, int SignX, double? FactorY, int SignY)` with `static Calibration Parse(OapaFrame frame)` for `<K|state:done|x:12.34,+1|y:40.10,-1|...|>`; `Done` is `state == "done"`; `-` gives null factor and sign 0.
  - `public sealed record AlignmentState(string Phase, string Outcome, int Moves, string Reason)` with `static AlignmentState Parse(OapaFrame frame)` for `<L|phase:...|outcome:...|source:...|moves:N|...|reason:...|>`.

- [ ] **Step 1: Scaffold the solution**

Run from the repo root: `dotnet new sln -n OapaAlpaca`, `dotnet new classlib -n OapaAlpaca.Core -o src/OapaAlpaca.Core -f net8.0`, `dotnet new nunit -n OapaAlpaca.Core.Tests -o tests/OapaAlpaca.Core.Tests -f net8.0`, add both to the solution, add the project reference and the FluentAssertions 6.12.0 package to the tests, pin NUnit 3.14.0 / NUnit3TestAdapter 4.6.0 / Microsoft.NET.Test.Sdk 17.11.1. Copy `Directory.Build.props` from the Correctors plugin. Delete the generated `Class1.cs` and `UnitTest1.cs`.

- [ ] **Step 2: Write the failing tests**

```csharp
[Test] public void A_status_frame_parses_state_steps_and_version() {
    OapaFrame.TryParse("<Idle|MPos:12.00,-3.00,0.00|V:1.3.6|>", out var f).Should().BeTrue();
    var s = StatusFrame.Parse(f);
    s.Should().Be(new StatusFrame("Idle", 12, -3, "1.3.6"));
}
[Test] public void A_plain_ok_is_not_a_frame() => OapaFrame.TryParse("ok", out _).Should().BeFalse();
[Test] public void A_calibration_frame_parses_factors_and_signs() {
    OapaFrame.TryParse("<K|state:done|x:12.34,+1|y:40.10,-1|xplay:4.20,4.35,U|yplay:-|reason:Calibration complete|>", out var f);
    Calibration.Parse(f).Should().Be(new Calibration(true, 12.34, 1, 40.10, -1));
}
[Test] public void An_idle_calibration_has_no_factors() {
    OapaFrame.TryParse("<K|state:idle|x:-|y:-|xplay:-|yplay:-|reason:|>", out var f);
    Calibration.Parse(f).Should().Be(new Calibration(false, null, 0, null, 0));
}
[Test] public void An_alignment_frame_parses_phase_outcome_moves_and_reason() {
    OapaFrame.TryParse("<L|phase:moving_x|outcome:none|source:nina|moves:3|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Correction / capped|>", out var f);
    AlignmentState.Parse(f).Should().Be(new AlignmentState("moving_x", "none", 3, "Correction / capped"));
}
[Test] public void A_value_may_contain_a_colon() {
    OapaFrame.TryParse("<L|phase:ended|outcome:halted_runaway|source:nina|moves:9|az:-|alt:-|plan:0.00,0.00|reason:error grew: 3 times|>", out var f);
    f.Fields["reason"].Should().Be("error grew: 3 times");
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: build error, `OapaFrame` not defined.

- [ ] **Step 4: Implement `OapaFrame`, `StatusFrame`, `Calibration`, `AlignmentState` in `src/OapaAlpaca.Core/OapaFrame.cs`**

Numbers parse with `CultureInfo.InvariantCulture`. `Calibration.Parse` splits `x` on `,`: factor then sign (`+1`/`-1`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: 6 passed.

- [ ] **Step 6: Commit**

```bash
git init -b main && git add -A
git commit -m "Solution, licence and the OAPA reply frames"
```

---

### Task 2: The line link to the board

**Files:**
- Create: `src/OapaAlpaca.Core/IOapaLink.cs`, `src/OapaAlpaca.Core/TcpOapaLink.cs`, `src/OapaAlpaca.Core/SerialOapaLink.cs`, `src/OapaAlpaca.Core/LinkException.cs`
- Create: `tests/OapaAlpaca.Core.Tests/ScriptedLink.cs` (test double, used by every later Core test)
- Test: `tests/OapaAlpaca.Core.Tests/TcpOapaLinkTest.cs`

**Interfaces:**
- Produces:
  - `public interface IOapaLink : IDisposable { void Open(); void Close(); bool IsOpen { get; } void WriteLine(string text); string ReadLine(); string Description { get; } }`. `ReadLine` returns one line without `\n` and without a trailing `\r`; a silent board or a dead link throws `LinkException(string message, Exception? inner)`.
  - `public sealed class TcpOapaLink(string address) : IOapaLink` — `address` is `host` or `host:port`, default port 2323; `TcpClient` with `NoDelay`, 1000 ms read and write timeouts; `Description` is `"tcp://host:port"`.
  - `public sealed class SerialOapaLink(string portName) : IOapaLink` — `SerialPort` 115200, 8N1, `NewLine = "\n"`, `ReadTimeout = WriteTimeout = 1000`, `DtrEnable = true`; `Description` is the port name.
  - Test double `ScriptedLink : IOapaLink`: `Expect(string command, params string[] replyLines)` queues an exchange; `ReadLine` returns the queued lines for the last command written; an unexpected command throws; `Sent` is the list of commands written; `FailNext()` makes the next `ReadLine` throw `LinkException`.

- [ ] **Step 1: Write the failing tests (TCP against a local `TcpListener` fake)**

```csharp
[Test] public void Reads_a_line_without_its_line_ending() // fake answers "<P|protocol:1|>\r\n"
[Test] public void A_silent_board_throws_a_link_exception_within_two_seconds()
[Test] public void A_closed_socket_throws_a_link_exception_on_write()
[Test] public void The_address_may_omit_the_port() // Description == "tcp://127.0.0.1:2323" when the fake listens on 2323 is not required: assert the parsed port instead via a static TcpOapaLink.ParseAddress("192.168.1.40") == ("192.168.1.40", 2323)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests --filter TcpOapaLinkTest`
Expected: build error.

- [ ] **Step 3: Implement the interface, `LinkException`, `TcpOapaLink`, `SerialOapaLink` and `ScriptedLink`**

`TcpOapaLink.ReadLine` reads bytes until `\n` with the stream's `ReadTimeout`; a `TimeoutException` or `IOException` becomes `LinkException`. `SerialOapaLink` wraps `SerialPort.ReadLine`/`WriteLine` the same way and trims `\r`. The serial class is not unit tested: it is exercised in Task 10.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Line link to the board over TCP and serial, with a scripted link for tests"
```

---

### Task 3: `OapaDevice.Connect`

**Files:**
- Create: `src/OapaAlpaca.Core/DeviceException.cs`, `src/OapaAlpaca.Core/DeviceInfo.cs`, `src/OapaAlpaca.Core/OapaDevice.cs`, `src/OapaAlpaca.Core/IClock.cs`
- Test: `tests/OapaAlpaca.Core.Tests/OapaDeviceConnectTest.cs`, `tests/OapaAlpaca.Core.Tests/ManualClock.cs`

**Interfaces:**
- Consumes: `IOapaLink`, `ScriptedLink`, the frame records of Task 1.
- Produces:
  - `public enum DeviceErrorKind { NotConnected, NotImplemented, ActionNotImplemented, InvalidValue, InvalidOperation, Link, Driver }` and `public sealed class DeviceException(DeviceErrorKind kind, string message) : Exception`.
  - `public sealed record DeviceInfo(string FirmwareVersion, double MaxMove, double? ResolutionAz, double? ResolutionAlt, bool Calibrated)`.
  - `public interface IClock { DateTimeOffset UtcNow { get; } }` and `SystemClock.Instance`; tests use `ManualClock` with `Advance(TimeSpan)`.
  - `public sealed class OapaDevice(IOapaLink link, string name, IClock clock)` with `string Name`, `bool Connected`, `DeviceInfo Info` (throws NotConnected while disconnected), `void Connect()`, `void Disconnect()`. Every exchange with the board goes through one private `string[] Exchange(string command, int replyLines = 1)` under one lock; a `LinkException` becomes `DeviceException(Link)`.
  - Connect sequence (spec 4.1): `$P?` → frame `P` with `protocol >= 1`, else `DeviceException(Driver, "not an OAPA alignment device")` and the link is closed; `?` (two lines) → `FirmwareVersion`; `$M?` → `MaxMove` (120 on `ok`); `$K?` → `Calibrated` and resolutions `1/factor` (both null and `Calibrated=false` on `ok` or state not done).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Connect_runs_the_sequence_and_fills_info() {
    var link = new ScriptedLink();
    link.Expect("$P?", "<P|protocol:1|>"); link.Expect("?", "<Idle|MPos:0.00,0.00,0.00|V:1.3.6|>", "ok");
    link.Expect("$M?", "<M|max:180|>"); link.Expect("$K?", "<K|state:done|x:12.34,+1|y:40.10,-1|xplay:-|yplay:-|reason:|>");
    var d = new OapaDevice(link, "OAPA", new ManualClock()); d.Connect();
    d.Connected.Should().BeTrue();
    d.Info.Should().Be(new DeviceInfo("1.3.6", 180, 1 / 12.34, 1 / 40.10, true));
    link.Sent.Should().Equal("$P?", "?", "$M?", "$K?");
}
[Test] public void A_board_without_the_protocol_is_refused_and_the_link_closed() // "$P?" -> "ok": throws Driver "not an OAPA alignment device"; link.IsOpen false; Connected false
[Test] public void A_board_without_the_move_cap_reports_120() // "$M?" -> "ok"
[Test] public void An_uncalibrated_board_has_no_resolution() // "$K?" -> idle frame: ResolutionAz null, Calibrated false
[Test] public void Info_throws_not_connected_before_connect()
[Test] public void A_dead_link_during_connect_is_a_link_error() // link.FailNext(): throws DeviceException Kind Link; Connected false
[Test] public void Disconnect_closes_the_link() 
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests --filter OapaDeviceConnectTest`
Expected: build error.

- [ ] **Step 3: Implement `DeviceException`, `DeviceInfo`, `IClock`, `OapaDevice` (connect part)**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "OapaDevice: the connect sequence and the device info"
```

---

### Task 4: `OapaDevice.Status`

**Files:**
- Modify: `src/OapaAlpaca.Core/OapaDevice.cs`
- Create: `src/OapaAlpaca.Core/DeviceStatus.cs`
- Test: `tests/OapaAlpaca.Core.Tests/OapaDeviceStatusTest.cs`

**Interfaces:**
- Produces:
  - `public sealed record AxisStatus(bool Moving, double? Position)`, `public sealed record AlignmentBlock(string Phase, int Moves, string? Reason)`, `public sealed record DeviceStatus(AxisStatus Az, AxisStatus Alt, AlignmentBlock Alignment)`.
  - `DeviceStatus OapaDevice.Status()`: one `?` exchange, plus one `$L?` only while a Tier 2 session is active (between `Begin` and `End`, Task 5); outside a session the block is `("idle", 0, null)`. Answered from a cache when the last poll is less than 250 ms old (`IClock`). `Moving` is true for both axes when the state is `Run` or `Home`. `Position` is `steps / factor` in arcminutes per axis, null while not calibrated.
  - Mapping of `$L?` to `AlignmentBlock.Phase` (spec 4.4), implemented as `internal static AlignmentBlock MapAlignment(AlignmentState state, bool sessionActive)`:
    `idle` → `idle` outside a session, `failed` "the board lost the alignment" inside one; `waiting` → `waiting`; `moving_x`/`moving_y` → `moving`; `ended`+`finished`/`finished_best_effort` → `done`; `ended`+`halted_*` → `failed`; `ended`+`stopped_no_source` → `failed` "no readings for 30 s"; `ended`+`stopped_*` → `idle` when `End` was sent, else `failed` "stopped"; `Reason` is the frame's reason unless this table gives one; `Moves` is the frame's.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Status_reports_moving_and_positions_in_arcminutes() // calibrated board, "?" -> "<Run|MPos:1234.00,-401.00,0.00|V:1.3.6|>","ok": Az (true, 1234/12.34), Alt (true, -401/40.10)
[Test] public void Positions_are_null_while_not_calibrated()
[Test] public void A_second_status_within_250_ms_does_not_poll_the_board() // two Status(), clock +100 ms: Sent has one "?"
[Test] public void A_status_after_250_ms_polls_again() // clock +250 ms: two "?"
[Test] public void Outside_a_session_no_L_query_is_sent_and_the_block_is_idle()
[TestCase("idle","none",false,"idle")] [TestCase("idle","none",true,"failed")] [TestCase("waiting","none",true,"waiting")]
[TestCase("moving_x","none",true,"moving")] [TestCase("moving_y","none",true,"moving")]
[TestCase("ended","finished",true,"done")] [TestCase("ended","finished_best_effort",true,"done")]
[TestCase("ended","halted_runaway",true,"failed")] [TestCase("ended","halted_calibration_suspect",true,"failed")]
[TestCase("ended","stopped_no_source",true,"failed")] [TestCase("ended","stopped_by_user",true,"failed")]
public void The_alignment_phase_follows_the_table(string phase, string outcome, bool active, string expected)
[Test] public void Two_threads_polling_never_interleave_commands() // ScriptedLink expects "?" twice with distinct replies; run Status() on two threads with clock advanced between; Sent is exactly ["?","?"] and each call saw a whole frame
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests --filter OapaDeviceStatusTest`
Expected: build error.

- [ ] **Step 3: Implement `DeviceStatus` and `Status()` with the cache and `MapAlignment`**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "OapaDevice: status with positions, the 250 ms cache and the alignment phase table"
```

---

### Task 5: Tier 2, stop and home

**Files:**
- Modify: `src/OapaAlpaca.Core/OapaDevice.cs`
- Test: `tests/OapaAlpaca.Core.Tests/OapaDeviceTier2Test.cs`

**Interfaces:**
- Produces on `OapaDevice`:
  - `void Begin(double tolerance, double? maxMove)`: `$T=<t>`, then `$M=<m>` when given, then `$A=1`; `tolerance` outside `(0, 60]` or `maxMove` outside `[1, Info.MaxMove]` or non-finite → `InvalidValue` before anything is sent; marks the session active and clears the `End` flag.
  - `void Error(double az, double alt)`: non-finite or `|value| > 3600` → `InvalidValue`; otherwise `$E=<az>,<alt>` then `$L?`, which refreshes the alignment block in the cache. Numbers written with `F2` and the invariant culture.
  - `void End()`: `$A=0`; marks `End` sent and the session inactive.
  - `void Stop()`: `!`. `void Home()`: `$H`; a session, if active, is marked stopped by the host (spec: a hand move or stop ends an alignment the host is feeding).
  - `void Move(string axis, double arcmin)`: throws `NotImplemented` "Tier 1 needs firmware with $R=" (this plan).
  - A board reply `error` to any of these → `DeviceException(InvalidValue, "the board refused <command>")`. Every method throws `NotConnected` while disconnected.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Begin_sends_tolerance_cap_and_start_in_order() // Sent ends with "$T=1.50","$M=60","$A=1"
[Test] public void Begin_without_a_cap_sends_two_commands()
[TestCase(0)] [TestCase(61)] [TestCase(double.NaN)] public void Begin_refuses_a_bad_tolerance_before_sending(double t) // InvalidValue, Sent unchanged
[Test] public void Begin_refuses_a_cap_above_the_board_limit()
[Test] public void Error_sends_the_reading_and_refreshes_the_alignment_block() // "$E=-2.10,0.40" then "$L?" -> moving_x frame; Status() without clock advance reads Phase "moving", Moves 3, no new "?" 
[TestCase(double.NaN, 0)] [TestCase(1e9, 0)] [TestCase(0, double.PositiveInfinity)] public void Error_refuses_a_bad_value_before_sending(double az, double alt)
[Test] public void End_sends_stop_and_a_later_stopped_frame_reads_idle() // after End, Status with "$L?" not sent (session inactive) -> block idle
[Test] public void A_board_restart_during_a_session_reads_failed() // Begin, then "$L?" idle frame on next Status: Phase failed, Reason "the board lost the alignment"
[Test] public void Stop_sends_bang()
[Test] public void Home_sends_H()
[Test] public void Move_is_not_implemented_in_this_version() // DeviceException NotImplemented
[Test] public void A_refused_command_is_an_invalid_value() // "$T=5.00" -> "error"
[Test] public void A_dead_link_is_a_link_error_and_the_device_stays_connected() // FailNext before Error: Kind Link; Connected still true
[Test] public void Methods_throw_not_connected_before_connect()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests --filter OapaDeviceTier2Test`
Expected: build error.

- [ ] **Step 3: Implement `Begin`, `Error`, `End`, `Stop`, `Home`, `Move`**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "OapaDevice: Tier 2 session, stop and home over the OAPA protocol"
```

---

### Task 6: PolarAlign JSON and the action dispatcher

**Files:**
- Create: `src/OapaAlpaca.Core/PolarAlignJson.cs`, `src/OapaAlpaca.Core/PolarAlignActions.cs`
- Test: `tests/OapaAlpaca.Core.Tests/PolarAlignActionsTest.cs`

**Interfaces:**
- Produces:
  - `public static class PolarAlignJson`: `string Info(DeviceInfo info, string name)` → the JSON of spec 3.3 with `tiers:[2]`, `deviceName` = name, `deviceVersion` = firmware, both axes with `maxMove` = `Info.MaxMove`, `resolution` = the axis resolution or 0, `backlashHandled:true`, no `range`, `simultaneousMoves:true`, `movesWhileTracking:true`, `movesWhileSlewing:false`, `canStop:true`, `settleSeconds:0`; `string Status(DeviceStatus s)` → spec 3.4 without `fault`, `position` omitted when null; `(double tolerance, double? maxMove) ParseBegin(string json)`, `(double az, double alt) ParseError(string json)`, `(string axis, double arcmin) ParseMove(string json)` — a missing required field or invalid JSON → `DeviceException(InvalidValue)`. Numbers always with a dot (`Utf8JsonWriter` with invariant formatting).
  - `public static class PolarAlignActions`: `IReadOnlyList<string> Names` = `PolarAlign:Info`, `Status`, `Move`, `Stop`, `Home`, `Begin`, `Error`, `End`; `string Dispatch(OapaDevice device, string action, string parameters)`, case-insensitive on the name, returns the reply string (`""` for the empty replies); an unknown name → `DeviceException(ActionNotImplemented, "<name> is not implemented")`; `Stop` ignores an `axis` parameter (both axes stop, spec 4.2).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Info_json_matches_the_contract() // parse the output with JsonDocument: specVersion "0.1", tiers [2], axes.az.maxMove 180, axes.alt.resolution 0.0249 (1/40.10 to 4 places), backlashHandled true, no "range" property, canStop true
[Test] public void Status_json_omits_a_null_position_and_carries_the_alignment_block()
[Test] public void Numbers_are_written_with_a_dot_under_an_italian_culture() // CultureInfo.CurrentCulture = it-IT during the call
[Test] public void Begin_parses_tolerance_and_optional_max_move()
[Test] public void Error_parses_az_and_alt_and_ignores_time()
[Test] public void A_missing_field_is_an_invalid_value() // {"tolerance":1} fine; {} -> InvalidValue
[Test] public void Dispatch_routes_each_action_to_the_device() // scripted device connected; "polaralign:begin" with {"tolerance":2} -> Sent has "$T=2.00","$A=1"
[Test] public void An_unknown_action_is_action_not_implemented()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Core.Tests --filter PolarAlignActionsTest`
Expected: build error.

- [ ] **Step 3: Implement `PolarAlignJson` and `PolarAlignActions`**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OapaAlpaca.Core.Tests`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "PolarAlign actions: JSON of Info and Status, parameter parsing and the dispatcher"
```

---

### Task 7: The Alpaca host: envelope, management and the Switch

**Files:**
- Create: `src/OapaAlpaca/OapaAlpaca.csproj` (Microsoft.NET.Sdk.Web, net8.0, reference to Core), `src/OapaAlpaca/Program.cs`, `src/OapaAlpaca/Alpaca.cs` (envelope helpers), `src/OapaAlpaca/appsettings.json` (`"Urls": "http://0.0.0.0:11113"`)
- Create: `tests/OapaAlpaca.Tests/OapaAlpaca.Tests.csproj` (NUnit, FluentAssertions, Microsoft.AspNetCore.Mvc.Testing 8.0.8, references to OapaAlpaca and to the Core tests project for `ScriptedLink`), `tests/OapaAlpaca.Tests/ServerFactory.cs`
- Test: `tests/OapaAlpaca.Tests/SwitchHttpTest.cs`

**Interfaces:**
- Consumes: `OapaDevice`, `IOapaLink`, `DeviceException`.
- Produces:
  - `Program` registers one `IOapaLink` (from config, Task 9; in the `Testing` environment the factory replaces it with a `ScriptedLink`) and one `OapaDevice` singleton; `public partial class Program { }` for the test host.
  - `Alpaca.cs`: `static object Ok(HttpRequest r, object? value)`, `static object Fail(HttpRequest r, int number, string message)`, `static int ErrorNumber(DeviceErrorKind kind)` (0x400 NotImplemented, 0x401 InvalidValue, 0x407 NotConnected, 0x40B InvalidOperation, 0x40C ActionNotImplemented, 0x500 Driver), `static IResult Run(HttpRequest r, Func<object?> body)`: runs `body`, wraps the value, maps `DeviceException` to `Fail`, and returns HTTP 503 for `DeviceErrorKind.Link`. The envelope has PascalCase fields (`Value`, `ClientTransactionID`, `ServerTransactionID`, `ErrorNumber`, `ErrorMessage`); `ServerTransactionID` counts up; `ClientTransactionID` is read from the form or the query, 0 when absent or not a number.
  - Routes under `/api/v1/switch/0/`: `connected` GET/PUT, `connect`, `disconnect`, `connecting` (false), `name`, `description` ("OAPA polar alignment base"), `driverinfo` ("OAPA Alpaca server <version>"), `driverversion`, `interfaceversion` (3), `supportedactions`, `devicestate`, `maxswitch` (2), `getswitchname`, `getswitchdescription` ("Read only"), `canwrite` (false), `canasync` (false), `getswitch` (false), `getswitchvalue` (position; 0x400 while not calibrated), `minswitchvalue` (−10000), `maxswitchvalue` (10000), `switchstep` (resolution or 0), `statechangecomplete` (true), `setswitchname`/`setswitch`/`setswitchvalue`/`setasync`/`setasyncvalue` (0x400); any other member 0x400. `Id` outside 0–1 → 0x401.
  - Management: `/management/apiversions` `[1]`, `/management/v1/description`, `/management/v1/configureddevices` (one Switch, `DeviceNumber` 0, `UniqueID` from config).
  - `ServerFactory : WebApplicationFactory<Program>` sets the `Testing` environment and exposes the `ScriptedLink`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Connected_round_trips_and_runs_the_connect_sequence() // PUT connected=true; GET connected -> Value true; link.Sent starts "$P?"
[Test] public async Task The_envelope_echoes_the_client_transaction_id() // ?ClientTransactionID=42 -> 42; missing -> 0
[Test] public async Task Switch_values_are_the_positions_in_arcminutes_when_calibrated()
[Test] public async Task Switch_values_are_not_implemented_while_not_calibrated() // ErrorNumber 0x400
[Test] public async Task An_id_out_of_range_is_an_invalid_value() // getswitchname?Id=2 -> 0x401
[Test] public async Task Writes_are_not_implemented() // PUT setswitchvalue -> 0x400
[Test] public async Task Members_before_connect_are_not_connected() // getswitchvalue -> 0x407; name and interfaceversion still answer
[Test] public async Task Configured_devices_lists_one_switch()
[Test] public async Task A_lost_link_is_a_503_not_an_alpaca_error() // link.FailNext(); GET getswitchvalue -> HTTP 503
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Tests`
Expected: build error.

- [ ] **Step 3: Implement `Program.cs`, `Alpaca.cs` and `ServerFactory`**

Follow the Correctors simulator's `Program.cs` for the minimal-API shape and the PascalCase JSON options.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OapaAlpaca.sln`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Alpaca host: envelope, management endpoints and the Switch members"
```

---

### Task 8: Actions over HTTP

**Files:**
- Modify: `src/OapaAlpaca/Program.cs`
- Test: `tests/OapaAlpaca.Tests/ActionsHttpTest.cs`

**Interfaces:**
- Produces: `PUT /api/v1/switch/0/action` with form fields `Action` and `Parameters`, answered through `PolarAlignActions.Dispatch`; errors mapped as in Task 7; `supportedactions` returns `PolarAlignActions.Names`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Info_comes_back_as_the_contract_json() // Value parses; tiers [2]; deviceVersion "1.3.6"
[Test] public async Task A_tier_2_session_reaches_the_board_in_order() // Begin, Error, End -> link.Sent ends "$T=1.50","$A=1","$E=-2.10,0.40","$L?","$A=0"
[Test] public async Task Status_during_a_session_carries_the_alignment_block()
[Test] public async Task Move_is_not_implemented() // 0x400
[Test] public async Task An_unknown_action_is_0x40C()
[Test] public async Task A_bad_parameter_is_0x401() // Begin {"tolerance":0}
[Test] public async Task A_lost_link_during_an_action_is_a_503()
[Test] public async Task Connected_keeps_answering_after_the_link_died() // FailNext, action -> 503; GET connected -> true, no extra command sent
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OapaAlpaca.Tests --filter ActionsHttpTest`
Expected: failures (route missing → 0x400 from the fallback).

- [ ] **Step 3: Implement the `action` route**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OapaAlpaca.sln`
Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Alpaca host: the PolarAlign actions"
```

---

### Task 9: Config file, setup page and discovery

**Files:**
- Create: `src/OapaAlpaca/ServerConfig.cs`, `src/OapaAlpaca/AlpacaDiscovery.cs` (from the Correctors simulator, port from `Urls`), `src/OapaAlpaca/wwwroot/index.html`
- Modify: `src/OapaAlpaca/Program.cs`
- Test: `tests/OapaAlpaca.Tests/ServerConfigTest.cs`, `tests/OapaAlpaca.Tests/SetupHttpTest.cs`

**Interfaces:**
- Produces:
  - `public sealed record ServerConfig(string Link, string Address, string Name, string UniqueId)` with `static ServerConfig Load(string path)` and `void Save(string path)`: JSON file `config.json` next to the executable; defaults `Link="tcp"`, `Address="192.168.1.40"`, `Name="OAPA"`, `UniqueId` a new GUID generated on first load and saved at once. `Link` is `"serial"` or `"tcp"`; `static IOapaLink CreateLink(ServerConfig c)` picks `SerialOapaLink` or `TcpOapaLink`.
  - Routes: `GET /api/config` (the record), `POST /api/config` (saves; if the device is connected the change applies at the next connect and the reply says so: `{"applied":false}`), `GET /api/state` (`connected`, `firmware`, `calibrated`, `link` description, last error text), `GET /api/ports` (`SerialPort.GetPortNames()`).
  - `wwwroot/index.html`: one page, plain HTML and JS as the simulator's: link type, address or port (with the port list), device name, Save, and a state box refreshed every second. No framework.
  - Changing the config replaces the `IOapaLink` of the `OapaDevice` on the next `Connect`: `OapaDevice` gets `void UseLink(IOapaLink link)` allowed only while disconnected (`InvalidOperation` otherwise).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void A_missing_file_yields_defaults_with_a_fresh_unique_id_and_saves_it()
[Test] public void Save_then_load_round_trips()
[Test] public async Task Post_config_is_saved_and_applies_at_the_next_connect() // Testing env: config path in a temp dir
[Test] public async Task Post_config_while_connected_is_saved_but_not_applied() // applied false
[Test] public async Task State_reports_connection_and_firmware()
[Test] public void Use_link_while_connected_is_an_invalid_operation() // Core test, add to OapaDeviceConnectTest
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OapaAlpaca.sln --filter "ServerConfigTest|SetupHttpTest"`
Expected: build error.

- [ ] **Step 3: Implement `ServerConfig`, `UseLink`, the routes, the page and discovery; register `AlpacaDiscovery` outside the `Testing` environment**

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OapaAlpaca.sln`
Expected: all passed.

- [ ] **Step 5: Start the server by hand and check the page**

Run: `dotnet run --project src/OapaAlpaca`; open `http://localhost:11113/`.
Expected: the page shows the default config and "disconnected"; the Windows ASCOM chooser (Switch) lists "OAPA" through discovery.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Config file, setup page and Alpaca discovery"
```

---

### Task 10: Bench rehearsal against the real board

**Files:**
- Create: `tools/rehearsal.py` (Python 3, standard library only)
- Modify: `README.md` (how to run the server, the page, the rehearsal)

**Interfaces:**
- Produces: `python tools/rehearsal.py http://localhost:11113 --error 30,-20 --tolerance 1.5 [--scale-az 1 --scale-alt 1]`. The script connects, reads `Info` (asserts `tiers` contains 2), sends `Begin`, then loops: computes the "measured" error as the initial error plus the change of the device's reported `position` per axis since the start times the scale, plus 0.1′ noise, sends `Error`, polls `Status` at 4 Hz until the phase is `waiting`, `done` or `failed`, and repeats. Exit 0 when the phase is `done` and the synthetic error is within tolerance; exit 1 with the reason otherwise. It also checks `Stop` ends a move within one poll (second run with `--stop-after 1`), and that `Info` is identical after the session.

- [ ] **Step 1: Write the script**

- [ ] **Step 2: Run it against the bench board over TCP**

Precondition: the bench board (firmware 1.3.6, 192.168.1.40, calibrated) is powered, the server runs with `Link=tcp`.
Run: `python tools/rehearsal.py http://localhost:11113 --error 30,-20 --tolerance 1.5`
Expected: exit 0; the server log shows the `$T`, `$A=1`, `$E` and `$L?` exchanges; the board's page shows the alignment running.

- [ ] **Step 3: Run it over USB**

Set `Link=serial` and the COM port in the page; repeat step 2.
Expected: exit 0.

- [ ] **Step 4: Run ConformU on the Switch**

Run ConformU (download the Windows release from github.com/ASCOMInitiative/ConformU) against `http://localhost:11113`, device Switch 0, with the board connected and calibrated.
Expected: 0 errors, 0 issues; save the report under `docs/conformu/`.

- [ ] **Step 5: Update the README and commit**

```bash
git add -A && git commit -m "Bench rehearsal script, ConformU report and the README"
```

---

## Self-review notes

- Spec coverage: 3.1 Switch members (Task 7), 3.2 actions (Tasks 6, 8), 3.3 Info (Task 6), 3.4 Status without `fault` (Task 4), 4.1 connect (Task 3), 4.2 only `Stop`/`Home` and the refusals that exist without `$R=` (Task 5), 4.3 Tier 2 (Task 5), 4.4 table (Task 4), 4.5 polling (Task 4), 6 column "C# server" (Tasks 2, 9), 7.1 ConformU and 7.2 rehearsal (Task 10). Not in this plan, by design: `Move`, `fault`, `$R?` in connect, `tiers [1,2]`.
- Review Focus 1–5 are pinned by tests in Tasks 3, 4, 5, 5, 8.
- Names used across tasks: `OapaDevice.Status()`, `Begin/Error/End/Stop/Home/Move`, `DeviceInfo`, `DeviceStatus`, `AlignmentBlock`, `DeviceException(DeviceErrorKind)`, `ScriptedLink.Expect/Sent/FailNext`, `PolarAlignActions.Dispatch/Names`, `PolarAlignJson.Info/Status/ParseBegin/ParseError/ParseMove`, `ServerConfig.Load/Save/CreateLink`, `OapaDevice.UseLink`.
