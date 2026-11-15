# OAPA Alpaca server

Presents an OAPA polar alignment board, on USB or on the network, as an ASCOM Alpaca
device: a Switch with the PolarAlign actions, so that any program that drives polar
alignment correctors through ASCOM or Alpaca can drive OAPA. N.I.N.A. reaches it through
the Polar Alignment Correctors plugin; the ASCOM Platform's dynamic clients make it a
normal ASCOM Switch for any Windows program.

The contract it implements, shared with the AlpacaBridge driver and the firmware's own
Alpaca server, is in
[docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md](docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md).

This version drives the board through **Tier 2** only: the host sends the polar error
readings and the board runs the alignment itself (firmware 1.3.3 or later, protocol 1).
Tier 1 relative moves arrive with the firmware's `$R=` command.

## Run

Requires the .NET 8 runtime (Windows, Linux, macOS).

```
dotnet run --project src/OapaAlpaca
```

Then open <http://localhost:11113/>, choose how the board is reached (a serial port, or
the board's WiFi address on TCP 2323), and save. The setting lives in `config.json` next
to the executable, together with the Alpaca `UniqueID` generated on the first start.

The server answers Alpaca discovery, so the ASCOM chooser and N.I.N.A. list it as
"OAPA" without typing an address. Device: Switch 0, `http://<host>:11113/api/v1/switch/0`.

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
  (OpenAstroTech draft v0.1). `Info` reports `tiers: [2]`.
- Alpaca error numbers as in the contract; a lost link fails the request with HTTP 503.

## Tests

```
dotnet test OapaAlpaca.slnx
```

117 tests: the protocol frames, the links, the device engine against a scripted board, and
the HTTP surface through an in-memory host. No hardware needed.

## Bench

- `tools/bench_calibrate.py <host:port>`: calibrates a bench board with a synthetic sky
  over the OAPA protocol (`$C=2`), so an uncalibrated board gets factors. The motors move.
- `tools/rehearsal.py <server url>`: a Tier 2 session through Alpaca with a synthetic
  sky; passes when the board reports done within tolerance. `--stop-after N` checks Stop.
- ConformU on the Switch: [docs/conformu](docs/conformu), 0 errors, 0 issues (4.5.3,
  server on a Raspberry Pi 5, board on USB).

## Licence

[MPL-2.0](LICENSE). See [NOTICE](NOTICE) for the name and attribution terms.
