# OAPA Alpaca server

Presents an OAPA polar alignment board, on USB or on the network, as an ASCOM Alpaca
device: a Switch with the PolarAlign actions, so that any program that drives polar
alignment correctors through ASCOM or Alpaca can drive OAPA.

The contract it implements, shared with the AlpacaBridge driver and the firmware's own
Alpaca server, is in
[docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md](docs/superpowers/specs/2026-11-14-oapa-alpaca-contract-design.md).

## Licence

[MPL-2.0](LICENSE). See [NOTICE](NOTICE) for the name and attribution terms.
