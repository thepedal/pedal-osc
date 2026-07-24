#!/usr/bin/env python3
"""Generic OSC dump for the ReBuzz -> video bridge.

Shows every address arriving on UDP 127.0.0.1:9000 with its current value, sorted
by address. Unlike a fixed-layout meter this discovers addresses as they appear,
which is what Pedal OSC Data needs: its /rebuzz/param/... addresses depend on
which machine and parameters you have selected.

Run:   python oscdump.py
Deps:  pip install -r requirements.txt

Both Pedal OSC (the audio tap) and Pedal OSC Data (the control machine) send to
the same port; this shows the merged picture.
"""

import argparse
import os
import threading
import time

from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

STALE_EXPIRY_S = 10.0   # drop an address this long after its last packet

_vals = {}          # address -> (value, last_seen)
_count = 0
_last_rx = 0.0
_lock = threading.Lock()


def on_msg(address, *args):
    global _count, _last_rx
    if not args:
        return
    try:
        v = float(args[0])
    except (TypeError, ValueError):
        v = None
    now = time.time()
    with _lock:
        _vals[address] = (v, now)
        _count += 1
        _last_rx = now


def bar(v, width=18):
    if v is None:
        return " " * width
    filled = max(0, min(width, int(round(max(0.0, min(1.0, v)) * width))))
    return "#" * filled + "-" * (width - filled)


def display_loop():
    last_count, last_t = 0, time.time()
    prev_lines = 0
    while True:
        time.sleep(1 / 10)
        now = time.time()
        with _lock:
            # Expire addresses unseen for a while, so ghost entries from a previous
            # export selection do not linger forever after you re-point the machine.
            cutoff = now - STALE_EXPIRY_S
            for addr in [a for a, (_, seen) in _vals.items() if seen < cutoff]:
                del _vals[addr]
            snapshot = dict(_vals)
            count = _count
            last_rx = _last_rx

        dt = now - last_t
        rate = (count - last_count) / dt if dt > 0 else 0.0
        last_count, last_t = count, now

        out = []
        if last_rx == 0.0:
            out.append("waiting for OSC ...")
        else:
            stale = now - last_rx > 1.0
            hdr = f"{len(snapshot)} addresses   {rate:6.1f} msg/s"
            if stale:
                hdr += f"   (no packets for {now - last_rx:.1f}s)"
            out.append(hdr)
            out.append("")
            for addr in sorted(snapshot):
                v, seen = snapshot[addr]
                dim = "  " if (now - seen) < 1.0 else " ."
                if v is None:
                    out.append(f"{dim}{addr:44s}        (non-numeric)")
                else:
                    # Values outside 0..1 (bpm, version) print without a bar.
                    if 0.0 <= v <= 1.0:
                        out.append(f"{dim}{addr:44s} {v:8.3f}  [{bar(v)}]")
                    else:
                        out.append(f"{dim}{addr:44s} {v:8.2f}")

        # Redraw in place: move cursor up over the previous block.
        if prev_lines:
            print(f"\033[{prev_lines}A", end="")
        for line in out:
            print(line.ljust(90))
        # Clear any leftover rows from a previously longer block.
        for _ in range(max(0, prev_lines - len(out))):
            print(" " * 90)
        prev_lines = max(len(out), prev_lines)


def main():
    ap = argparse.ArgumentParser(description="Generic OSC dump for the ReBuzz video bridge.")
    ap.add_argument("--host", default="0.0.0.0",
                    help="bind address; 0.0.0.0 receives on all interfaces (needed for LAN)")
    ap.add_argument("--port", type=int, default=9000)
    args = ap.parse_args()

    # Windows consoles do not process ANSI escapes by default, so the in-place
    # redraw prints literal "<-[15A" instead of moving the cursor. An empty
    # os.system call flips on virtual-terminal processing for the session.
    if os.name == "nt":
        os.system("")

    disp = Dispatcher()
    disp.set_default_handler(on_msg)
    server = ThreadingOSCUDPServer((args.host, args.port), disp)
    threading.Thread(target=display_loop, daemon=True).start()

    print(f"Listening on {args.host}:{args.port}   (Ctrl+C to stop)\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nbye")


if __name__ == "__main__":
    main()
