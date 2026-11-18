# OAPA Alpaca server

Presents an OAPA polar alignment board, on USB or on the network, as two ASCOM Alpaca
devices on one board: a Switch with the PolarAlign actions, so that any program that
drives polar alignment correctors through ASCOM or Alpaca can drive OAPA today, and an
Aligner after the ASCOM IAlignerV1 draft, so that the interface the ASCOM Initiative is
writing up has a real device to test against. N.I.N.A. reaches the Switch through the
Polar Alignment Correctors plugin; the ASCOM Platform's dynamic clients make it a normal
ASCOM Switch for any Windows program.

The contract it implements, shared with the AlpacaBridge driver and the firmware's own
Alpaca server, is in
[docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md](docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md).

Both tiers of the PolarAlign draft: **Tier 2**, where the host sends the polar error
readings and the board runs the alignment itself (firmware 1.3.3 or later, protocol 1),
and **Tier 1**, where the host asks for one relative move at a time in arcminutes and the
board converts with its own factors and compensates backlash (firmware 1.3.7 or later,
command `$R=`). A board without `$R=` is served with Tier 2 only.

## Run

Requires the .NET 8 runtime (Windows, Linux, macOS).

```
dotnet run --project src/OapaAlpaca
```

Then open <http://localhost:11113/>, choose how the board is reached (a serial port, or
the board's WiFi address on TCP 2323), and save. The setting lives in `config.json` next
to the executable, together with the Alpaca `UniqueID` generated on the first start.

The server answers Alpaca discovery, so the ASCOM chooser and N.I.N.A. list it as
"OAPA" without typing an address. Devices: Switch 0, `http://<host>:11113/api/v1/switch/0`,
and Aligner 0, `http://<host>:11113/api/v1/aligner/0`.

A self-contained build for a Raspberry Pi (Debian/Raspberry Pi OS 64-bit):

```
dotnet publish src/OapaAlpaca -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o out
```

Copy the `out` folder to the Pi (the `.so` next to the executable is the serial port
driver), `chmod +x OapaAlpaca`, run `./OapaAlpaca --urls http://0.0.0.0:11113`, and
point the page at `/dev/serial/by-id/...` so a re-enumerated USB converter keeps working.

## What a client sees

- Switch, interface version 3, two read-only switches with the azimuth and altitude
  position in arcminutes (unavailable until the board is calibrated).
- Actions `PolarAlign:Info`, `Status`, `Move`, `Stop`, `Home`, `Begin`, `Error`, `End`
  (OpenAstroTech draft v0.1). `Info` reports `tiers: [1, 2]`, or `[2]` on a board without
  `$R=`. `Move` answers `{"arcmin": n}`, the request rounded to whole steps; a move that
  the board gives up on (stall, timeout, a stop from the board's page) shows in `Status`
  as a `fault` string on that axis until the next `Move` of the axis, `Stop` or `Home`.
- Factors entered on the board's page or pushed by the controller count as a calibration:
  positions, resolution and `Move` are available with them.
- `maxMove` is the move cap set on the board's page (firmware 1.3.7 reports it in `$M?`):
  it bounds a `Move` and the `maxMove` of `Begin`, so a host can ask for smaller
  corrections than the page allows, never larger.
- Alpaca error numbers as in the contract; a lost link fails the request with HTTP 503.

The Aligner (ASCOM IAlignerV1 draft 1, <https://ascom-standards.org/ialigner>, Alpaca
definition at <https://ascom-standards.org/apialigner>), interface version 1:

- `configuration` answers one object: `AlignerPurpose` 1 (polar aligner), `Units` 1
  (degrees), `CanMoveX` and `CanMoveY` true, `CanMoveZ` false, `CanFindHome` and `CanHalt`
  true, `CanMoveAbsolute` and `CanSync` false, `XMaxMove` and `YMaxMove` the board's move
  cap in degrees; the travel limits are left out because the board does not know them.
- `move` takes `X`, `Y`, `Z` in degrees (`Z` must be 0) and sends one `$R=` with both
  axes; positive X increases the azimuth of the polar axis (clockwise seen from above),
  positive Y raises it. A zero move completes at once without reaching the board.
  `halt` stops both axes, `findhome` homes them; `xposition` and `yposition` are degrees
  from the board's zero, `zposition`, `moveabsolute` and `sync` are not implemented.
  The positions are motor positions, so across a reversal they include the board's
  backlash take-up: on the bench a 0.01° move read 0.01° in the same direction as the
  previous one and up to 0.06° at a reversal. A client re-measures the sky, as the draft
  expects, rather than trusting the position change.
- A move that fails is reported by `ismoving` as a driver error (0x500) naming the axis
  and the board's reason, until the next `move` (a zero move will do), `halt`, `findhome`
  or reconnect; `devicestate` leaves `IsMoving` out meanwhile. After `halt`, `ismoving`
  is false: the move was cancelled, not failed.
- The Switch and the Aligner are the same board: connecting one connects the other, and
  the PolarAlign actions stay on the Switch.

## Tests

```
dotnet test OapaAlpaca.slnx
```

169 tests: the protocol frames, the links, the device engine against a scripted board, and
the HTTP surface of both devices through an in-memory host. No hardware needed.

## Bench

- `tools/bench_calibrate.py <host:port>`: calibrates a bench board with a synthetic sky
  over the OAPA protocol (`$C=2`), so an uncalibrated board gets factors. The motors move.
- `tools/rehearsal.py <server url>`: a Tier 2 session through Alpaca with a synthetic
  sky; passes when the board reports done within tolerance. `--play <arcmin>` gives the
  synthetic mount a mechanical play, felt across every reversal of the motors as on a real
  mount, so the board's backlash compensation (the `$B=` values a host pushed, kept in its
  RAM until a reboot) has to match it. `--stop-after N` checks Stop. `--tier 1` instead
  sends relative moves and checks that a move in the direction of the previous one moves the
  position by exactly the reply, reports the compensation the board added on a reversal, and
  checks the refusals and that Stop ends a move within two seconds (the motors decelerate).
- ConformU on the Switch: [docs/conformu](docs/conformu), 0 errors, 0 issues (4.5.3,
  server on a Raspberry Pi 5, board on USB). ConformU does not know the Aligner yet; the
  draft is a proposal.
- Over WiFi the link waits up to 3 s for a reply: at a weak signal (-87 dBm on the bench)
  single replies took up to 1 s. A lost link closes the TCP connection and the request
  fails with 503; the client reconnects with Connected false then true. The board accepts
  one TCP client at a time, so a second client (a terminal, N.I.N.A. on the same board)
  takes the link away from the server.

## Licence

[MPL-2.0](LICENSE). See [NOTICE](NOTICE) for the name and attribution terms.
