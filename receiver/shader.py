#!/usr/bin/env python3
"""OSC-driven shader for the ReBuzz -> video bridge.

Listens for Pedal OSC's feature frame on UDP 127.0.0.1:9000 and drives shader
uniforms: audio level sets the background, and BEAT PHASE drives a flash and a
sweeping bar marker -- i.e. visuals locked to the sequencer's grid, not merely
reacting to loudness. That grid-lock is the thing an external audio-listening
visualizer cannot do reliably; the bridge gets it for free by living in the host.

Run:   python shader.py
Deps:  pip install -r requirements.txt   (moderngl, pyglet, python-osc)

Keys:  + / -   gain            A  auto-gain
       S       smoothing       B  toggle beat visuals
       ESC     quit
"""

import argparse
import struct
import threading
import time

import moderngl
import pyglet
from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

# Newest value per address. Written by the OSC thread, read by the render loop.
# Last-writer-wins: the renderer undersamples the sender (~60 fps vs ~125 msg/s),
# so there is deliberately no queue.
_v = {"rms": 0.0, "beat": 0.0, "bar": 0.0, "bpm": 0.0, "playing": 0.0, "beatsperbar": 4.0}
_last_rx = 0.0


def make_handler(prefix):
    plen = len(prefix) + 1

    def on_msg(address, *args):
        global _last_rx
        if not args or not address.startswith(prefix):
            return
        key = address[plen:]
        if key in _v:
            try:
                _v[key] = float(args[0])
            except (TypeError, ValueError):
                return
            _last_rx = time.time()

    return on_msg


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

uniform float uLevel;       // audio level 0..1, post-gain
uniform float uBeat;        // beat phase 0..1  <- the grid-locked input
uniform float uBar;         // bar phase 0..1
uniform float uBeatsPerBar;
uniform float uAspect;
uniform float uStale;       // 1.0 when no packets recently
uniform float uUseBeat;     // 1.0 = show beat visuals

void main() {
    // Background: loudness.
    vec3 col = vec3(uLevel * 0.55) * vec3(1.00, 0.93, 0.84);

    if (uUseBeat > 0.5) {
        // Flash on the beat: sharp attack at phase 0, decaying across the beat.
        float flash = pow(1.0 - uBeat, 5.0);

        // Centre disc, sized by level, brightened by the flash.
        vec2 p = (uv - 0.5) * vec2(uAspect, 1.0);
        float r = length(p);
        float radius = 0.10 + 0.16 * uLevel;
        float disc = smoothstep(radius, radius - 0.012, r);
        col += disc * flash * vec3(1.00, 0.72, 0.35);

        // Ring expanding once per bar - a slow visual reference for the bar line.
        float ringR = 0.18 + uBar * 0.30;
        float ring = smoothstep(0.012, 0.0, abs(r - ringR)) * (1.0 - uBar) * 0.5;
        col += ring * vec3(0.35, 0.65, 1.00);

        // Beat ticks across the top: the current beat of the bar lights up.
        if (uv.y > 0.955) {
            float n = max(1.0, uBeatsPerBar);
            float slot = floor(uv.x * n);
            float cur = floor(uBar * n);
            float gap = abs(fract(uv.x * n) - 0.5);
            if (gap < 0.30) {
                col = (slot == cur) ? vec3(0.95, 0.75, 0.35) * (0.35 + 0.65 * flash)
                                    : vec3(0.10);
            }
        }
    }

    // Diagnostic strip: level bar; dark red when the stream has stopped.
    if (uv.y < 0.022) {
        col = (uv.x < uLevel) ? vec3(0.95, 0.55, 0.20) : vec3(0.07);
        if (uStale > 0.5) col = vec3(0.35, 0.06, 0.06);
    }

    fragColor = vec4(col, 1.0);
}
"""


def main():
    ap = argparse.ArgumentParser(description="OSC-driven shader for the ReBuzz video bridge.")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9000)
    ap.add_argument("--prefix", default="/rebuzz", help="OSC address prefix (default /rebuzz)")
    ap.add_argument("--gain", type=float, default=3.5,
                    help="initial gain (default 3.5; raw RMS peaks well below 1.0)")
    ap.add_argument("--width", type=int, default=900)
    ap.add_argument("--height", type=int, default=600)
    args = ap.parse_args()

    disp = Dispatcher()
    disp.set_default_handler(make_handler(args.prefix))
    server = ThreadingOSCUDPServer((args.host, args.port), disp)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"Listening on {args.host}:{args.port}")
    print("Keys:  +/- gain   A auto-gain   S smoothing   B beat visuals   ESC quit")

    # Request a 3.3 core context explicitly (the shaders are #version 330).
    config = pyglet.gl.Config(major_version=3, minor_version=3,
                              forward_compatible=True, double_buffer=True)
    window = pyglet.window.Window(width=args.width, height=args.height,
                                  caption="Pedal OSC", resizable=True, config=config)
    ctx = moderngl.create_context()
    prog = ctx.program(vertex_shader=VERT, fragment_shader=FRAG)

    quad = ctx.buffer(struct.pack("8f", -1, -1, 1, -1, -1, 1, 1, 1))
    vao = ctx.vertex_array(prog, [(quad, "2f", "in_pos")])

    state = {"gain": args.gain, "auto": False, "smooth": True,
             "beat": True, "shown": 0.0, "peak": 0.05}

    def render():
        now = time.time()
        raw = _v["rms"]
        stale = (_last_rx == 0.0) or (now - _last_rx > 1.0)

        if state["auto"]:
            state["peak"] = max(raw, state["peak"] * 0.9995, 0.02)
            level = raw * (0.92 / state["peak"])
        else:
            level = raw * state["gain"]
        level = 0.0 if stale else max(0.0, min(1.0, level))

        # Fast attack, slower release: transients stay sharp, the image does not flicker.
        if state["smooth"]:
            k = 0.55 if level > state["shown"] else 0.12
            state["shown"] += k * (level - state["shown"])
        else:
            state["shown"] = level

        ctx.viewport = (0, 0, window.width, window.height)
        ctx.clear(0.0, 0.0, 0.0)
        prog["uLevel"].value = state["shown"]
        prog["uBeat"].value = _v["beat"]
        prog["uBar"].value = _v["bar"]
        prog["uBeatsPerBar"].value = max(1.0, _v["beatsperbar"])
        prog["uAspect"].value = window.width / max(1, window.height)
        prog["uStale"].value = 1.0 if stale else 0.0
        prog["uUseBeat"].value = 1.0 if (state["beat"] and not stale) else 0.0
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
            state["gain"] = min(64.0, state["gain"] * 1.25)
            print(f"gain {state['gain']:.2f}")
        elif symbol in (key.MINUS, key.NUM_SUBTRACT):
            state["gain"] = max(0.25, state["gain"] / 1.25)
            print(f"gain {state['gain']:.2f}")
        elif symbol == key.A:
            state["auto"] = not state["auto"]
            print(f"auto-gain {'on' if state['auto'] else 'off'}")
        elif symbol == key.S:
            state["smooth"] = not state["smooth"]
            print(f"smoothing {'on' if state['smooth'] else 'off'}")
        elif symbol == key.B:
            state["beat"] = not state["beat"]
            print(f"beat visuals {'on' if state['beat'] else 'off'}")

    def tick(dt):
        window.invalid = True

    pyglet.clock.schedule_interval(tick, 1 / 60)

    try:
        pyglet.app.run()
    finally:
        server.shutdown()


if __name__ == "__main__":
    main()
