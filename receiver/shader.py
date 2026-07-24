#!/usr/bin/env python3
"""OSC-driven shader for the ReBuzz -> video bridge (schema v2).

Routes any value arriving on the wire to any visual target. Sources are
discovered from the stream at runtime - audio taps, transport, and every
exported machine parameter - so nothing needs naming on the command line.

  /rebuzz/song/beat, /bar    transport and grid (Pedal OSC Data)
  /rebuzz/tap/<name>/rms     per-instance audio level (Pedal OSC)
  /rebuzz/param/<m>/<p>      any machine parameter (Pedal OSC Data)

Six visual targets, each independently routable:

  BRIGHT  background brightness
  SIZE    centre disc radius
  HUE     colour
  RING    expanding ring radius
  WARP    radial ripple distortion
  FLASH   disc brightness pulse

Run:   python shader.py
Deps:  pip install -r requirements.txt   (moderngl, pyglet, python-osc)

Keys:  1-6      select a visual target
       [ ]      cycle that target's source (Shift+[ ] steps backwards)
       + -      that target's gain
       0        clear that target
       S        smoothing on/off
       L        list all discovered sources
       ESC      quit
"""

import argparse
import os
import struct
import threading
import time

import moderngl
import pyglet
from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

SONG = "/rebuzz/song/"
TAP = "/rebuzz/tap/"
PARAM = "/rebuzz/param/"

# Newest value per address plus a stable discovery order. Written by the OSC
# thread, read by the render loop. Last-writer-wins: the renderer undersamples
# the senders (~60 fps against ~125 msg/s each), so there is no queue.
_vals = {}
_sources = []       # routable addresses, in first-seen order
_last_rx = 0.0
_lock = threading.Lock()

# Addresses that are structural rather than routable signals.
_SKIP = ("/rebuzz/v", SONG + "bpm", SONG + "playing", SONG + "beatsperbar")


def on_msg(address, *args):
    global _last_rx
    if not args:
        return
    try:
        v = float(args[0])
    except (TypeError, ValueError):
        return
    with _lock:
        _vals[address] = v
        _last_rx = time.time()
        if address not in _SKIP and address not in _sources:
            _sources.append(address)


# Visual targets, in HUD order.
SLOTS = ["BRIGHT", "SIZE", "HUE", "RING", "WARP", "FLASH"]

# Sensible opening routing, applied as sources appear. Each entry is a
# predicate over the address; the first unrouted match wins.
# Each target lists predicates in PREFERENCE order; the first one that matches an
# available source wins. FLASH prefers an onset envelope (punchy, transient-driven)
# but falls back to beat phase when no onset stream is present, so the default
# visual always has a pulse.
DEFAULTS = {
    "BRIGHT": [lambda a: a.startswith(TAP) and a.endswith("/rms")],
    "SIZE":   [lambda a: a.startswith(TAP) and a.endswith("/rms")],
    "FLASH":  [lambda a: a.startswith(TAP) and a.endswith("/onset"),
               lambda a: a == SONG + "beat"],
    "RING":   [lambda a: a == SONG + "bar"],
    "HUE":    [lambda a: a.startswith(PARAM)],
    "WARP":   [lambda a: a.startswith(PARAM)],
}

# Tap levels are small (a loud mix reads ~0.25); everything else is already 0..1.
def default_gain(address):
    return 3.5 if address.startswith(TAP) else 1.0


VERT = """
#version 330
in vec2 in_pos;
out vec2 uv;
void main() {
    uv = in_pos * 0.5 + 0.5;
    gl_Position = vec4(in_pos, 0.0, 1.0);
}
"""

FRAG = """
#version 330
in vec2 uv;
out vec4 fragColor;

uniform float uBright;
uniform float uSize;
uniform float uHue;
uniform float uRing;
uniform float uWarp;
uniform float uFlash;

uniform float uBar;          // grid reference for the beat ticks
uniform float uBeatsPerBar;
uniform float uAspect;
uniform float uTime;
uniform float uStale;
uniform float uHaveSong;    // 1.0 when transport data is arriving
uniform int   uSelected;     // highlighted HUD row
uniform int   uActive;       // bitmask of routed slots

vec3 tint(float h) {
    return 0.5 + 0.5 * cos(6.28318 * (h + vec3(0.00, 0.33, 0.67)));
}

void main() {
    vec3 base = tint(uHue * 0.85 + 0.08);

    vec2 p = (uv - 0.5) * vec2(uAspect, 1.0);
    float r = length(p);

    // WARP: radial ripple. Displaces the radius every later feature reads,
    // so one source can visibly deform the whole composition.
    float rw = r + sin(r * 26.0 - uTime * 2.2) * uWarp * 0.05;

    // Background.
    vec3 col = vec3(uBright * 0.55) * base;

    // Centre disc: SIZE sets the radius, FLASH the brightness.
    float radius = 0.06 + 0.22 * uSize;
    float disc = smoothstep(radius, radius - 0.012, rw);
    col += disc * (0.15 + 0.85 * uFlash) * base;

    // Expanding ring.
    float ringR = 0.18 + uRing * 0.34;
    float ring = smoothstep(0.013, 0.0, abs(rw - ringR)) * (1.0 - uRing) * 0.65;
    col += ring * vec3(0.35, 0.65, 1.00);

    // Beat ticks across the top, straight off the bar phase. Dimmed when no
    // transport data is arriving, so a frozen grid reads as "no data" not "beat 1".
    if (uv.y > 0.955) {
        float n = max(1.0, uBeatsPerBar);
        float slot = floor(uv.x * n);
        float cur = floor(uBar * n);
        if (abs(fract(uv.x * n) - 0.5) < 0.30) {
            vec3 lit = base * (0.35 + 0.65 * uFlash);
            col = (slot == cur) ? lit : vec3(0.10);
            if (uHaveSong < 0.5) col *= 0.25;
        }
    }

    // ---- HUD: six value bars down the left edge -------------------------
    // No text (that would need font machinery); the console names the routing.
    float slots[6] = float[6](uBright, uSize, uHue, uRing, uWarp, uFlash);
    if (uv.x < 0.135 && uv.y > 0.70 && uv.y < 0.94) {
        float row = (0.94 - uv.y) / 0.04;        // 0..6 top to bottom
        int idx = int(floor(row));
        if (idx >= 0 && idx < 6) {
            float withinRow = fract(row);
            if (withinRow > 0.25 && withinRow < 0.85) {
                float x = (uv.x - 0.012) / 0.115;
                bool routed = (uActive & (1 << idx)) != 0;
                vec3 rowCol = (idx == uSelected) ? vec3(1.00, 0.85, 0.45)
                                                 : vec3(0.55, 0.60, 0.70);
                if (!routed) rowCol *= 0.30;
                if (x > 0.0 && x < 1.0) {
                    col = (x < slots[idx]) ? rowCol : vec3(0.10);
                    // Tick the selected row so it is findable at a glance.
                    if (idx == uSelected && x < 0.012) col = vec3(1.0, 0.85, 0.45);
                }
            }
        }
    }

    // Stale marker along the very bottom.
    if (uv.y < 0.012 && uStale > 0.5) col = vec3(0.35, 0.06, 0.06);

    fragColor = vec4(col, 1.0);
}
"""


def _prepare_console():
    """
    On Windows, clicking in the console starts a QuickEdit selection that BLOCKS
    stdout until dismissed. Since key handling prints from pyglet's event thread,
    that freezes the render window - it looks like a lockup but is just a blocked
    write. Turn QuickEdit off and enable ANSI handling.
    """
    if os.name != "nt":
        return
    os.system("")                     # enable ANSI escape processing
    try:
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.windll.kernel32
        STD_INPUT_HANDLE = -10
        ENABLE_QUICK_EDIT = 0x0040
        ENABLE_EXTENDED_FLAGS = 0x0080
        handle = kernel32.GetStdHandle(STD_INPUT_HANDLE)
        mode = wintypes.DWORD()
        if kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            new = (mode.value & ~ENABLE_QUICK_EDIT) | ENABLE_EXTENDED_FLAGS
            kernel32.SetConsoleMode(handle, new)
    except Exception:
        pass                          # not fatal - worst case QuickEdit stays on


def short(address):
    """Trim an address to something readable in the console."""
    if address.startswith(PARAM):
        return address[len(PARAM):]
    if address.startswith(TAP):
        return "tap " + address[len(TAP):]
    if address.startswith(SONG):
        return "song " + address[len(SONG):]
    return address


def main():
    ap = argparse.ArgumentParser(description="OSC-driven shader for the ReBuzz video bridge.")
    ap.add_argument("--host", default="0.0.0.0",
                    help="bind address; 0.0.0.0 receives on all interfaces (needed for LAN)")
    ap.add_argument("--port", type=int, default=9000)
    ap.add_argument("--width", type=int, default=980)
    ap.add_argument("--height", type=int, default=640)
    args = ap.parse_args()

    if os.name == "nt":
        _prepare_console()

    disp = Dispatcher()
    disp.set_default_handler(on_msg)
    server = ThreadingOSCUDPServer((args.host, args.port), disp)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"Listening on {args.host}:{args.port}")
    print("Keys:  1-6 select target   [ ] cycle source   +/- gain   0 clear")
    print("       S smoothing   L list sources   ESC quit\n")

    config = pyglet.gl.Config(major_version=3, minor_version=3,
                              forward_compatible=True, double_buffer=True)
    window = pyglet.window.Window(width=args.width, height=args.height,
                                  caption="Pedal OSC", resizable=True, config=config)
    ctx = moderngl.create_context()
    prog = ctx.program(vertex_shader=VERT, fragment_shader=FRAG)

    quad = ctx.buffer(struct.pack("8f", -1, -1, 1, -1, -1, 1, 1, 1))
    vao = ctx.vertex_array(prog, [(quad, "2f", "in_pos")])

    # Routing state: one entry per visual target.
    route = {s: {"src": None, "gain": 1.0, "shown": 0.0} for s in SLOTS}
    st = {"sel": 0, "smooth": True, "t0": time.time(), "seen": 0, "song_warned": False}

    def autoroute(sources):
        """Fill unrouted targets as sources appear, without disturbing manual choices."""
        for slot in SLOTS:
            if route[slot]["src"] is not None:
                continue
            preds = DEFAULTS.get(slot)
            if not preds:
                continue
            taken = {route[s]["src"] for s in SLOTS if route[s]["src"]}
            chosen = None
            for pred in preds:                 # preference order
                for a in sources:
                    # Allow BRIGHT and SIZE to share one tap; otherwise prefer unused.
                    if pred(a) and (a not in taken or slot in ("SIZE",)):
                        chosen = a
                        break
                if chosen:
                    break
            if chosen:
                route[slot]["src"] = chosen
                route[slot]["gain"] = default_gain(chosen)
                print(f"  {slot:6s} <- {short(chosen)}")

    def render():
        now = time.time()
        with _lock:
            vals = dict(_vals)
            sources = list(_sources)
            last_rx = _last_rx

        if len(sources) != st["seen"]:
            if st["seen"] == 0:
                print("routing:")
            st["seen"] = len(sources)
            autoroute(sources)

        # If nothing on /rebuzz/song has arrived a few seconds in, the beat grid is
        # frozen at bar 0 and looks broken. Say so once, rather than silently.
        have_song = any(k.startswith(SONG) for k in vals)
        if (not have_song and not st["song_warned"]
                and (now - st["t0"]) > 4.0 and vals):
            print("note: no /rebuzz/song data - is Pedal OSC Data in the song? "
                  "beat visuals will stay frozen without it.")
            st["song_warned"] = True

        stale = (last_rx == 0.0) or (now - last_rx > 1.0)

        active = 0
        for i, slot in enumerate(SLOTS):
            r = route[slot]
            if r["src"] is None:
                target = 0.0
            else:
                active |= (1 << i)
                target = vals.get(r["src"], 0.0) * r["gain"]
                target = max(0.0, min(1.0, 0.0 if stale else target))
            if st["smooth"]:
                k = 0.55 if target > r["shown"] else 0.14
                r["shown"] += k * (target - r["shown"])
            else:
                r["shown"] = target

        ctx.viewport = (0, 0, window.width, window.height)
        ctx.clear(0.0, 0.0, 0.0)
        prog["uBright"].value = route["BRIGHT"]["shown"]
        prog["uSize"].value = route["SIZE"]["shown"]
        prog["uHue"].value = route["HUE"]["shown"]
        prog["uRing"].value = route["RING"]["shown"]
        prog["uWarp"].value = route["WARP"]["shown"]
        prog["uFlash"].value = route["FLASH"]["shown"]
        prog["uBar"].value = vals.get(SONG + "bar", 0.0)
        prog["uBeatsPerBar"].value = max(1.0, vals.get(SONG + "beatsperbar", 4.0))
        prog["uAspect"].value = window.width / max(1, window.height)
        prog["uTime"].value = now - st["t0"]
        prog["uStale"].value = 1.0 if stale else 0.0
        prog["uHaveSong"].value = 1.0 if have_song else 0.0
        prog["uSelected"].value = st["sel"]
        prog["uActive"].value = active
        vao.render(moderngl.TRIANGLE_STRIP)

    def cycle(step):
        with _lock:
            sources = list(_sources)
        if not sources:
            print("no sources discovered yet - are the machines in the song?")
            return
        slot = SLOTS[st["sel"]]
        cur = route[slot]["src"]
        i = sources.index(cur) if cur in sources else -1
        nxt = sources[(i + step) % len(sources)]
        route[slot]["src"] = nxt
        route[slot]["gain"] = default_gain(nxt)
        print(f"{slot:6s} <- {short(nxt)}")

    @window.event
    def on_draw():
        render()

    @window.event
    def on_key_press(symbol, modifiers):
        # Any exception escaping here can leave the window unresponsive with no
        # explanation, so report it and carry on rather than dying silently.
        try:
            _handle_key(symbol, modifiers)
        except Exception:
            import traceback
            traceback.print_exc()

    def _handle_key(symbol, modifiers):
        key = pyglet.window.key
        shift = modifiers & key.MOD_SHIFT

        if symbol == key.ESCAPE:
            pyglet.app.exit()
        elif key._1 <= symbol <= key._6:
            st["sel"] = symbol - key._1
            slot = SLOTS[st["sel"]]
            src = route[slot]["src"]
            print(f"[{slot}] {short(src) if src else '(unrouted)'}"
                  f"   gain {route[slot]['gain']:.2f}")
        elif symbol == key.BRACKETRIGHT:
            cycle(1)
        elif symbol == key.BRACKETLEFT:
            cycle(-1)
        elif symbol in (key.PLUS, key.EQUAL, key.NUM_ADD):
            slot = SLOTS[st["sel"]]
            route[slot]["gain"] = min(64.0, route[slot]["gain"] * 1.25)
            print(f"{slot:6s} gain {route[slot]['gain']:.2f}")
        elif symbol in (key.MINUS, key.NUM_SUBTRACT):
            slot = SLOTS[st["sel"]]
            route[slot]["gain"] = max(0.05, route[slot]["gain"] / 1.25)
            print(f"{slot:6s} gain {route[slot]['gain']:.2f}")
        elif symbol == key._0:
            slot = SLOTS[st["sel"]]
            route[slot]["src"] = None
            print(f"{slot:6s} cleared")
        elif symbol == key.S:
            st["smooth"] = not st["smooth"]
            print(f"smoothing {'on' if st['smooth'] else 'off'}")
        elif symbol == key.L:
            with _lock:
                sources = list(_sources)
            print(f"\n{len(sources)} sources:")
            for a in sources:
                used = [s for s in SLOTS if route[s]["src"] == a]
                mark = ("  -> " + ", ".join(used)) if used else ""
                print(f"  {short(a)}{mark}")
            print()

    def tick(dt):
        window.invalid = True

    pyglet.clock.schedule_interval(tick, 1 / 60)

    try:
        pyglet.app.run()
    finally:
        server.shutdown()


if __name__ == "__main__":
    main()
