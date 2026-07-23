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

- **Audio level** drives background brightness and the centre disc's size.
- **Beat phase** drives the flash, the beat ticks along the top, and the
  per-bar expanding ring — locked to the sequencer grid, not inferred.
- **A machine parameter** drives the colour and stretches the ring radius.

Taps and parameters are **discovered from the stream**, so nothing needs naming
on the command line — cycle through whatever is publishing:

| Key | Action |
|---|---|
| `+` `-` | gain |
| `A` | auto-gain |
| `S` | visual smoothing (fast attack, slow release) |
| `B` | beat visuals on/off — compare grid-lock against loudness-only |
| `T` | next audio tap |
| `P` | next machine parameter |
| `ESC` | quit |

The selected tap and parameter are printed to the console as you cycle.

**Gain matters.** Raw RMS peaks well below 1.0 — a normally-loud mix reads
~0.25 — so the default gain is 3.5. Tune with `+`/`-`, or press `A`.

Try it with a filter sweep: export a synth's cutoff from Pedal OSC Data, press
`P` until it is selected, and sweep the parameter in ReBuzz. The colour follows
what you sequence.

## Notes

- Values are latched last-writer-wins and read once per frame; the renderer
  undersamples the senders (~60 fps against ~125 msg/s each), which is expected.
- `shader.py` requests an OpenGL 3.3 core context (`#version 330`).
