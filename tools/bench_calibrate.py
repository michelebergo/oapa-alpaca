#!/usr/bin/env python3
"""Calibrates a bench board with a synthetic sky, over the OAPA protocol (serial via socat, or the
board's TCP port), so that an uncalibrated board gets factors and the Alpaca rehearsal can run.

Plays the host of a `$C=2` calibration: every few seconds it reads the motor positions with `?`,
turns them into a "measured" error with a known scale (default 60 steps per arcminute, like the
firmware's own simulator), sends it as `$E=`, and watches `$K?` until the board says done or failed.

    python tools/bench_calibrate.py 192.168.1.64:2323 --factor 60 --error 30,-20

The motors move. Exit code 0 when the calibration is done. Standard library only.
"""
import argparse
import random
import socket
import sys
import time


class Board:
    """The board over TCP. A dropped link (a USB converter re-enumerating behind socat) is reopened once per command."""

    def __init__(self, address):
        host, _, port = address.partition(":")
        self.endpoint = (host, int(port or 2323))
        self.sock = None
        self.buffer = b""
        self.connect()

    def connect(self):
        if self.sock is not None:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = socket.create_connection(self.endpoint, timeout=5)
        self.buffer = b""
        time.sleep(0.5)
        self.drain()

    def drain(self):
        self.sock.settimeout(0.3)
        try:
            while self.sock.recv(4096):
                pass
        except (socket.timeout, OSError):
            pass
        self.buffer = b""

    def line(self, timeout=3.0):
        self.sock.settimeout(timeout)
        while b"\n" not in self.buffer:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise RuntimeError("the link closed")
            self.buffer += chunk
        text, _, self.buffer = self.buffer.partition(b"\n")
        return text.decode(errors="replace").rstrip("\r")

    def command(self, text, lines=1):
        for attempt in (1, 2):
            try:
                self.sock.sendall((text + "\n").encode())
                return [self.line() for _ in range(lines)]
            except (OSError, RuntimeError) as error:
                if attempt == 2:
                    raise
                print(f"link dropped ({error}); reconnecting")
                time.sleep(2.0)
                self.connect()

    def status(self):
        frame, _ok = self.command("?", lines=2)
        fields = dict(f.split(":", 1) for f in frame.strip("<>|").split("|")[1:] if ":" in f)
        x, y, _z = (float(v) for v in fields["MPos"].split(","))
        return frame.strip("<").split("|")[0], x, y

    def frame(self, text):
        reply = self.command(text)[0]
        if not reply.startswith("<"):
            return {}
        parts = reply.strip("<>|").split("|")
        return dict(f.split(":", 1) for f in parts[1:] if ":" in f)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("address", help="host or host:port of the board (socat on the Pi, or the board's WiFi)")
    p.add_argument("--factor", type=float, default=60, help="synthetic steps per arcminute, both axes")
    p.add_argument("--error", default="30,-20", help="synthetic initial error az,alt in arcminutes")
    p.add_argument("--noise", type=float, default=0.05)
    p.add_argument("--refresh-s", type=float, default=3.0)
    p.add_argument("--timeout-s", type=float, default=900)
    args = p.parse_args()
    az0, alt0 = (float(v) for v in args.error.split(","))
    random.seed(1)

    board = Board(args.address)
    print("protocol:", board.command("$P?")[0])
    state, x0, y0 = board.status()
    print(f"start: {state} x={x0:.0f} y={y0:.0f}")
    # The board keeps the previous result in $K? until the new calibration takes its first reading.
    stale = board.frame("$K?")
    print("calibrate only:", board.command("$C=2")[0])
    started = time.time()
    last = {}
    while time.time() - started < args.timeout_s:
        state, x, y = board.status()
        az = az0 - (x - x0) / args.factor + random.gauss(0, args.noise)
        alt = alt0 - (y - y0) / args.factor + random.gauss(0, args.noise)
        board.command(f"$E={az:.2f},{alt:.2f}")
        k = board.frame("$K?")
        if k != last:
            print(f"{time.time() - started:6.1f}s {state:5} x={x:7.0f} y={y:7.0f}  reading {az:+.2f},{alt:+.2f}  K: {k}")
            last = k
        if k == stale:
            time.sleep(args.refresh_s)
            continue
        if k.get("state") in ("done", "failed", "stopped"):
            print("result:", k)
            return 0 if k.get("state") == "done" else 1
        time.sleep(args.refresh_s)
    print("FAIL: timeout")
    return 1


if __name__ == "__main__":
    sys.exit(main())
