#!/usr/bin/env python3
"""Bench rehearsal of an OAPA Alpaca device: a Tier 2 session with a synthetic sky.

The script plays the host. It connects over Alpaca, reads Info, sends Begin, and then feeds the
device error readings computed from the motion the device reports: the "measured" error is the
initial error minus the change of each axis position since the start (times a scale), plus noise.
It passes when the device reports the alignment done and the synthetic error is within tolerance.

    python tools/rehearsal.py http://localhost:11113 --error 30,-20 --tolerance 1.5

A second run with --stop-after N sends Stop after the N-th reading and checks the axes stop within
one poll. Exit code 0 means pass; 1 prints the reason. Standard library only.
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


def main():
    p = argparse.ArgumentParser()
    p.add_argument("url")
    p.add_argument("--error", default="30,-20", help="initial az,alt error in arcminutes")
    p.add_argument("--tolerance", type=float, default=1.5)
    p.add_argument("--max-move", type=float, default=None)
    p.add_argument("--scale-az", type=float, default=1.0, help="arcminutes of error per arcminute of reported azimuth motion")
    p.add_argument("--scale-alt", type=float, default=1.0)
    p.add_argument("--noise", type=float, default=0.1)
    p.add_argument("--stop-after", type=int, default=None, help="send Stop after this many readings and check the axes stop")
    p.add_argument("--timeout-s", type=float, default=600)
    p.add_argument("--seed", type=int, default=1)
    args = p.parse_args()
    random.seed(args.seed)
    az0, alt0 = (float(v) for v in args.error.split(","))
    dev = Alpaca(args.url)

    dev.put("connected", Connected="true")
    info = dev.action("PolarAlign:Info")
    print("Info:", json.dumps(info))
    if 2 not in info.get("tiers", []):
        return fail("the device does not offer Tier 2")
    status = dev.action("PolarAlign:Status")
    start = {axis: status.get(axis, {}).get("position") for axis in ("az", "alt")}
    if start["az"] is None or start["alt"] is None:
        return fail("the device reports no positions (not calibrated?): the synthetic sky needs them")

    def measure():
        now = dev.action("PolarAlign:Status")
        az = az0 - (now["az"]["position"] - start["az"]) * args.scale_az + random.gauss(0, args.noise)
        alt = alt0 - (now["alt"]["position"] - start["alt"]) * args.scale_alt + random.gauss(0, args.noise)
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
                time.sleep(POLL_S)
                after = dev.action("PolarAlign:Status")
                if after["az"]["moving"] or after["alt"]["moving"]:
                    return fail("an axis still moves one poll after Stop")
                print("PASS: Stop ended the motion within one poll")
                return 0
            # Wait for the device to act on the reading and settle again.
            while time.time() - started < args.timeout_s:
                time.sleep(POLL_S)
                status = dev.action("PolarAlign:Status")
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
