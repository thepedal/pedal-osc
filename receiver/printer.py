#!/usr/bin/env python3
"""Diagnostic printer for the ReBuzz -> video bridge.

Listens for Pedal OSC's feature frame on UDP 127.0.0.1:9000 and shows a live
readout: level meter, beat phase, BPM, transport state, message rate, and the
running min/max of the level (which sanity-checks the machine's SampleScale).

Run:   python printer.py
Deps:  pip install -r requirements.txt

If nothing moves: check Pedal OSC is inserted (conventionally before Master),
a song is playing, and host/port match the machine (127.0.0.1:9000).
"""

import argparse
import threading
import time

from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

_v = {}                 # newest value per OSC address
_meta = {"count": 0, "last_rx": 0.0, "min": None, "max": None}
_lock = threading.Lock()


def on_msg(address, *args):
    if not args:
        return
    try:
        val = float(args[0])
    except (TypeError, ValueError):
        return
    with _lock:
        _v[address] = val
        _meta["last_rx"] = time.time()
        if address.endswith("/rms"):
            _meta["count"] += 1
            mn, mx = _meta["min"], _meta["max"]
            _meta["min"] = val if mn is None else min(mn, val)
            _meta["max"] = val if mx is None else max(mx, val)


def display_loop(prefix):
    last_count, last_t = 0, time.time()
    while True:
        time.sleep(1 / 15)
        now = time.time()
        with _lock:
            v = dict(_v)
            count = _meta["count"]
            last_rx = _meta["last_rx"]
            mn, mx = _meta["min"], _meta["max"]

        dt = now - last_t
        rate = (count - last_count) / dt if dt > 0 else 0.0
        last_count, last_t = count, now

        if last_rx == 0.0:
            _line("waiting for OSC ...")
            continue
        if now - last_rx > 1.0:
            _line(f"(no packets for {now - last_rx:4.1f}s - machine inserted? song playing?)")
            continue
        if not v:
            _line("receiving OSC, but no recognised addresses")
            continue

        rms   = v.get(prefix + "/rms", 0.0)
        beat  = v.get(prefix + "/beat", 0.0)
        bpm   = v.get(prefix + "/bpm", 0.0)
        play  = v.get(prefix + "/playing", 0.0)

        lvl = _bar(rms, 22)
        # Beat phase as a moving marker - it should sweep left-to-right once per beat.
        bt = _marker(beat, 8)
        rng = f"min {mn:.3f} max {mx:.3f}" if mn is not None else ""
        state = "PLAY" if play >= 0.5 else "stop"

        _line(f"lvl [{lvl}] {rms:5.3f}  beat [{bt}]  {bpm:5.1f}bpm  {state}  "
              f"{rate:5.1f}/s  {rng}")


def _bar(value, width):
    filled = max(0, min(width, int(round(value * width))))
    return "#" * filled + "-" * (width - filled)


def _marker(phase, width):
    pos = max(0, min(width - 1, int(phase * width)))
    return "".join("O" if i == pos else "." for i in range(width))


def _line(s):
    print("\r" + s.ljust(100), end="", flush=True)


def main():
    ap = argparse.ArgumentParser(description="Diagnostic printer for the ReBuzz video bridge.")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9000)
    ap.add_argument("--prefix", default="/rebuzz", help="OSC address prefix (default /rebuzz)")
    args = ap.parse_args()

    disp = Dispatcher()
    disp.set_default_handler(on_msg)
    server = ThreadingOSCUDPServer((args.host, args.port), disp)
    threading.Thread(target=display_loop, args=(args.prefix,), daemon=True).start()

    print(f"Listening on {args.host}:{args.port}   (Ctrl+C to stop)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nbye")


if __name__ == "__main__":
    main()
