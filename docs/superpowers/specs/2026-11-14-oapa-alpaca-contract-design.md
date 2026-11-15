# OAPA over Alpaca — the contract

Date: 2026-11-14. Author: Michele Bergo. Status: draft for review.

One document for the three implementations of the OAPA Alpaca driver: the C# server on
the PC, the C++ driver in OpenAstro AlpacaBridge, and the Alpaca server inside the ESP32
firmware. Each one presents an OAPA board as the same Alpaca device, speaking the same
actions, translated into the same OAPA protocol commands. What differs between them is
listed at the end; everything else is this document.

Once approved, the public copy goes to `oapa-firmware/ALPACA.md`, next to `PROTOCOL.md`,
which it extends with two optional commands.

## 1. Purpose

Make an OAPA board usable from any program that speaks ASCOM or Alpaca, through the
PolarAlign actions that N.I.N.A.'s Polar Alignment Correctors plugin already drives, and
give the ASCOM Initiative a working device for the interface Peter Simpson is writing up.

Success: the Correctors plugin completes a TPPA session against a real board through each
implementation, in both tiers; ConformU passes on the Switch; the same bench rehearsal
script passes against all three.

## 2. Decisions

| Topic | Decision |
|---|---|
| Carrier | Alpaca **Switch**, interface version 3, plus the **PolarAlign actions** (OpenAstroTech draft v0.1) in `SupportedActions`. The IAxisAligner type comes later behind the same code. |
| Tier 1 moves | A new optional firmware command, `$R=`, moves in arcminutes: the board converts with its own factors and compensates backlash. No conversion in the driver. |
| Azimuth sign | Positive azimuth turns the polar axis **clockwise seen from above** (the azimuth coordinate increases: east in the north, west in the south). Positive altitude **raises** the polar axis. A device executes this without knowing the hemisphere. This settles point D1 of `PolarAlignActionsComparison.md` in OpenAstroTech's favour; the IAxisAligner draft follows. |
| Units on the wire | Arcminutes, as PolarAlign and TPPA use them. Degrees belong to the future ASCOM type; the conversion is a factor of 60 in one place. |
| Not calibrated | `Move` is refused with `InvalidOperation` until the board has factors (from a Tier 2 session, or `$F=` entered by the user). Tier 2 works from the first session, because the board calibrates itself. |
| Failure during a move | Reported in `Status` as a `fault` string on the axis, an extension to the draft (its point D6), ignored by hosts that don't know it. |
| Identity | `UniqueID` stable across restarts; where it comes from is per implementation. |
| Licence | MPL-2.0 for the C# server and the firmware part; AGPL-3.0 for the AlpacaBridge driver, as that repository requires. |

## 3. The device as a client sees it

### 3.1 Switch members

| Member | Value |
|---|---|
| `InterfaceVersion` | 3 |
| `Name` | `OAPA` followed by the board's network name when known |
| `Description` | `OAPA polar alignment base` |
| `DriverInfo` | implementation name and version |
| `MaxSwitch` | 2 |
| switch 0 | `Azimuth position (arcmin)`, read only |
| switch 1 | `Altitude position (arcmin)`, read only |
| `GetSwitchValue` | position in arcminutes from the board's zero; `PropertyNotImplemented` (0x400) while the board has no factors |
| `MinSwitchValue`, `MaxSwitchValue` | −10000, 10000 |
| `SwitchStep` | arcminutes per motor step, or 0 while not calibrated |
| `CanWrite`, `GetSwitch`, `SetSwitch*` | false / not implemented |
| `DeviceState` | empty list while disconnected; `TimeStamp` plus the two positions when calibrated |

The two switches exist so that a client that knows nothing about the actions still shows
something sensible. Nothing moves through them.

### 3.2 Actions

All in `SupportedActions`, names case-insensitive, parameters and replies JSON as in the
PolarAlign draft and `PolarAlignJson` in the Correctors plugin.

| Action | Parameters | Reply |
|---|---|---|
| `PolarAlign:Info` | — | capabilities, section 3.3 |
| `PolarAlign:Status` | — | per-axis `moving`, `position`, `fault`; `alignment` block |
| `PolarAlign:Move` | `{"axis":"az"\|"alt","arcmin":x}` | `{"arcmin": commanded}` |
| `PolarAlign:Stop` | `{"axis":...}` or empty | empty |
| `PolarAlign:Home` | — | empty |
| `PolarAlign:Begin` | `{"tolerance":t,"maxMove":m}` | empty |
| `PolarAlign:Error` | `{"az":a,"alt":b,"time":...}` | empty |
| `PolarAlign:End` | — | empty |

Any other action name throws `ActionNotImplemented` (0x40C in Alpaca's list, 0x400 is
also accepted by clients; use 0x40C).

### 3.3 Capabilities (`Info`)

```json
{
  "specVersion": "0.1",
  "tiers": [1, 2],
  "deviceName": "OAPA",
  "deviceVersion": "<firmware V field>",
  "axes": {
    "az":  { "maxMove": 120, "resolution": 0.0123, "backlashHandled": true },
    "alt": { "maxMove": 120, "resolution": 0.0145, "backlashHandled": true }
  },
  "simultaneousMoves": true,
  "movesWhileTracking": true,
  "movesWhileSlewing": false,
  "canStop": true,
  "settleSeconds": 0
}
```

- `maxMove`: the board's move cap from `$M?` (120 when the board has no `$M?`). It bounds
  one `Move` call and the Tier 2 corrections alike.
- `resolution`: arcminutes per step from the calibration factors; 0 while not calibrated.
- `range` is omitted: an OAPA board does not know its travel.
- `tiers` contains 1 only when the board answers `$R?`; a board without it (firmware before
  the command) reports `[2]`.

### 3.4 Status

```json
{
  "az":  { "moving": false, "position": 12.4 },
  "alt": { "moving": true,  "position": -3.1 },
  "alignment": { "phase": "moving", "moves": 3, "reason": "Correcting" }
}
```

- `moving`: the board's state is `Run` or `Home`, or `$R?` says `moving`, for that axis.
- `position`: `MPos` steps divided by the factor, with the mechanical sign; omitted while
  not calibrated.
- `fault`: present on an axis after a `Move` that failed (stall, end of travel, timeout),
  with the board's reason; cleared by the next `Move`, `Stop` or `Home` on that axis.
- `alignment`: present always (Tier 2 is always available); mapping in 4.4.

## 4. Mapping onto the OAPA protocol

The driver owns the serial or TCP link and serialises every exchange. One command, one
reply; the status poll (`?`) never overlaps another command.

### 4.1 Connect

1. `$P?` → must answer `<P|protocol:N|>` with N ≥ 1; otherwise `Connect` fails with
   `DriverException` "not an OAPA alignment device".
2. `?` → firmware `V` for `deviceVersion`.
3. `$M?` → `maxMove` (120 if plain `ok`).
4. `$R?` → tier 1 available if framed, else `tiers: [2]`.
5. `$K?` → factors; `resolution` and positions available when `state:done`.

Connect takes under two seconds. It never homes or calibrates.

### 4.2 Tier 1

| Action | Commands | Notes |
|---|---|---|
| `Move az x` | `$R=x,0` | arcminutes, sign of section 2 |
| `Move alt y` | `$R=0,y` | |
| `Stop` | `!` | both axes; a per-axis stop is a stop of both |
| `Home` | `$H` | `moving` until the state leaves `Home` |

Refusals, decided before anything moves:

| Case | Board | Alpaca |
|---|---|---|
| not calibrated | `$R=` answers `error`; `$K?` not `done` | `InvalidOperation` 0x40B, "not calibrated: run an alignment with the controller first, or enter the factors" |
| \|x\| > maxMove, or not a number | `error` | `InvalidValue` 0x401 |
| an axis already moving | `error` | `InvalidOperation` 0x40B |
| link lost | no reply | the HTTP request fails (503 / connection closed), never an Alpaca error |

The reply `arcmin` is x rounded to whole steps, computed by the driver from the factor.

A `Move` while a Tier 2 alignment is running ends the alignment, as hand moves do in the
protocol; the `alignment` block then reads `failed` with reason "interrupted by a move".

### 4.3 Tier 2

| Action | Commands |
|---|---|
| `Begin {tolerance, maxMove}` | `$T=tolerance`, then `$M=maxMove` when given, then `$A=1` |
| `Error {az, alt}` | `$E=az,alt`, then `$L?` to refresh the alignment block |
| `End` | `$A=0` |

The error values are passed to the board unchanged. The board learns how each axis moves
the error from its first probe moves, so the frame of `Error` only has to be consistent
within one session; the host's sign conventions never reach the board.

### 4.4 The alignment block from `$L?`

| `$L?` phase / outcome | `alignment.phase` | `reason` |
|---|---|---|
| `idle` | `idle` | — |
| `waiting` | `waiting` | `$L?` reason |
| `moving_x`, `moving_y` | `moving` | `$L?` reason |
| `ended` / `finished`, `finished_best_effort` | `done` | `$L?` reason |
| `ended` / `halted_*` | `failed` | `$L?` reason |
| `ended` / `stopped_by_user`, `stopped_by_nina`, `stopped_source_ended` | `idle` after `End`; `failed` "stopped" if the host had not sent `End` | |
| `ended` / `stopped_no_source` | `failed`, "no readings for 30 s" | |

`moves` is `$L?` `moves`. PolarAlign's `settling` phase is not used: the board reports
`waiting` once it has settled.

### 4.5 Polling

Clients poll `Status` up to four times a second. Each `Status` costs the driver one `?`
and, during Tier 2, one `$L?`; after a failed `Move`, one `$R?`. The driver may answer
from a cache no older than 250 ms.

## 5. Firmware additions (protocol 1, optional commands)

Found by asking, like the other optional commands: a plain `ok` means not implemented.

| Command | Reply | Meaning |
|---|---|---|
| `$R=<az>,<alt>` | `ok` or `error` | Move both axes by the given arcminutes, with the board's factors and backlash compensation. `error` when not calibrated, when a value is not a number or exceeds the move cap, or when an axis is moving. Ends an alignment the host is feeding. |
| `$R?` | `<R\|state:idle\|moving\|done\|failed\|az:<arcmin>\|alt:<arcmin>\|reason:...\|>` | State of the last `$R=`: the arcminutes actually commanded per axis, and why it failed (`stall`, `travel`, `timeout`, `stopped`). `idle` when no `$R=` was ever sent. |

Sign: positive azimuth turns the polar axis clockwise seen from above, positive altitude
raises it. The board makes this true through the existing **motor direction** setting on
its page: the user sets each axis once so that the page's arrows move the base the way
the arrows point, and `$R=` uses the same sense. The firmware README documents the two
sentences a user needs to check it.

A later `$I?` (board identity: MAC, board type) is useful for `UniqueID` but not required;
without it the C# and C++ drivers generate and store one per configured device.

## 6. What differs between the three implementations

| | C# server (PC) | C++ in AlpacaBridge | ESP32 firmware |
|---|---|---|---|
| Link to the board | serial port or TCP 2323, chosen in a setup page | serial port on the Pi, configured in AlpacaBridge's web UI | in process: no link |
| Discovery | Alpaca UDP discovery on port 32227 | AlpacaBridge's | UDP discovery added to the firmware |
| `UniqueID` | generated once, stored with the device config | AlpacaBridge's persisted config | from the MAC |
| Setup UI | small web page served by the server | AlpacaBridge's device page | the board's existing page |
| Licence | MPL-2.0 | AGPL-3.0 | MPL-2.0 |
| Where it runs | Windows, Linux, macOS (.NET 8) | Debian 13 arm64 | the board |

Everything in sections 3 to 5 is identical.

## 7. Tests shared by the three

1. **ConformU** on the Switch device, no errors, no issues. Actions are not covered by
   ConformU; the rehearsal covers them.
2. **Bench rehearsal**: a script that connects to the device over Alpaca and plays a Tier 1
   probe plus correction and a Tier 2 session, with a synthetic sky: the "measured" error is
   the initial error minus the motion the board reports in `position`, plus noise. It
   passes when both sessions end within tolerance, a refused move comes back as the right
   Alpaca error, `Stop` ends a move within one poll, a disconnected link makes the request
   fail, and `Info` is identical before and after a session. The script is written once
   (Python, next to the firmware's bench tools) and run against all three.
3. **Field**: a TPPA session through the Correctors plugin, both tiers, on a real mount.
   One tester has been asked.

## 8. Out of scope

- The IAxisAligner ASCOM type: added later behind the same actions, once the spec exists.
- Calibration started from Alpaca: a Tier 2 session calibrates when needed.
- Absolute moves, sync, travel range: the board does not know its travel.
- The `:AX…#` serial protocol: a separate proposal; this contract uses the OAPA protocol.

## 9. Open points

1. `settleSeconds`: 0 (the board reports `waiting` only when settled), or a small value
   for safety? Default 0.
2. The `fault` field: propose it to Lutz as a v0.2 addition to the draft, or keep it as an
   OAPA extension? Default: propose it.
3. `$I?` for identity: in the same firmware release as `$R=`, or later? Default: same
   release, it is small.
