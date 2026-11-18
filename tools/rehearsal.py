#!/usr/bin/env python3
"""Bench rehearsal of an OAPA Alpaca device: a Tier 2 session with a synthetic sky, or Tier 1 moves.

The script plays the host. It connects over Alpaca, reads Info, sends Begin, and then feeds the
device error readings computed from the motion the device reports: the "measured" error is the
initial error minus the motion of each axis since the start (times a scale), plus noise. The
positions a board reports are motor positions, so the synthetic mount has a mechanical play
(--play, arcminutes, 0 by default): across every reversal of a motor that much travel moves
nothing, as on a real mount, and the board's own backlash compensation (the $B= values a host
pushed, kept in the board's RAM until a reboot) has to match it for the session to converge as it
does without play. It passes when the device reports the alignment done and the synthetic error is
within tolerance.

    python tools/rehearsal.py http://localhost:11113 --error 30,-20 --tolerance 1.5 --play 3

A second run with --stop-after N sends Stop after the N-th reading and checks the axes stop within
one poll. With --tier 1 the script sends relative moves instead (+1' az, -2' alt, +1.5' az, -1'
alt) and checks the reported positions: a move in the direction of the axis's previous move has to
follow within one step; the first move of an axis and a reversal are reported with the backlash
compensation the board added, which a client cannot know (nothing when the board's backlash is
Off). It then checks that a move beyond maxMove and a bad axis are refused before anything moves,
and that Stop ends a move within two seconds (the motors decelerate) and nothing moves afterwards.
Exit code 0 means pass; 1 prints the reason. Standard library only.
"""
import argparse
import json
import random
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

DEVICE = "/api/v1/switch/0/"
POLL_S = 0.25


class Alpaca:
    def __init__(self, base):
        self.base = base.rstrip("/")
        self.transaction = 0

    def _request(self, method, member, fields=None):
        self.transaction += 1
        fields = dict(fields or {})
        fields["ClientID"] = 1
        fields["ClientTransactionID"] = self.transaction
        data = urllib.parse.urlencode(fields).encode()
        if method == "GET":
            request = urllib.request.Request(f"{self.base}{DEVICE}{member}?{data.decode()}")
        else:
            request = urllib.request.Request(f"{self.base}{DEVICE}{member}", data=data, method="PUT")
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                body = json.loads(response.read())
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"{member}: HTTP {error.code} (a lost link?)") from None
        if body.get("ErrorNumber", 0):
            raise AlpacaError(body["ErrorNumber"], body.get("ErrorMessage", ""))
        return body.get("Value")

    def get(self, member, **fields):
        return self._request("GET", member, fields)

    def put(self, member, **fields):
        return self._request("PUT", member, fields)

    def action(self, name, parameters=None):
        reply = self.put("action", Action=name, Parameters=json.dumps(parameters) if parameters is not None else "")
        return json.loads(reply) if reply else {}


class AlpacaError(Exception):
    def __init__(self, number, message):
        super().__init__(f"Alpaca error 0x{number:X}: {message}")
        self.number = number


def fail(reason):
    print("FAIL:", reason)
    return 1


class Play:
    """The mechanical play of one axis of the synthetic mount, fed with the motor positions the
    device reports: the motor sits inside a gap of the given width and the axis moves only once the
    motor reaches the gap's edge in its direction of travel. Rate independent: coarse samples of a
    motion in one direction give the same result as fine ones. Engaged on the positive flank at
    the start, the state the compensators (TPPA, the OAPA firmware) assume at a connection."""

    def __init__(self, width):
        self.half = max(0.0, width) / 2
        self.gap = self.half  # motor minus axis, kept within +/- half the width
        self.motor = None  # the last motor position fed; the first one is the baseline
        self.axis = 0.0  # motion of the axis since the baseline

    def feed(self, motor):
        if self.motor is not None:
            self.gap += motor - self.motor
            if self.gap > self.half:
                self.axis += self.gap - self.half
                self.gap = self.half
            elif self.gap < -self.half:
                self.axis += self.gap + self.half
                self.gap = -self.half
        self.motor = motor
        return self.axis


# "!" decelerates the motors: a board at full feed reports Run for about half a second after
# Stop (0.8 s measured over WiFi on a FYSETC E4, 15/11/2026). Twice that is the allowance.
STOP_S = 2.0


def wait_still(dev, timeout_s):
    """Polls Status until no axis moves; returns the last status, or None on timeout."""
    end = time.time() + timeout_s
    while time.time() < end:
        time.sleep(POLL_S)
        status = dev.action("PolarAlign:Status")
        if not status["az"]["moving"] and not status["alt"]["moving"]:
            return status
    return None


def tier1(dev, info, args):
    if 1 not in info.get("tiers", []):
        return fail("the device does not offer Tier 1 (firmware without $R=?)")
    axes = info["axes"]
    before = dev.action("PolarAlign:Status")
    if before["az"].get("position") is None:
        return fail("the device reports no positions (not calibrated?)")
    # The positions are motor positions. In the direction of the previous move the motor travels
    # exactly the reply; across a reversal the board adds its backlash compensation, the $B= values
    # a host pushed (kept in its RAM until a reboot; Off until then), which this host cannot know
    # and so reports rather than judges. The direction before the connection is unknown too, so the
    # first move of each axis is reported the same way.
    last_direction = {}
    for axis, amount in (("az", 1.0), ("alt", -2.0), ("az", 1.5), ("alt", -1.0)):
        start = before[axis]["position"]
        reply = dev.action("PolarAlign:Move", {"axis": axis, "arcmin": amount})
        print(f"Move {axis} {amount:+.2f}' -> arcmin {reply.get('arcmin')}")
        status = dev.action("PolarAlign:Status")
        if not status[axis]["moving"]:
            print(f"  note: {axis} not reported moving on the first poll (a short move may already be over)")
        after = wait_still(dev, args.timeout_s)
        if after is None:
            return fail(f"{axis} still moves after {args.timeout_s} s")
        if "fault" in after[axis]:
            return fail(f"{axis} reports a fault: {after[axis]['fault']}")
        moved = after[axis]["position"] - start
        extra = moved - reply["arcmin"]
        exact = abs(extra) <= axes[axis]["resolution"] * 1.01
        direction = 1 if amount > 0 else -1
        if last_direction.get(axis) == direction:
            if not exact:
                return fail(f"{axis} moved {moved:+.3f}' for a reply of {reply['arcmin']:+.3f}' in the direction of its previous move")
            print(f"  {axis} moved {moved:+.3f}'  OK")
        elif exact:
            print(f"  {axis} moved {moved:+.3f}'  OK (first move of the axis or a reversal: the board added no compensation)")
        else:
            print(f"  {axis} moved {moved:+.3f}': {extra:+.3f}' of backlash compensation added by the board (first move of the axis or a reversal, its $B= values)")
        last_direction[axis] = direction
        before = after
    for parameters, expected in (({"axis": "az", "arcmin": axes["az"]["maxMove"] + 1}, 0x401), ({"axis": "ra", "arcmin": 1}, 0x401)):
        try:
            dev.action("PolarAlign:Move", parameters)
            return fail(f"Move {parameters} was accepted")
        except AlpacaError as error:
            if error.number != expected:
                return fail(f"Move {parameters}: {error}")
            print(f"Move {parameters} refused with 0x{error.number:X}  OK")
    # Long enough to still be in flight when Stop arrives (a 5' move is over in half a second), and
    # within the board's own move cap (30' by default on its page).
    start = before["az"]["position"]
    dev.action("PolarAlign:Move", {"axis": "az", "arcmin": -25})
    time.sleep(0.5)
    dev.action("PolarAlign:Stop")
    stopped = time.time()
    after = wait_still(dev, STOP_S)
    if after is None:
        return fail(f"az still moves {STOP_S} s after Stop")
    if "fault" in after["az"]:
        return fail(f"Stop left a fault: {after['az']['fault']}")
    time.sleep(POLL_S)
    later = dev.action("PolarAlign:Status")
    if later["az"]["moving"] or later["az"]["position"] != after["az"]["position"]:
        return fail("az moved again after it had stopped")
    moved = after["az"]["position"] - start
    if abs(moved) > 22:
        return fail(f"az moved {moved:+.1f}' of -25': the Stop did not interrupt the move")
    print(f"Stop ended the move in {time.time() - stopped:.2f} s (az moved {moved:+.3f}' of -25')  OK")
    print("PASS")
    return 0


def main():
    p = argparse.ArgumentParser()
    p.add_argument("url")
    p.add_argument("--error", default="30,-20", help="initial az,alt error in arcminutes")
    p.add_argument("--tolerance", type=float, default=1.5)
    p.add_argument("--max-move", type=float, default=None)
    p.add_argument("--scale-az", type=float, default=1.0, help="arcminutes of error per arcminute of reported azimuth motion")
    p.add_argument("--scale-alt", type=float, default=1.0)
    p.add_argument("--noise", type=float, default=0.1)
    p.add_argument("--play", type=float, default=0.0, help="mechanical play of the synthetic mount in arcminutes, felt across every reversal of the motors")
    p.add_argument("--stop-after", type=int, default=None, help="send Stop after this many readings and check the axes stop")
    p.add_argument("--timeout-s", type=float, default=600)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--tier", type=int, choices=(1, 2), default=2, help="1: relative moves instead of a session")
    args = p.parse_args()
    random.seed(args.seed)
    az0, alt0 = (float(v) for v in args.error.split(","))
    dev = Alpaca(args.url)

    # A fresh link: after a link fault the device stays Connected until the client reconnects.
    dev.put("connected", Connected="false")
    dev.put("connected", Connected="true")
    info = dev.action("PolarAlign:Info")
    print("Info:", json.dumps(info))
    if args.tier == 1:
        return tier1(dev, info, args)
    if 2 not in info.get("tiers", []):
        return fail("the device does not offer Tier 2")
    status = dev.action("PolarAlign:Status")
    if status.get("az", {}).get("position") is None or status.get("alt", {}).get("position") is None:
        return fail("the device reports no positions (not calibrated?): the synthetic sky needs them")
    # Every poll feeds the play, so a reversal inside a correction (the return leg of the board's
    # Unidirectional mode) counts when it falls on a sample; the device is polled four times a second.
    play = {"az": Play(args.play), "alt": Play(args.play)}

    def sample():
        now = dev.action("PolarAlign:Status")
        for axis in ("az", "alt"):
            play[axis].feed(now[axis]["position"])
        return now

    sample()

    def measure():
        now = sample()
        az = az0 - play["az"].axis * args.scale_az + random.gauss(0, args.noise)
        alt = alt0 - play["alt"].axis * args.scale_alt + random.gauss(0, args.noise)
        return now, az, alt

    begin = {"tolerance": args.tolerance}
    if args.max_move is not None:
        begin["maxMove"] = args.max_move
    dev.action("PolarAlign:Begin", begin)
    started = time.time()
    readings = 0
    try:
        while time.time() - started < args.timeout_s:
            status, az, alt = measure()
            readings += 1
            print(f"reading {readings}: az {az:+.2f}' alt {alt:+.2f}'  (az pos {status['az']['position']:.2f}, alt pos {status['alt']['position']:.2f})")
            dev.action("PolarAlign:Error", {"az": az, "alt": alt})
            if args.stop_after is not None and readings >= args.stop_after:
                time.sleep(1.0)
                dev.action("PolarAlign:Stop")
                stopped = time.time()
                if wait_still(dev, STOP_S) is None:
                    return fail(f"an axis still moves {STOP_S} s after Stop")
                print(f"PASS: Stop ended the motion in {time.time() - stopped:.2f} s")
                return 0
            # Wait for the device to act on the reading and settle again.
            while time.time() - started < args.timeout_s:
                time.sleep(POLL_S)
                status = sample()
                phase = status.get("alignment", {}).get("phase", "idle")
                moving = status["az"]["moving"] or status["alt"]["moving"]
                if phase == "failed":
                    return fail(f"the device gave up: {status['alignment'].get('reason')}")
                if phase == "done":
                    _, az, alt = measure()
                    total = (az ** 2 + alt ** 2) ** 0.5
                    print(f"done after {readings} readings, {status['alignment'].get('moves')} moves; synthetic error {total:.2f}' ({status['alignment'].get('reason')})")
                    if total > args.tolerance + 3 * args.noise:
                        return fail(f"the device reports done but the synthetic error is {total:.2f}'")
                    info_after = dev.action("PolarAlign:Info")
                    if info_after != info:
                        return fail("Info changed during the session")
                    print("PASS")
                    return 0
                if phase == "waiting" and not moving:
                    break
        return fail("timeout")
    finally:
        try:
            dev.action("PolarAlign:End")
        except (AlpacaError, RuntimeError):
            pass


if __name__ == "__main__":
    sys.exit(main())
