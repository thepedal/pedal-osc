#!/usr/bin/env python3
"""OSC-driven shader for the ReBuzz -> video bridge (schema v2).

Consumes the merged stream from both machines and drives shader uniforms:

  /rebuzz/song/beat, /bar    -> grid-locked flash, ring and beat ticks
  /rebuzz/tap/<name>/rms     -> brightness (Pedal OSC, the audio tap)
  /rebuzz/param/<m>/<p>      -> colour shift (Pedal OSC Data, any machine parameter)

Taps and parameters are discovered from the stream at runtime; cycle through
them with T and P rather than naming them on the command line.

Run:   python shader.py
Deps:  pip install -r requirements.txt   (moderngl, pyglet, python-osc)

Keys:  +/- gain    A auto-gain    S smoothing    B beat visuals
       T next tap  P next parameter            ESC quit
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

# Newest value per address, plus discovery sets. Written by the OSC thread, read by
# the render loop. Last-writer-wins: the renderer undersamples the senders (~60 fps
# against ~125 msg/s each), so there is deliberately no queue.
_vals = {}
_taps = []          # discovered /rebuzz/tap/<name> prefixes, in first-seen order
_params = []        # discovered /rebuzz/param/... addresses, in first-seen order
_last_rx = 0.0
_lock = threading.Lock()

SONG = "/rebuzz/song/"
TAP = "/rebuzz/tap/"
PARAM = "/rebuzz/param/"


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

        if address.startswith(TAP):
            # /rebuzz/tap/<name>/rms  ->  remember the <name> prefix once
            prefix = address.rsplit("/", 1)[0]
            if prefix not in _taps:
                _taps.append(prefix)
        elif address.startswith(PARAM):
            if address not in _params:
                _params.append(address)


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

uniform float uLevel;        // audio level 0..1, post-gain
uniform float uBeat;         // beat phase 0..1  <- grid-locked
uniform float uBar;          // bar phase 0..1
uniform float uBeatsPerBar;
uniform float uParam;        // any exported machine parameter, 0..1
uniform float uHasParam;     // 1.0 when a parameter is mapped
uniform float uAspect;
uniform float uStale;
uniform float uUseBeat;

// Cheap hue rotation so the mapped parameter is unmistakable on screen.
vec3 tint(float h) {
    return 0.5 + 0.5 * cos(6.28318 * (h + vec3(0.00, 0.33, 0.67)));
}

void main() {
    vec3 base = (uHasParam > 0.5) ? tint(uParam * 0.85) : vec3(1.00, 0.93, 0.84);

    // Background: loudness.
    vec3 col = vec3(uLevel * 0.55) * base;

    if (uUseBeat > 0.5) {
        // Sharp attack at phase 0, decaying across the beat.
        float flash = pow(1.0 - uBeat, 5.0);

        vec2 p = (uv - 0.5) * vec2(uAspect, 1.0);
        float r = length(p);

        // Centre disc: size follows level, brightness follows the beat.
        float radius = 0.10 + 0.16 * uLevel;
        float disc = smoothstep(radius, radius - 0.012, r);
        col += disc * flash * base;

        // One expanding ring per bar. Its radius also stretches with the parameter,
        // so a slow filter sweep visibly reshapes the geometry.
        float ringR = 0.18 + uBar * (0.26 + 0.18 * uParam * uHasParam);
        float ring = smoothstep(0.012, 0.0, abs(r - ringR)) * (1.0 - uBar) * 0.6;
        col += ring * vec3(0.35, 0.65, 1.00);

        // Beat ticks across the top; the current beat of the bar lights up.
        if (uv.y > 0.955) {
            float n = max(1.0, uBeatsPerBar);
            float slot = floor(uv.x * n);
            float cur = floor(uBar * n);
            if (abs(fract(uv.x * n) - 0.5) < 0.30) {
                col = (slot == cur) ? base * (0.35 + 0.65 * flash) : vec3(0.10);
            }
        }
    }

    // Diagnostic strips: level along the bottom, mapped parameter just above it.
    if (uv.y < 0.022) {
        col = (uv.x < uLevel) ? vec3(0.95, 0.55, 0.20) : vec3(0.07);
        if (uStale > 0.5) col = vec3(0.35, 0.06, 0.06);
    } else if (uHasParam > 0.5 && uv.y < 0.040) {
        col = (uv.x < uParam) ? base * 0.9 : vec3(0.05);
    }

    fragColor = vec4(col, 1.0);
}
"""


def main():
    ap = argparse.ArgumentParser(description="OSC-driven shader for the ReBuzz video bridge.")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9000)
    ap.add_argument("--gain", type=float, default=3.5,
                    help="initial gain (default 3.5; raw RMS peaks well below 1.0)")
    ap.add_argument("--width", type=int, default=900)
    ap.add_argument("--height", type=int, default=600)
    args = ap.parse_args()

    if os.name == "nt":
        os.system("")   # enable ANSI handling for console messages

    disp = Dispatcher()
    disp.set_default_handler(on_msg)
    server = ThreadingOSCUDPServer((args.host, args.port), disp)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"Listening on {args.host}:{args.port}")
    print("Keys:  +/- gain   A auto-gain   S smoothing   B beat   T tap   P param   ESC quit")

    config = pyglet.gl.Config(major_version=3, minor_version=3,
                              forward_compatible=True, double_buffer=True)
    window = pyglet.window.Window(width=args.width, height=args.height,
                                  caption="Pedal OSC", resizable=True, config=config)
    ctx = moderngl.create_context()
    prog = ctx.program(vertex_shader=VERT, fragment_shader=FRAG)

    quad = ctx.buffer(struct.pack("8f", -1, -1, 1, -1, -1, 1, 1, 1))
    vao = ctx.vertex_array(prog, [(quad, "2f", "in_pos")])

    st = {"gain": args.gain, "auto": False, "smooth": True, "beat": True,
          "shown": 0.0, "peak": 0.05, "tap": 0, "param": 0}

    def render():
        now = time.time()
        with _lock:
            vals = dict(_vals)
            taps = list(_taps)
            params = list(_params)
            last_rx = _last_rx

        stale = (last_rx == 0.0) or (now - last_rx > 1.0)

        # Level from the selected tap, if any tap is publishing.
        raw = 0.0
        if taps:
            raw = vals.get(taps[st["tap"] % len(taps)] + "/rms", 0.0)

        if st["auto"]:
            st["peak"] = max(raw, st["peak"] * 0.9995, 0.02)
            level = raw * (0.92 / st["peak"])
        else:
            level = raw * st["gain"]
        level = 0.0 if stale else max(0.0, min(1.0, level))

        # Fast attack, slower release: transients stay sharp, the image does not flicker.
        if st["smooth"]:
            k = 0.55 if level > st["shown"] else 0.12
            st["shown"] += k * (level - st["shown"])
        else:
            st["shown"] = level

        pval, has_param = 0.0, 0.0
        if params:
            pval = vals.get(params[st["param"] % len(params)], 0.0)
            has_param = 1.0

        ctx.viewport = (0, 0, window.width, window.height)
        ctx.clear(0.0, 0.0, 0.0)
        prog["uLevel"].value = st["shown"]
        prog["uBeat"].value = vals.get(SONG + "beat", 0.0)
        prog["uBar"].value = vals.get(SONG + "bar", 0.0)
        prog["uBeatsPerBar"].value = max(1.0, vals.get(SONG + "beatsperbar", 4.0))
        prog["uParam"].value = pval
        prog["uHasParam"].value = has_param
        prog["uAspect"].value = window.width / max(1, window.height)
        prog["uStale"].value = 1.0 if stale else 0.0
        prog["uUseBeat"].value = 1.0 if (st["beat"] and not stale) else 0.0
        vao.render(moderngl.TRIANGLE_STRIP)

    @window.event
    def on_draw():
        render()

    @window.event
    def on_key_press(symbol, modifiers):
        key = pyglet.window.key
        if symbol == key.ESCAPE:
            pyglet.app.exit()
        elif symbol in (key.PLUS, key.EQUAL, key.NUM_ADD):
            st["gain"] = min(64.0, st["gain"] * 1.25)
            print(f"gain {st['gain']:.2f}")
        elif symbol in (key.MINUS, key.NUM_SUBTRACT):
            st["gain"] = max(0.25, st["gain"] / 1.25)
            print(f"gain {st['gain']:.2f}")
        elif symbol == key.A:
            st["auto"] = not st["auto"]
            print(f"auto-gain {'on' if st['auto'] else 'off'}")
        elif symbol == key.S:
            st["smooth"] = not st["smooth"]
            print(f"smoothing {'on' if st['smooth'] else 'off'}")
        elif symbol == key.B:
            st["beat"] = not st["beat"]
            print(f"beat visuals {'on' if st['beat'] else 'off'}")
        elif symbol == key.T:
            with _lock:
                taps = list(_taps)
            if taps:
                st["tap"] = (st["tap"] + 1) % len(taps)
                print(f"tap -> {taps[st['tap']]}")
            else:
                print("no taps seen yet (is Pedal OSC in the song?)")
        elif symbol == key.P:
            with _lock:
                params = list(_params)
            if params:
                st["param"] = (st["param"] + 1) % len(params)
                print(f"param -> {params[st['param']]}")
            else:
                print("no parameters seen yet (set Machine and Params on Pedal OSC Data)")

    def tick(dt):
        window.invalid = True

    pyglet.clock.schedule_interval(tick, 1 / 60)

    try:
        pyglet.app.run()
    finally:
        server.shutdown()


if __name__ == "__main__":
    main()
