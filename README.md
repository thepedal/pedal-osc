# Pedal OSC

A ReBuzz managed **effect** machine that taps the audio passing through it,
measures its level, and streams that value out over **OSC/UDP** to an external
program — for driving audio-reactive visuals (shaders, VJ tools, lighting) from
inside ReBuzz.

It is **Phase 1 of a ReBuzz → real-time video bridge**: numbers leave the host
over OSC and drive shader uniforms in a separate OpenGL renderer.

## What it does

- Passes audio through unchanged (insert it anywhere; conventionally just before
  **Master** to tap the full mix, or on a bus to tap that bus).
- Measures per-block RMS and peak, applies **Sensitivity** and **Smooth**, and
  sends them under this instance's own name.
- Runs a 2048-point FFT and publishes log-spaced band energies. **The transform
  runs on the sender thread, not the audio thread** — `Work()` only copies mono
  samples into a ring buffer, so band analysis costs the audio thread nothing.
- All network I/O runs on a background sender thread — **never** on the audio
  thread.

## OSC output

Schema **v2**, one OSC bundle per send (~125/sec) to `127.0.0.1:9000`:

| Address | Value |
|---|---|
| `/rebuzz/v` | schema version (`2`) |
| `/rebuzz/tap/<name>/rms` | smoothed level, 0..1 |
| `/rebuzz/tap/<name>/peak` | block peak, 0..1 |
| `/rebuzz/tap/<name>/band0` … `band7` | log-spaced FFT band energies, 0..1 |

Bands are log-spaced from 40 Hz to 16 kHz — roughly `band0` kick, `band1` bass,
`band4` mids, `band7` air — so a kick and a hi-hat can drive different visual
elements from a single tap. Energy is summed rather than averaged across each
band, which keeps the response flat: a full-scale tone reads ~1.0 wherever in
the spectrum it sits.

`<name>` is this instance's own machine name, lowercased and folded to
`[a-z0-9_]`, so several taps on different busses never collide. Rename the
machine in ReBuzz and the addresses follow.

Host and port are compile-time constants in `PedalOsc.cs`.

**Transport, tempo, beat phase and machine parameters come from the companion
control machine,** [pedal-osc-data](https://github.com/thepedal/pedal-osc-data),
under `/rebuzz/song/...` and `/rebuzz/param/...`. The two machines are
independent — neither needs the other at runtime — and both send to the same
endpoint.

## Parameters

- **Sensitivity** (0–127, default 64 = ×1.0) — scales levels before sending.
- **Smooth** (0–127, default 0 = raw) — one-pole smoothing on sent values.
- **Bands** (0–8, default 8) — how many FFT bands to publish. `0` disables the
  transform entirely.
- **Dst IP 1–4** (0–254, default 0) — destination IP as four octets. All zero =
  loopback (`127.0.0.1`), the zero-config default. Set them to reach another
  machine on the LAN, e.g. `192 168 1 50`.
- **Port +** (0–127, default 0) — destination port as an offset from 9000, so
  `13` sends to `9013`. Lets several receivers run on one box.

The destination is parameter-driven so it **saves and restores with the song**,
and is editable live — moving an octet re-points the socket within one send.
(Octets cap at 254 because 255 is the Byte parameter's "no value" sentinel; a
full arbitrary host/port would need a GUI, deferred.)

## Build & deploy

Requires the .NET 10 SDK and a ReBuzz install providing `ReBuzz.dll` +
`BuzzGUI.Interfaces.dll`.

```powershell
dotnet build .\pedal-osc.csproj -c Release
```

The build writes `Pedal OSC.NET.dll` straight into `%BuzzDir%\Gear\Effects`
(default `C:\Program Files\ReBuzz`; override with `-p:BuzzDir=...`). Restart
ReBuzz to pick up a newly added machine.

## Status

Working. Roadmap: MIDI note events, runtime wire configuration, and OSC over LAN
to a separate render machine.

## Licence

GPL-3.0 (house default). See `LICENSE`.
