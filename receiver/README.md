# Pedal OSC — receiver

Consumes the schema-**v2** stream from both bridge machines on UDP
`127.0.0.1:9000`. Both send to the same endpoint; the receiver merges by address.

```bash
pip install -r requirements.txt
```

## Addresses

| Address | Source | Meaning |
|---|---|---|
| `/rebuzz/v` | both | schema version (`2`) |
| `/rebuzz/song/beat` `/bar` `/bpm` `/playing` `/beatsperbar` | Pedal OSC Data | transport and grid |
| `/rebuzz/tap/<name>/rms` `/peak` | Pedal OSC | per-instance audio level |
| `/rebuzz/param/<machine>/<param>` | Pedal OSC Data | any machine parameter, 0..1 |

Tap addresses are namespaced by the effect's own machine name, so several taps
on different busses coexist.

## `oscdump.py` — diagnostic

Lists every address arriving with live values. The right tool when addresses are
dynamic, which they are as soon as parameters are exported.

```bash
python oscdump.py
```

## `shader.py` — the renderer

```bash
python shader.py
```

Routes **any** value on the wire to **any** visual target. Sources are
discovered from the stream, so nothing is named on the command line.

| Target | Effect |
|---|---|
| `BRIGHT` | background brightness |
| `SIZE` | centre disc radius |
| `HUE` | colour |
| `RING` | expanding ring radius |
| `WARP` | radial ripple distortion |
| `FLASH` | disc brightness pulse |

On startup it auto-routes something sensible — audio level to brightness and
size, beat to flash, bar to ring, and the first two exported parameters to hue
and warp — then prints the routing. Re-route anything from there:

| Key | Action |
|---|---|
| `1`–`6` | select a target (shows its source and gain) |
| `[` `]` | cycle that target's source through everything discovered |
| `+` `-` | that target's gain |
| `0` | clear the target |
| `S` | smoothing on/off (fast attack, slow release) |
| `L` | list all discovered sources and what they drive |
| `ESC` | quit |

Six bars down the left edge show each target's live value; the selected one is
highlighted, unrouted ones are dimmed. Names go to the console rather than the
window — no font machinery in the GL path.

Gains default per source type: audio taps get ×3.5 (raw RMS peaks well below
1.0 — a normally-loud mix reads ~0.25), everything else ×1.0 since it is
already normalised.

Try routing a filter cutoff to `WARP` and a resonance to `HUE`, then sequence
both in the pattern editor.

## Notes

- Values are latched last-writer-wins and read once per frame; the renderer
  undersamples the senders (~60 fps against ~125 msg/s each), which is expected.
- `shader.py` requests an OpenGL 3.3 core context (`#version 330`).
