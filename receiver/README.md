# Pedal OSC — receiver

The external side of the ReBuzz → video bridge. Both tools listen for Pedal
OSC's feature frame on UDP `127.0.0.1:9000`.

```bash
pip install -r requirements.txt
```

## Wire format

One OSC **bundle** per send (~125/sec), so every value comes from the same audio
block. Bundling keeps per-value addresses (which off-the-shelf OSC tools map to
channels natively) while staying atomic.

| Address                | Value                                    |
|------------------------|------------------------------------------|
| `/rebuzz/v`            | schema version (currently `1`)           |
| `/rebuzz/rms`          | smoothed level, 0..1                     |
| `/rebuzz/peak`         | block peak, 0..1                         |
| `/rebuzz/beat`         | **phase within the beat, 0..1**          |
| `/rebuzz/bar`          | phase within the bar, 0..1               |
| `/rebuzz/bpm`          | tempo                                    |
| `/rebuzz/playing`      | 1 = transport running, 0 = stopped       |
| `/rebuzz/beatsperbar`  | from the machine's Beats/Bar parameter   |

## `printer.py` — diagnostic

Live readout: level meter, a beat-phase marker that sweeps once per beat, BPM,
transport state, message rate, and running min/max of the level (sanity-checks
the machine's `SampleScale`).

```bash
python printer.py
```

- `lvl [####----] 0.312  beat [..O.....]  126.0bpm  PLAY  124.8/s  min 0.010 max 0.258` — working.
- `waiting for OSC ...` — nothing has arrived yet.
- `no packets for Ns` — machine not inserted / no song playing / wrong port.

## `shader.py` — the renderer

Level drives the background; **beat phase** drives a flash, a centre disc, a
per-bar expanding ring, and beat ticks along the top. That is the payoff of
tapping inside the host: the visuals are locked to the sequencer's grid rather
than inferring tempo from a waveform.

```bash
python shader.py
```

| Key     | Action                                                  |
|---------|---------------------------------------------------------|
| `+` `-` | adjust gain                                             |
| `A`     | auto-gain (normalises against a decaying peak)          |
| `S`     | visual smoothing (fast attack, slow release)            |
| `B`     | toggle beat visuals (compare grid-lock vs loudness-only)|
| `ESC`   | quit                                                    |

**Gain matters.** Pedal OSC sends raw RMS, which peaks well below 1.0 — a
normally-loud mix reads ~0.25. The default gain of **3.5** maps that to ~0.90 on
screen. Tune with `+`/`-`, or press `A`.

`B` is the interesting one: toggle it while a song plays to see the difference
between merely reacting to loudness and being locked to the grid.

## Notes

- The OSC server runs on its own thread and keeps only the **newest** value
  (last-writer-wins). The render loop reads once per frame; it undersamples the
  sender (~60 fps vs ~125 msg/s), which is expected — there is deliberately no
  queue.
- The C# encoder's message *and* bundle byte layouts have been verified against
  this stack, so silence here means the problem is upstream in ReBuzz.
- `shader.py` requests an OpenGL 3.3 core context explicitly (`#version 330`).
